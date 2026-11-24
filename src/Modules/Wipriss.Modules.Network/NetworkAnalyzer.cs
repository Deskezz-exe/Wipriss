using System.Diagnostics;
using System.IO;
using Wipriss.ProcessTools;

namespace Wipriss.Modules.Network;

/// <summary>One program that used the network today or is connected right now.</summary>
internal sealed record AppReport(
    string Key,
    string Name,
    string? Path,
    bool IsStoreApp,
    bool IsWindows,
    FileSignature? Signature,
    long ReceivedToday,
    long SentToday,
    int ConnectionCount,
    double? CpuPercent,
    IReadOnlyList<ProcessEntry> Processes,
    Verdict Verdict,
    string? IconPath)
{
    public long TotalToday => ReceivedToday + SentToday;
}

internal sealed record NetworkReport(IReadOnlyList<AppReport> Apps, DateTime MadeAt);

/// <summary>
/// Brings together Windows' traffic accounting, live connections, running processes and file signatures,
/// and runs <see cref="TrafficCheck"/> on each program. Slow parts are cached: signatures per file version,
/// last week's usage per day, today's usage for a minute. One analysis at a time, on a background thread.
/// </summary>
internal sealed class NetworkAnalyzer
{
    private const string WindowsKey = "";
    private static readonly string WindowsDir =
        Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd('\\') + "\\";

    private readonly object _gate = new();
    private readonly Dictionary<string, (DateTime Written, FileSignature Signature)> _signatures = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _names = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(int Pid, long Created), string?> _paths = [];
    private Dictionary<string, (long UsualMax, int Days, double SentShare)> _week = [];
    private DateTime _weekFor;
    private List<AppTraffic> _today = [];
    private DateTime _todayAt;
    private Dictionary<(int, long), long> _cpu = [];
    private long _cpuStamp;

    public NetworkReport? Last { get; private set; }

    public NetworkReport Analyze()
    {
        lock (_gate)
        {
            DateTime now = DateTime.Now;
            RefreshUsage(now);

            var all = ProcessSnapshot.Take();
            var connections = PortTable.InternetConnections().ToLookup(c => c.Pid);
            var cpuByPid = CpuShares(all);

            // Running processes by program file, so live facts can join the usage numbers.
            var running = new Dictionary<string, List<ProcessEntry>>(StringComparer.OrdinalIgnoreCase);
            var alive = new HashSet<(int, long)>();
            foreach (var p in all)
            {
                if (p.Pid <= 4) continue;
                var key = (p.Pid, p.CreateTime);
                alive.Add(key);
                if (!_paths.TryGetValue(key, out string? path)) _paths[key] = path = ProcessQuery.ImagePath(p.Pid);
                if (path is null) continue;
                if (!running.TryGetValue(path, out var list)) running[path] = list = [];
                list.Add(p);
            }

            foreach (var gone in _paths.Keys.Where(k => !alive.Contains(k)).ToList()) _paths.Remove(gone);

            // Programs to look at: everything with traffic today plus everything connected right now.
            var keys = new Dictionary<string, (string? Path, bool Store)>(StringComparer.OrdinalIgnoreCase);
            var usage = new Dictionary<string, AppTraffic>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in _today)
            {
                string key = KeyOf(t.Id, out string? path, out bool store);
                keys[key] = (path, store);
                usage[key] = usage.TryGetValue(key, out var prev) ? prev with { Received = prev.Received + t.Received, Sent = prev.Sent + t.Sent } : t;
            }

            foreach (var (path, processes) in running)
                if (processes.Any(p => connections[p.Pid].Any()))
                    keys.TryAdd(path, (path, false));

            var apps = new List<AppReport>();
            foreach (var (key, (path, store)) in keys)
            {
                usage.TryGetValue(key, out var traffic);
                var processes = path is not null && running.TryGetValue(path, out var r) ? r
                    : store ? running.Where(pair => InPackage(pair.Key, key)).SelectMany(pair => pair.Value).ToList()
                    : [];
                var appConnections = processes.SelectMany(p => connections[p.Pid]).ToList();
                double? cpu = processes.Count == 0 || cpuByPid is null ? null : processes.Sum(p => cpuByPid.GetValueOrDefault(p.Pid));
                bool inWindows = key == WindowsKey || (path?.StartsWith(WindowsDir, StringComparison.OrdinalIgnoreCase) ?? false);
                var signature = path is not null && !inWindows ? SignatureOf(path) : null;
                _week.TryGetValue(key, out var week);
                string? parent = processes.Select(p => ProcessTree.ParentOf(all, p)?.Name).FirstOrDefault(n => n is not null);

                var facts = new TrafficFacts
                {
                    Path = path,
                    FileName = path is not null ? System.IO.Path.GetFileName(path) : key,
                    IsStoreApp = store,
                    InWindowsFolder = inWindows,
                    Signature = signature,
                    ReceivedToday = traffic?.Received ?? 0,
                    SentToday = traffic?.Sent ?? 0,
                    UsualDailyMax = week.UsualMax,
                    ActiveDays = week.Days,
                    UsualSentShare = week.SentShare,
                    Connections = appConnections,
                    CpuPercent = cpu,
                    ParentName = parent,
                };

                var verdict = key == WindowsKey ? new Verdict(0, []) : TrafficCheck.Evaluate(facts);
                // Store apps have no file path in the usage data; a running process gives one for the icon.
                string? iconPath = path ?? processes.Select(p => _paths.GetValueOrDefault((p.Pid, p.CreateTime))).FirstOrDefault(p => p is not null);
                apps.Add(new AppReport(key, NameOf(key, path, store), path, store, key == WindowsKey || inWindows, signature,
                    facts.ReceivedToday, facts.SentToday, appConnections.Count, cpu, processes, verdict, iconPath));
            }

            Last = new NetworkReport(apps, now);
            return Last;
        }
    }

    /// <summary>Forget everything (the module was switched off).</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _signatures.Clear();
            _names.Clear();
            _paths.Clear();
            _week = [];
            _weekFor = default;
            _today = [];
            _todayAt = default;
            _cpu = [];
            _cpuStamp = 0;
            Last = null;
        }
    }

    /// <summary>Today's usage at most once a minute (Windows updates it about hourly); last week's once a day.</summary>
    private void RefreshUsage(DateTime now)
    {
        if (now - _todayAt > TimeSpan.FromMinutes(1))
        {
            _today = NetworkUsage.Read(now.Date.ToUniversalTime(), now.ToUniversalTime());
            _todayAt = now;
        }

        if (_weekFor == now.Date) return;
        var days = new Dictionary<string, (long Max, int Days, long Sent, long Total)>(StringComparer.OrdinalIgnoreCase);
        for (int back = 1; back <= 7; back++)
        {
            DateTime day = now.Date.AddDays(-back);
            foreach (var t in NetworkUsage.Read(day.ToUniversalTime(), day.AddDays(1).ToUniversalTime()))
            {
                if (t.Total == 0) continue;
                string key = KeyOf(t.Id, out _, out _);
                days.TryGetValue(key, out var d);
                days[key] = (Math.Max(d.Max, t.Total), d.Days + 1, d.Sent + t.Sent, d.Total + t.Total);
            }
        }

        _week = days.ToDictionary(d => d.Key, d => (d.Value.Max, d.Value.Days, d.Value.Total > 0 ? (double)d.Value.Sent / d.Value.Total : 0),
            StringComparer.OrdinalIgnoreCase);
        _weekFor = now.Date;
    }

    /// <summary>CPU share since the last analysis, like Task Manager. Null on the first run.</summary>
    private Dictionary<int, double>? CpuShares(List<ProcessEntry> all)
    {
        long now = Stopwatch.GetTimestamp();
        var times = all.ToDictionary(p => (p.Pid, p.CreateTime), p => p.CpuTime);
        Dictionary<int, double>? shares = null;
        if (_cpuStamp != 0)
        {
            double capacity = (now - _cpuStamp) * 10_000_000.0 / Stopwatch.Frequency * Environment.ProcessorCount;
            if (capacity > 0)
            {
                shares = [];
                foreach (var p in all)
                    if (_cpu.TryGetValue((p.Pid, p.CreateTime), out long before))
                        shares[p.Pid] = 100.0 * Math.Max(0, p.CpuTime - before) / capacity;
            }
        }

        _cpu = times;
        _cpuStamp = now;
        return shares;
    }

    /// <summary>Windows reports desktop programs by kernel path; turn that into a normal path when possible.</summary>
    private static string KeyOf(string id, out string? path, out bool store)
    {
        store = false;
        path = null;
        if (string.IsNullOrEmpty(id)) return WindowsKey;
        if (id.StartsWith(@"\device\", StringComparison.OrdinalIgnoreCase))
        {
            path = DevicePaths.ToDosPath(id);
            return path ?? id;
        }

        // Store apps are reported by package family name: "Name_" plus a 13-character publisher ID.
        int cut = id.LastIndexOf('_');
        store = cut > 0 && id.Length - cut - 1 == 13 && id[(cut + 1)..].All(char.IsAsciiLetterOrDigit);
        return id;
    }

    /// <summary>"…\WindowsApps\Claude_1.2.3.0_x64__pzs8sxrjxfjjc\…" belongs to package family "Claude_pzs8sxrjxfjjc".</summary>
    private static bool InPackage(string processPath, string family)
    {
        int cut = family.LastIndexOf('_');
        if (cut <= 0) return false;
        string name = family[..cut], publisher = family[(cut + 1)..];
        int at = processPath.IndexOf("\\WindowsApps\\", StringComparison.OrdinalIgnoreCase);
        if (at < 0) return false;
        string folder = processPath[(at + 13)..].Split('\\')[0];
        return folder.StartsWith(name + "_", StringComparison.OrdinalIgnoreCase)
            && folder.EndsWith("__" + publisher, StringComparison.OrdinalIgnoreCase);
    }

    private FileSignature SignatureOf(string path)
    {
        DateTime written;
        try
        {
            written = File.GetLastWriteTimeUtc(path);
        }
        catch (Exception)
        {
            return new FileSignature(SignatureState.Unknown, null);
        }

        if (_signatures.TryGetValue(path, out var cached) && cached.Written == written) return cached.Signature;
        var signature = FileTrust.Check(path);
        _signatures[path] = (written, signature);
        return signature;
    }

    private string NameOf(string key, string? path, bool store)
    {
        if (key == WindowsKey) return "Windows и службы";
        if (_names.TryGetValue(key, out string? name)) return name;

        if (path is not null)
        {
            try
            {
                name = FileVersionInfo.GetVersionInfo(path).FileDescription?.Trim();
            }
            catch (Exception)
            {
                name = null;
            }

            if (string.IsNullOrEmpty(name) || name.Length > 40) name = System.IO.Path.GetFileNameWithoutExtension(path);
        }
        else if (store)
        {
            // "Microsoft.WindowsNotepad_8wekyb3d8bbwe" → "WindowsNotepad"
            string family = key[..Math.Max(0, key.LastIndexOf('_'))];
            name = family.StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase) ? family[10..] : family;
            if (name.Length == 0) name = key;
        }
        else
        {
            name = System.IO.Path.GetFileName(key);
        }

        _names[key] = name;
        return name;
    }
}
