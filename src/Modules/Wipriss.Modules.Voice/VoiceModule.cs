using System.Windows;
using System.Windows.Threading;
using Wipriss.Core;
using Wipriss.Core.Modules;
using Wipriss.Voice;

namespace Wipriss.Modules.Voice;

/// <summary>
/// Voice typing. The hotkey opens the microphone, the second press closes it, and the recognized text is
/// pasted where the cursor is. Speech is recognized offline by Wipriss.Voice.exe; while dictation is off
/// nothing runs at all (no hook, no process). Without a model the hotkey offers to download one.
/// </summary>
public sealed class VoiceModule : IModule
{
    private ModelChooserWindow? _chooser;

    private enum State { Idle, Starting, Listening, Recognizing }

    private IModuleHost? _host;
    private Dispatcher? _dispatcher;
    private HotkeyHook? _hook;
    private VoiceEngine? _engine;
    private VoicePill? _pill;
    private State _state;
    private Action<List<int>?>? _captureDone;

    public ModuleInfo Info { get; } = ModuleCatalog.Get("dictation");

    public Size PanelSize => new(440, 560);

    internal VoiceSettings Settings { get; private set; } = new();

    internal ModelStore Model => ModelStore.For(VoiceEngine.ModelsDir, CurrentModel);

    internal VoiceModel CurrentModel => VoiceModels.Get(Settings.Model);

    /// <summary>Raised when a model is downloaded, chosen or removed.</summary>
    internal event Action? ModelsChanged;

    internal static bool IsInstalled(VoiceModel model) => ModelStore.For(VoiceEngine.ModelsDir, model).LooksInstalled();

    internal bool ModelReady { get; private set; }

    /// <summary>True while the model download runs (the panel shows it again when reopened).</summary>
    internal DownloadJob? Download { get; private set; }

    internal bool HookActive => _hook?.Active == true;

    /// <summary>The engine is loaded right now (for the panel's memory line).</summary>
    internal bool EngineRunning => _engine is not null;

    public string Summary =>
        !ModelReady ? "нужна модель"
        : !Settings.Enabled ? "выключен"
        : HotkeyKeys.Describe(Settings.Keys);

    public void Start(IModuleHost host)
    {
        _host = host;
        _dispatcher = host.Dispatcher;
        Settings = VoiceSettings.Load();
        ModelReady = Model.LooksInstalled();
        Apply();
    }

    public void Stop()
    {
        Download?.Cancel();
        ReleaseHook();
        CloseEngine();
        _pill?.Close();
        _pill = null;
        _chooser?.Close();
    }

    public FrameworkElement CreatePanel(IPanelContext panel) => new VoicePanel(this, panel);

    /// <summary>Turns the hook and the engine on or off to match the settings and the model.</summary>
    internal void Apply()
    {
        bool on = Settings.Enabled;
        if (!on)
        {
            ReleaseHook();
            CloseEngine();
            return;
        }
        if (_hook is null)
        {
            _hook = new HotkeyHook();
            _hook.Matcher.Pressed += () => _dispatcher?.BeginInvoke(OnHotkey);
            _hook.Matcher.Cancelled += () => _dispatcher?.BeginInvoke(OnCancel);
            _hook.Matcher.Captured += keys => _dispatcher?.BeginInvoke(() => OnCaptured(keys));
        }
        _hook.Matcher.Keys = Settings.Keys.ToArray();

        if (!ModelReady) { CloseEngine(); return; } // the hotkey shows the model choice
        if (Settings.KeepLoaded) EnsureEngine();
        else _engine?.Send(LoadRequest()); // a running engine takes the new unload time
    }

    internal void SaveSettings()
    {
        Settings.Save();
        Apply();
    }

    /// <summary>The panel's "Изменить": the next 1–3 keys pressed together become the hotkey.</summary>
    internal bool BeginCapture(Action<List<int>?> done)
    {
        if (_hook is null) return false;
        _captureDone = done;
        _hook.Matcher.BeginCapture();
        return true;
    }

    /// <summary>The panel closed while waiting for keys: nothing is changed and keys reach programs again.</summary>
    internal void CancelCapture()
    {
        _hook?.Matcher.CancelCapture();
        _captureDone = null;
    }

    /// <summary>Makes a downloaded model the one in use. The engine restarts with it on the next phrase.</summary>
    internal void SelectModel(VoiceModel model)
    {
        if (Settings.Model == model.Id) return;
        Settings.Model = model.Id;
        Settings.Save();
        CloseEngine();
        ModelReady = Model.LooksInstalled();
        Apply();
        ModelsChanged?.Invoke();
    }

    /// <summary>Removes a model that is not in use (its own folder inside the models folder).</summary>
    internal bool DeleteModel(VoiceModel model)
    {
        if (model.Id == Settings.Model || Download?.Model == model.Id) return false;
        var folder = ModelStore.For(VoiceEngine.ModelsDir, model).Folder;
        try
        {
            if (System.IO.Directory.Exists(folder)) System.IO.Directory.Delete(folder, recursive: true);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            ErrorLog.Write(ex);
            return false;
        }
        ModelsChanged?.Invoke();
        return true;
    }

    internal async Task<VoiceError> DownloadModelAsync(DownloadJob job)
    {
        Download = job;
        ModelsChanged?.Invoke();
        try
        {
            var result = await VoiceEngine.DownloadAsync(VoiceEngine.ModelsDir, job.Model, job.Report, job.Token);
            if (job.Token.IsCancellationRequested) return VoiceError.None;
            var model = VoiceModels.Get(job.Model);
            // The first model downloaded, or the one picked in the "no model" window, becomes the one in use.
            if (result == VoiceError.None && IsInstalled(model) && (!ModelReady || job.Use))
            {
                Settings.Model = model.Id;
                Settings.Save();
                CloseEngine();
            }
            ModelReady = Model.LooksInstalled();
            if (result == VoiceError.None && ModelReady) Apply();
            return result;
        }
        catch (Exception ex) when (ex is System.IO.IOException or System.ComponentModel.Win32Exception)
        {
            ErrorLog.Write(ex);
            return VoiceError.EngineFailed;
        }
        finally
        {
            Download = null;
            ModelsChanged?.Invoke();
        }
    }

    private void OnCaptured(List<int>? keys)
    {
        var done = _captureDone;
        _captureDone = null;
        if (keys is not null && HotkeyKeys.IsValid(keys))
        {
            Settings.Keys = keys;
            SaveSettings();
        }
        done?.Invoke(keys);
    }

    private void OnHotkey()
    {
        switch (_state)
        {
            case State.Idle:
                if (!ModelReady)
                {
                    ShowChooser();
                    return;
                }
                if (!EnsureEngine()) return;
                _state = State.Starting;
                Pill().ShowWaiting();
                _engine!.Send(new VoiceRequest { Command = VoiceCommand.Start });
                break;
            case State.Starting:
            case State.Listening:
                _state = State.Recognizing;
                Pill().ShowRecognizing();
                _engine?.Send(new VoiceRequest { Command = VoiceCommand.Stop });
                SetListening(false);
                break;
            case State.Recognizing:
                break; // the phrase is almost there
        }
    }

    private void OnCancel()
    {
        if (_state is not (State.Starting or State.Listening)) return;
        _engine?.Send(new VoiceRequest { Command = VoiceCommand.Cancel });
        SetListening(false);
        _state = State.Idle;
        _pill?.FadeOut();
    }

    private bool EnsureEngine()
    {
        if (_engine is not null) return true;
        try
        {
            _engine = VoiceEngine.Start(_dispatcher!);
        }
        catch (Exception ex) when (ex is System.IO.IOException or System.ComponentModel.Win32Exception)
        {
            ErrorLog.Write(ex);
            Pill().ShowMessage("Голосовой ввод не запустился");
            return false;
        }
        _engine.Reply += OnReply;
        _engine.Exited += OnExited;
        _engine.Send(LoadRequest());
        return true;
    }

    private VoiceRequest LoadRequest() => new()
    {
        Command = VoiceCommand.Load,
        ModelsDir = VoiceEngine.ModelsDir,
        Model = Settings.Model,
        IdleMinutes = Settings.KeepLoaded ? 0 : Settings.UnloadMinutes,
    };

    private void OnReply(VoiceReply reply)
    {
        switch (reply.Kind)
        {
            case VoiceReplyKind.Listening:
                if (_state != State.Starting) break;
                _state = State.Listening;
                SetListening(true);
                Pill().ShowListening();
                break;
            case VoiceReplyKind.Level:
                if (_state == State.Listening) _pill?.SetLevel(reply.Percent);
                break;
            case VoiceReplyKind.Recognizing:
                if (_state == State.Listening) // the 5-minute limit stopped it
                {
                    _state = State.Recognizing;
                    SetListening(false);
                    Pill().ShowRecognizing();
                }
                break;
            case VoiceReplyKind.Text:
                if (_state != State.Recognizing) break;
                _state = State.Idle;
                _ = Deliver(reply.Text ?? "");
                break;
            case VoiceReplyKind.Error:
                OnError(reply.Error);
                break;
        }
    }

    private async Task Deliver(string text)
    {
        if (text.Length == 0)
        {
            _pill?.FadeOut();
            return;
        }
        _pill?.FadeOut();
        // A following phrase should not stick to this one.
        if (".!?,;:…".Contains(text[^1])) text += " ";
        bool pasted;
        try { pasted = await TextInserter.InsertAsync(text); }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.ExternalException or InvalidOperationException)
        {
            ErrorLog.Write(ex);
            pasted = false;
        }
        if (!pasted) Pill().ShowMessage("Буфер обмена занят, текст не вставлен");
    }

    private void OnError(VoiceError error)
    {
        bool dictating = _state != State.Idle;
        _state = State.Idle;
        SetListening(false);
        if (error is VoiceError.ModelMissing or VoiceError.ModelDamaged)
        {
            ModelReady = Model.LooksInstalled();
            if (!ModelReady) CloseEngine();
            ModelsChanged?.Invoke();
        }
        string text = error switch
        {
            VoiceError.NoMicrophone => "Микрофон не найден",
            VoiceError.MicrophoneBusy => "Микрофон занят другой программой",
            VoiceError.ModelMissing or VoiceError.ModelDamaged => "Модель повреждена, скачайте её заново",
            _ => "Не получилось распознать",
        };
        if (dictating || error is VoiceError.ModelMissing or VoiceError.ModelDamaged) Pill().ShowMessage(text);
    }

    private void OnExited()
    {
        bool dictating = _state != State.Idle;
        if (_engine is not null)
        {
            _engine.Reply -= OnReply;
            _engine.Exited -= OnExited;
            _engine.Dispose();
            _engine = null;
        }
        _state = State.Idle;
        SetListening(false);
        if (dictating) Pill().ShowMessage("Голосовой ввод перезапустится при следующем нажатии");
        else _host?.TrimMemory();
    }

    /// <summary>The hotkey was pressed but no model is here yet: offer the choice.</summary>
    private void ShowChooser()
    {
        if (_chooser is not null) { _chooser.Activate(); return; }
        _chooser = new ModelChooserWindow(this);
        _chooser.Closed += (_, _) => { _chooser = null; _host?.TrimMemory(); };
        _chooser.Show();
        _chooser.Activate();
    }

    private void SetListening(bool on)
    {
        if (_hook is not null) _hook.Matcher.Listening = on;
    }

    private VoicePill Pill()
    {
        if (_pill is null || _pill.IsClosing)
        {
            var pill = new VoicePill();
            pill.Closed += (_, _) => { if (_pill == pill) _pill = null; };
            _pill = pill;
        }
        return _pill;
    }

    private void ReleaseHook()
    {
        _hook?.Dispose();
        _hook = null;
        if (_captureDone is { } done) { _captureDone = null; done(null); }
    }

    private void CloseEngine()
    {
        if (_engine is null) return;
        _engine.Reply -= OnReply;
        _engine.Exited -= OnExited;
        _engine.Dispose();
        _engine = null;
        _state = State.Idle;
        _pill?.FadeOut();
        _host?.TrimMemory();
    }
}

/// <summary>A running model download that a reopened panel can show again.</summary>
internal sealed class DownloadJob(string model, bool use = false)
{
    /// <summary>Which model is downloading.</summary>
    public string Model { get; } = model;

    /// <summary>Use this model once it is here (it was picked in the "no model" window).</summary>
    public bool Use { get; } = use;

    private readonly CancellationTokenSource _cancel = new();

    public int Percent { get; private set; }

    public CancellationToken Token => _cancel.Token;

    public event Action<int>? Progress;

    public void Report(int percent)
    {
        Percent = percent;
        Progress?.Invoke(percent);
    }

    public void Cancel() => _cancel.Cancel();
}
