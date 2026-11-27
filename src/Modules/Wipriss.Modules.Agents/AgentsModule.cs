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
public sealed class AgentsModule : IModule, IIslandCard
{
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

        if (!Directory.Exists(_root)) return; // Claude Code is not installed: the module stays silent

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
        lock (_gate)
        {
            _sessions.Clear();
            _pending.Clear();
        }
    }

    public FrameworkElement CreatePanel(IPanelContext panel) => new AgentCard(this);

    // ---- IIslandCard ----

    public IslandActivity? Activity
    {
        get
        {
            var now = DateTime.Now;
            var s = Visible(now).FirstOrDefault();
            if (s is null) return null;
            return s.Phase switch
            {
                Phase.Working => new IslandActivity(s.Headline, ActivityState.Busy, Mark, AgentCard.AgentBrush),
                Phase.Waiting => new IslandActivity("Ждёт вас", ActivityState.Waiting, Mark, AgentCard.AgentBrush),
                Phase.Done when now - s.LastEvent < DoneInPill => new IslandActivity("Готово", ActivityState.Done, Mark, AgentCard.AgentBrush),
                _ => null,
            };
        }
    }

    public bool HasContent => Visible(DateTime.Now).Count > 0;

    public FrameworkElement CreateCard() => new AgentCard(this);

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

internal sealed record SessionView(string Project, Phase Phase, string Headline, string Doing, Step[] Steps, DateTime LastEvent);
