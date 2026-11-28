using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Wipriss.Core.Modules;

namespace Wipriss.Modules.Agents;

/// <summary>
/// Shows what Claude Code is doing (the terminal and the Code tab of Claude Desktop both write the same
/// transcripts under %USERPROFILE%\.claude\projects). A file watcher tells us when a transcript grows;
/// only the new bytes are read, on a worker thread. Nothing runs while no session is active.
/// </summary>
public sealed class AgentsModule : IModule, IIslandCard, IPillSource, ISettingsPage, ISwitchOffAware
{
    /// <summary>The user switched Agents off: our hook leaves their agents' settings.</summary>
    public void SwitchedOff()
    {
        try { HookSetup.Disconnect(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { }
    }

    /// <summary>A session silent for this long has stopped (closed window, crash) even if its last line said "working".</summary>
    private static readonly TimeSpan Stale = TimeSpan.FromMinutes(10);

    /// <summary>A finished session stays on the home screen this long.</summary>
    private static readonly TimeSpan KeepFinished = TimeSpan.FromMinutes(30);

    /// <summary>"Готово" stays in the closed pill this long.</summary>
    private static readonly TimeSpan DoneInPill = TimeSpan.FromMinutes(2);

    /// <summary>The first look at an old transcript reads only its end.</summary>
    private const int FirstReadBytes = 512 * 1024;

    private readonly object _gate = new();
    private readonly Dictionary<string, Session> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _pending = new(StringComparer.OrdinalIgnoreCase);
    private FileSystemWatcher? _watcher;
    private DispatcherTimer? _debounce;
    private DispatcherTimer? _expire;
    private Dispatcher? _dispatcher;
    private IModuleHost? _host;
    private bool _firstRead = true;
    private string _root = "";

    public ModuleInfo Info { get; } = ModuleCatalog.Get("agents");

    public Size PanelSize => new(0, 0);

    public string Summary => "";

    public event Action? Changed;

    internal static Geometry Mark { get; } = MakeMark();

    public static string ProjectsRoot()
    {
        string? config = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        string baseDir = string.IsNullOrWhiteSpace(config)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude")
            : config;
        return Path.Combine(baseDir, "projects");
    }

    public void Start(IModuleHost host)
    {
        _dispatcher = host.Dispatcher;
        _host = host;
        _root = ProjectsRoot();
        _debounce = new DispatcherTimer(DispatcherPriority.Background, _dispatcher) { Interval = TimeSpan.FromMilliseconds(250) };
        _debounce.Tick += (_, _) => { _debounce.Stop(); ProcessPending(); };
        _expire = new DispatcherTimer(DispatcherPriority.Background, _dispatcher);
        _expire.Tick += (_, _) => { _expire.Stop(); Raise(); };

        _listen = new CancellationTokenSource();
        _ = ListenAsync(_listen.Token);
        WatchCodex();
        // The hook goes in by itself (or gets the new path after Wipriss moved): the owner asked for no buttons.
        Task.Run(() =>
        {
            try { HookSetup.Connect(); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { }
        });

        if (!Directory.Exists(_root)) return; // Claude Code is not installed: its transcripts stay silent

        _watcher = new FileSystemWatcher(_root, "*.jsonl")
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
            InternalBufferSize = 16 * 1024,
        };
        _watcher.Changed += OnFile;
        _watcher.Created += OnFile;
        _watcher.EnableRaisingEvents = true;

        // Sessions that were already running when Wipriss started.
        Task.Run(() =>
        {
            try
            {
                var since = DateTime.Now - Stale;
                foreach (var file in Directory.EnumerateFiles(_root, "*.jsonl", SearchOption.AllDirectories))
                    if (IsMainTranscript(file) && File.GetLastWriteTime(file) > since)
                        lock (_gate) _pending.Add(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            _dispatcher.BeginInvoke(ProcessPending);
        });
    }

    public void Stop()
    {
        if (_watcher is not null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Dispose();
            _watcher = null;
        }
        _debounce?.Stop();
        _expire?.Stop();
        _listen?.Cancel();
        _listen = null;
        if (_codexWatcher is not null)
        {
            _codexWatcher.EnableRaisingEvents = false;
            _codexWatcher.Dispose();
            _codexWatcher = null;
        }
        lock (_gate)
        {
            _sessions.Clear();
            _pending.Clear();
            _hookWaits.Clear();
        }
        _pills.Clear();
        _states.Clear();
    }

    public FrameworkElement CreatePanel(IPanelContext panel) => new AgentBlocks(this);

    public FrameworkElement CreateSettings() => new AgentsSettings();

    // ---- The pill: one block per agent that is doing something ----

    public event Action? PillChanged;

    public event Action<PillPeek>? PillPeek;

    private readonly Dictionary<string, AgentPill> _pills = [];
    private readonly Dictionary<string, AgentState> _states = [];
    private bool _pillShown;

    public IReadOnlyList<PillSlot> PillSlots =>
        _states.Where(s => s.Value != AgentState.None)
            .OrderBy(s => s.Key)
            .Select(s => new PillSlot("agent." + s.Key, "agents", PillGroup.Agents, 100, () => PillView(s.Key)))
            .ToList();

    public void SetPillShown(bool shown)
    {
        _pillShown = shown;
        foreach (var pill in _pills.Values) pill.Animate(shown);
    }

    private AgentPill PillView(string agent)
    {
        if (!_pills.TryGetValue(agent, out var pill)) _pills[agent] = pill = new AgentPill(agent);
        pill.Show(_states.GetValueOrDefault(agent));
        pill.Animate(_pillShown);
        return pill;
    }

    /// <summary>What each agent does now, the most urgent of its sessions.</summary>
    private Dictionary<string, (AgentState State, string Project)> CurrentStates(DateTime now)
    {
        var result = new Dictionary<string, (AgentState, string)>();

        // Claude Code: its transcripts, plus the hook for "waits for your approval".
        var claude = (State: AgentState.None, Project: "");
        foreach (var v in Visible(now))
        {
            var s = v.Phase switch
            {
                Phase.Waiting => AgentState.Waiting,
                Phase.Working => AgentState.Working,
                Phase.Done when now - v.LastEvent < DoneInPill => AgentState.Done,
                _ => AgentState.None,
            };
            if (Rank(s) > Rank(claude.State)) claude = (s, v.Project);
        }
        lock (_gate)
        {
            foreach (var w in _hookWaits.Values)
                if (w.Agent == "claude" && now - w.At < Stale && Rank(AgentState.Waiting) > Rank(claude.State))
                    claude = (AgentState.Waiting, w.Project);
        }
        result["claude"] = claude;

        // Codex: its session files say it works; its notify says it waits or is done.
        var codex = (State: AgentState.None, Project: "");
        lock (_gate)
        {
            foreach (var w in _hookWaits.Values)
                if (w.Agent == "codex" && now - w.At < Stale) codex = (AgentState.Waiting, w.Project);
            if (codex.State == AgentState.None)
            {
                if (_codexWorking is { } work && now - work < CodexQuiet && (_codexDone is not { } d || d < work)) codex = (AgentState.Working, "");
                else if (_codexDone is { } done && now - done < DoneInPill) codex = (AgentState.Done, _codexProject);
            }
        }
        result["codex"] = codex;
        return result;
    }

    private static int Rank(AgentState s) => s switch
    {
        AgentState.Waiting => 3,
        AgentState.Working => 2,
        AgentState.Done => 1,
        _ => 0,
    };

    /// <summary>Brings the pill up to date; a newly waiting agent gets a moment of attention.</summary>
    private void UpdatePill(DateTime now)
    {
        bool slotsChanged = false;
        foreach (var (agent, (state, project)) in CurrentStates(now))
        {
            var was = _states.GetValueOrDefault(agent);
            if (was == state) continue;
            _states[agent] = state;
            if ((was == AgentState.None) != (state == AgentState.None)) slotsChanged = true;
            if (state == AgentState.None) _pills.Remove(agent);
            else if (_pills.TryGetValue(agent, out var pill)) pill.Show(state);

            if (state == AgentState.Waiting)
            {
                string a = agent, p = project;
                PillPeek?.Invoke(new PillPeek("agent." + a, () => AgentPill.Peek(a, "ждёт подтверждения", p), TimeSpan.FromSeconds(3)));
            }
        }
        if (slotsChanged) PillChanged?.Invoke();
    }

    // ---- Hooks: Claude Code and Codex tell us when they wait (see Wipriss.AgentHook) ----

    private static readonly TimeSpan CodexQuiet = TimeSpan.FromMinutes(2);
    private readonly Dictionary<string, HookWait> _hookWaits = [];
    private DateTime? _codexWorking, _codexDone;
    private string _codexProject = "";
    private CancellationTokenSource? _listen;
    private FileSystemWatcher? _codexWatcher;

    private sealed record HookWait(string Agent, string Transcript, string Project, DateTime At);

    /// <summary>Waits for hook messages. Awaiting a connection holds no thread.</summary>
    private async Task ListenAsync(CancellationToken cancel)
    {
        while (!cancel.IsCancellationRequested)
        {
            try
            {
                await using var server = new System.IO.Pipes.NamedPipeServerStream(HookProtocol.PipeName, System.IO.Pipes.PipeDirection.In, 4,
                    System.IO.Pipes.PipeTransmissionMode.Byte, System.IO.Pipes.PipeOptions.Asynchronous | System.IO.Pipes.PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(cancel).ConfigureAwait(false);
                var buffer = new byte[HookProtocol.MaxMessageBytes];
                int total = 0, read;
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel))
                {
                    timeout.CancelAfter(2000);
                    while (total < buffer.Length && (read = await server.ReadAsync(buffer.AsMemory(total), timeout.Token).ConfigureAwait(false)) > 0)
                        total += read;
                }
                if (HookProtocol.Read(Encoding.UTF8.GetString(buffer, 0, total)) is { } message)
                    _dispatcher?.BeginInvoke(() => OnHook(message));
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested) { return; }
            catch (OperationCanceledException) { }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { return; } // another copy holds the pipe
        }
    }

    private void OnHook(HookMessage m)
    {
        if (_dispatcher is null) return;
        var now = DateTime.Now;
        string key = m.Agent + ":" + (m.Session.Length > 0 ? m.Session : m.Transcript);
        lock (_gate)
        {
            if (m.Kind == HookKind.Waiting) _hookWaits[key] = new HookWait(m.Agent, m.Transcript, m.Project, now);
            else _hookWaits.Remove(key);
            if (m.Agent == "codex" && m.Kind == HookKind.Done)
            {
                _codexDone = now;
                _codexProject = m.Project;
                foreach (var k in _hookWaits.Where(w => w.Value.Agent == "codex").Select(w => w.Key).ToList()) _hookWaits.Remove(k);
            }
        }
        Raise();
    }

    /// <summary>Codex writes its sessions under ~/.codex/sessions: a write means it works.</summary>
    private void WatchCodex()
    {
        string dir = Path.Combine(Path.GetDirectoryName(HookSetup.CodexConfigFile)!, "sessions");
        if (!Directory.Exists(dir)) return;
        try
        {
            _codexWatcher = new FileSystemWatcher(dir, "*.jsonl")
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                InternalBufferSize = 8 * 1024,
            };
            _codexWatcher.Changed += OnCodexFile;
            _codexWatcher.Created += OnCodexFile;
            _codexWatcher.EnableRaisingEvents = true;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException) { _codexWatcher = null; }
    }

    private void OnCodexFile(object sender, FileSystemEventArgs e)
    {
        var now = DateTime.Now;
        bool fresh;
        lock (_gate)
        {
            // Many writes a second while it works: wake the UI only when the state may change.
            fresh = _codexWorking is not { } w || now - w > TimeSpan.FromSeconds(20) || (_codexDone is { } d && d > w);
            _codexWorking = now;
            if (_hookWaits.Count > 0)
                foreach (var k in _hookWaits.Where(x => x.Value.Agent == "codex" && now - x.Value.At > TimeSpan.FromSeconds(2)).Select(x => x.Key).ToList())
                    _hookWaits.Remove(k);
        }
        if (fresh) _dispatcher?.BeginInvoke(Raise);
    }

    public bool HasContent => HookSetup.ClaudeInstalled || HookSetup.CodexInstalled || Visible(DateTime.Now).Count > 0;

    public FrameworkElement CreateCard() => new AgentBlocks(this);

    /// <summary>The agents for the home screen (installed or active), at most two, with what each does now.</summary>
    internal List<AgentView> HomeAgents(DateTime now)
    {
        var states = CurrentStates(now);
        var visible = Visible(now);
        var list = new List<AgentView>();

        if (HookSetup.ClaudeInstalled || visible.Count > 0)
        {
            var (state, project) = states["claude"];
            var working = visible.FirstOrDefault(v => v.Phase == Phase.Working);
            if (project.Length == 0) project = visible.FirstOrDefault()?.Project ?? "";
            var status = state switch
            {
                AgentState.Working => AgentStatus.Working,
                AgentState.Waiting => AgentStatus.Waiting,
                _ when visible.Count > 0 => AgentStatus.Idle,
                _ => AgentStatus.Inactive,
            };
            // What it does right now ("Читает Test.html"); else the plan's step in progress.
            string doing = working?.Doing is { Length: > 0 } d ? d
                : working?.Headline is { Length: > 0 } h && h != "Работает" ? h : "";
            list.Add(new AgentView("claude", status, project, doing));
        }

        if (HookSetup.CodexInstalled)
        {
            var (state, project) = states["codex"];
            bool recent;
            lock (_gate) recent = _codexDone is { } done && now - done < KeepFinished || _codexWorking is { } w && now - w < KeepFinished;
            var status = state switch
            {
                AgentState.Working => AgentStatus.Working,
                AgentState.Waiting => AgentStatus.Waiting,
                _ when recent => AgentStatus.Idle,
                _ => AgentStatus.Inactive,
            };
            list.Add(new AgentView("codex", status, project, ""));
        }

        // The busy ones first when there are more than fit.
        return list.OrderBy(a => a.Status).Take(2).OrderBy(a => a.Agent == "claude" ? 0 : 1).ToList();
    }

    /// <summary>Sessions worth showing, most important first: working, then the most recent.</summary>
    internal List<SessionView> Visible(DateTime now)
    {
        lock (_gate)
        {
            return _sessions.Values
                .Select(s => s.View(now))
                .Where(v => v.Phase is Phase.Working or Phase.Waiting || (v.Phase == Phase.Done && now - v.LastEvent < KeepFinished))
                .OrderBy(v => v.Phase == Phase.Working ? 0 : v.Phase == Phase.Waiting ? 1 : 2)
                .ThenByDescending(v => v.LastEvent)
                .ToList();
        }
    }

    // ---- Reading transcripts ----

    /// <summary>Main transcripts sit right in a project folder; helper agents' ones are deeper.</summary>
    private bool IsMainTranscript(string path) =>
        string.Equals(Path.GetDirectoryName(Path.GetDirectoryName(path)), _root, StringComparison.OrdinalIgnoreCase);

    private void OnFile(object sender, FileSystemEventArgs e)
    {
        if (!IsMainTranscript(e.FullPath)) return;
        lock (_gate) _pending.Add(e.FullPath);
        _dispatcher?.BeginInvoke(() => { if (_debounce is { IsEnabled: false }) _debounce.Start(); });
    }

    private void ProcessPending()
    {
        string[] files;
        lock (_gate)
        {
            files = [.. _pending];
            _pending.Clear();
        }
        if (files.Length == 0) return;

        Task.Run(() =>
        {
            foreach (var file in files)
            {
                try { ReadNew(file); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            _dispatcher?.BeginInvoke(Raise);
        });
    }

    private void ReadNew(string path)
    {
        Session session;
        lock (_gate)
        {
            if (!_sessions.TryGetValue(path, out session!)) _sessions[path] = session = new Session();
        }

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        long length = stream.Length;
        if (length < session.Offset) { session.Reset(); } // rewritten from scratch
        long from = session.Offset == 0 && length > FirstReadBytes ? length - FirstReadBytes : session.Offset;
        bool skipFirst = from > 0 && session.Offset == 0; // started mid-line
        if (length <= from) return;

        stream.Position = from;
        var buffer = new byte[length - from];
        int read = 0;
        while (read < buffer.Length)
        {
            int n = stream.Read(buffer, read, buffer.Length - read);
            if (n == 0) break;
            read += n;
        }

        // Only whole lines; the rest waits for the next write.
        int lastNewline = Array.LastIndexOf(buffer, (byte)'\n', read - 1);
        if (lastNewline < 0) return;
        string text = Encoding.UTF8.GetString(buffer, 0, lastNewline);

        lock (_gate)
        {
            session.Offset = from + lastNewline + 1;
            bool first = skipFirst;
            foreach (var line in text.Split('\n'))
            {
                if (first) { first = false; continue; }
                session.Transcript.Feed(line.TrimEnd('\r'));
            }
            session.FileTime = File.GetLastWriteTime(path);

            // The transcript moved on after "waits for approval": the user answered.
            if (_hookWaits.Count > 0)
            {
                var now = DateTime.Now;
                string id = Path.GetFileNameWithoutExtension(path);
                var answered = _hookWaits
                    .Where(w => w.Value.Agent == "claude" && now - w.Value.At > TimeSpan.FromSeconds(1.5)
                                && (string.Equals(w.Value.Transcript, path, StringComparison.OrdinalIgnoreCase)
                                    || w.Key.EndsWith(":" + id, StringComparison.OrdinalIgnoreCase)))
                    .Select(w => w.Key)
                    .ToList();
                foreach (var key in answered) _hookWaits.Remove(key);
            }
        }
    }

    private void Raise()
    {
        var now = DateTime.Now;
        var visible = Visible(now);

        // One wake-up when something should change by itself: a "Готово" leaving the pill, a silent
        // session counting as stopped, a finished one leaving the home screen.
        TimeSpan? next = null;
        foreach (var v in visible)
        {
            TimeSpan left = v.Phase switch
            {
                Phase.Working or Phase.Waiting => v.LastEvent + Stale - now,
                _ => (now - v.LastEvent < DoneInPill ? v.LastEvent + DoneInPill : v.LastEvent + KeepFinished) - now,
            };
            if (left > TimeSpan.Zero && (next is null || left < next)) next = left;
        }
        // The hook's "waits" and Codex's states end by themselves too.
        lock (_gate)
        {
            var ends = _hookWaits.Values.Select(w => w.At + Stale).ToList();
            if (_codexWorking is { } work) ends.Add(work + CodexQuiet);
            if (_codexDone is { } done) ends.Add(done + DoneInPill);
            foreach (var end in ends)
            {
                var left = end - now;
                if (left > TimeSpan.Zero && (next is null || left < next)) next = left;
            }
            foreach (var key in _hookWaits.Where(w => now - w.Value.At >= Stale).Select(w => w.Key).ToList()) _hookWaits.Remove(key);
        }
        UpdatePill(now);
        // The first look reads old transcripts: give that memory back once it is parsed.
        if (_firstRead) { _firstRead = false; _host?.TrimMemory(); }
        _expire!.Stop();
        if (next is { } wait)
        {
            _expire.Interval = wait + TimeSpan.FromSeconds(1);
            _expire.Start();
        }

        Changed?.Invoke();
    }

    private static Geometry MakeMark()
    {
        var g = Geometry.Parse("M12,1.5 L13.2,9.1 L19.4,4.6 L14.9,10.8 L22.5,12 L14.9,13.2 L19.4,19.4 L13.2,14.9 L12,22.5 L10.8,14.9 L4.6,19.4 L9.1,13.2 L1.5,12 L9.1,10.8 L4.6,4.6 L10.8,9.1 Z");
        g.Freeze();
        return g;
    }

    private sealed class Session
    {
        public long Offset;
        public DateTime FileTime;
        public Transcript Transcript = new();

        public void Reset()
        {
            Offset = 0;
            Transcript = new Transcript();
        }

        /// <summary>A copy for the UI thread. A working session that has gone quiet counts as stopped.</summary>
        public SessionView View(DateTime now)
        {
            var t = Transcript;
            var last = t.LastEvent > FileTime ? t.LastEvent : FileTime;
            var phase = t.Phase is Phase.Working or Phase.Waiting && now - last > Stale ? Phase.Idle : t.Phase;
            return new SessionView(t.Project, phase, t.Headline, t.Doing, [.. t.Steps], last);
        }
    }
}

/// <summary>What the home screen says about an agent. The order is the order of urgency.</summary>
internal enum AgentStatus { Waiting, Working, Idle, Inactive }

internal sealed record AgentView(string Agent, AgentStatus Status, string Project, string Doing);

internal sealed record SessionView(string Project, Phase Phase, string Headline, string Doing, Step[] Steps, DateTime LastEvent);
