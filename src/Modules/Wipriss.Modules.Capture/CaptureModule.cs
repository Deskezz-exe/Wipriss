using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using Wipriss.Core;
using Wipriss.Core.Modules;

namespace Wipriss.Modules.Capture;

/// <summary>
/// Screenshots on PrtSc. A small keyboard hook takes the key before Windows' Snipping Tool, so switching it on works
/// at once; a registered hotkey covers programs running as administrator. Nothing is polled while idle. A screenshot lives only in memory and on the clipboard until you press "Сохранить".
/// </summary>
public sealed class CaptureModule : IModule, ISwitchOffAware
{
    private const int ShotKey = 1, StopKey = 2;

    private HwndSource? _hotkeys;
    private PrintScreenHook? _hook;
    private System.Windows.Threading.Dispatcher? _dispatcher;
    private IModuleHost? _host;
    private ShotToast? _toast;
    private Action? _onStopKey;

    public ModuleInfo Info { get; } = ModuleCatalog.Get("capture");

    public Size PanelSize => new(440, 380);

    /// <summary>True when the registered hotkey is ours (it matters only over windows running as administrator).</summary>
    internal bool KeyRegistered { get; private set; }

    /// <summary>True when the keyboard hook catches PrtSc (the normal case).</summary>
    internal bool HookActive => _hook?.Active == true;

    internal bool IsCapturing { get; private set; }

    /// <summary>Who gets PrtSc: Wipriss or Snipping Tool.</summary>
    internal PrintScreenKey PrintScreen { get; } = new();

    public string Summary =>
        HookActive || KeyRegistered ? "по " + Wipriss.Core.Ui.HotkeyRow.Pretty(PrintScreen.Key)
        : "клавиша занята";

    /// <summary>The module is switched off: PrtSc goes back to Snipping Tool.</summary>
    public void SwitchedOff()
    {
        try { PrintScreen.Apply(moduleOn: false); }
        catch (Exception ex) { ErrorLog.Write(ex); }
    }

    /// <summary>A new screenshot key from Settings. Works at once.</summary>
    internal void SetKey(Wipriss.Core.Input.Hotkey key)
    {
        PrintScreen.Choose(key);
        ReleaseKey();
        TakeKey();
    }

    /// <summary>While Settings records a new key, the old one must not take screenshots.</summary>
    internal void PauseKey(bool paused, Action<int>? printScreen = null)
    {
        if (_hook is not null)
        {
            _hook.Recorder = paused ? printScreen : null;
            _hook.Paused = paused;
        }
        if (paused && _hotkeys is not null && KeyRegistered)
        {
            Native.UnregisterHotKey(_hotkeys.Handle, ShotKey);
            KeyRegistered = false;
        }
        else if (!paused) TakeKey();
    }

    public void Start(IModuleHost host)
    {
        _host = host;
        _dispatcher = host.Dispatcher;
        _hotkeys = new HwndSource(new HwndSourceParameters("Wipriss.Capture") { ParentWindow = new IntPtr(-3), WindowStyle = 0 });
        _hotkeys.AddHook(OnMessage);

        // The user's choice wins every time Wipriss starts, even if a Windows update switched PrtSc back.
        try
        {
            PrintScreen.Apply();
        }
        catch (Exception ex)
        {
            ErrorLog.Write(ex);
        }

        TakeKey();
    }

    private void TakeKey()
    {
        var key = PrintScreen.Key;
        // The hook calls back on its own thread; the capture itself runs on the UI thread.
        _hook ??= new PrintScreenHook(() => _dispatcher?.BeginInvoke(TakeScreenshot));
        _hook.Key = key;
        _hook.Paused = false;
        if (_hotkeys is not null && !KeyRegistered)
            KeyRegistered = Native.RegisterHotKey(_hotkeys.Handle, ShotKey, (uint)key.Modifiers | Native.MOD_NOREPEAT, (uint)key.Key);
    }

    private void ReleaseKey()
    {
        _hook?.Dispose();
        _hook = null;
        if (_hotkeys is not null && KeyRegistered) Native.UnregisterHotKey(_hotkeys.Handle, ShotKey);
        KeyRegistered = false;
    }

    public void Stop()
    {
        _toast?.Close();
        ReleaseKey();
        if (_hotkeys is not null)
        {
            Native.UnregisterHotKey(_hotkeys.Handle, StopKey);
            _hotkeys.Dispose();
            _hotkeys = null;
        }

        KeyRegistered = false;
    }

    public FrameworkElement CreatePanel(IPanelContext panel) => new CapturePanel(this, panel);

    /// <summary>Freezes the screen and shows the choice: area, whole screen or scrolling.</summary>
    internal void TakeScreenshot()
    {
        if (IsCapturing) return;
        IsCapturing = true;
        try
        {
            new CaptureSession(this).Start();
        }
        catch (Exception ex)
        {
            ErrorLog.Write(ex);
            IsCapturing = false;
        }
    }

    internal void SessionEnded()
    {
        IsCapturing = false;
        _host?.TrimMemory(); // the frozen screen copies are no longer needed
    }

    /// <summary>The finished screenshot: on the clipboard already; the toast offers the editor.</summary>
    internal void Deliver(BitmapSource image, bool onClipboard)
    {
        _toast?.Close();
        _toast = new ShotToast(image, onClipboard);
        _toast.EditRequested += () =>
        {
            var editor = new EditorWindow(image);
            editor.Closed += (_, _) =>
            {
                _host?.TrimMemory(); // the picture and its undo history are gone: give the memory back
            };
            editor.Show();
            // The click landed on a window that never takes focus, so bring the editor forward explicitly.
            Native.BringToFront(editor);
            editor.Activate();
        };
        _toast.Closed += (_, _) =>
        {
            _toast = null;
            _host?.TrimMemory();
        };
        _toast.Show();
    }

    /// <summary>Esc works as a system key while a scrolling capture runs (the page has focus, not Wipriss).</summary>
    internal void WatchStopKey(Action? onStop)
    {
        if (_hotkeys is null) return;
        _onStopKey = onStop;
        if (onStop is null) Native.UnregisterHotKey(_hotkeys.Handle, StopKey);
        else Native.RegisterHotKey(_hotkeys.Handle, StopKey, Native.MOD_NOREPEAT, Native.VK_ESCAPE);
    }


    private IntPtr OnMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != Native.WM_HOTKEY) return IntPtr.Zero;
        handled = true;
        if (wParam == ShotKey) TakeScreenshot();
        else if (wParam == StopKey) _onStopKey?.Invoke();
        return IntPtr.Zero;
    }
}
