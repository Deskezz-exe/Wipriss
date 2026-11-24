using System.Diagnostics;
using System.IO;
using Wipriss.ProcessTools;

namespace Wipriss.Modules.Processes;

/// <summary>One program: all running processes of the same .exe (a browser can have dozens).</summary>
internal sealed record AppGroup(string Path, string Name, IReadOnlyList<ProcessEntry> Processes, long PrivateBytes, double? CpuPercent);

/// <summary>
/// Builds the list of the user's programs: processes on this desktop, not part of Windows, grouped by .exe.
/// Paths and names are cached per process, so each refresh only asks Windows about new processes.
/// One collect at a time; it runs on a background thread.
/// </summary>
internal sealed class AppCollector
{
    private static readonly string WindowsDir =
        Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd('\\') + "\\";

    private readonly object _gate = new();
    private readonly Dictionary<(int Pid, long Created), string?> _paths = [];
    private readonly Dictionary<string, string> _names = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<(int Pid, long Created), long> _cpu = [];
    private long _cpuStamp;

    /// <summary>The full process list from the latest collect (for finding child processes).</summary>
    public IReadOnlyList<ProcessEntry> LastSnapshot { get; private set; } = [];

    public List<AppGroup> Collect(bool withNames)
    {
        lock (_gate)
        {
            var all = ProcessSnapshot.Take();
            long now = Stopwatch.GetTimestamp();
            int session = ProcessQuery.CurrentSessionId;

            var byPath = new Dictionary<string, List<ProcessEntry>>(StringComparer.OrdinalIgnoreCase);
            var cpuNow = new Dictionary<(int, long), long>();
            var cpuDelta = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            var alive = new HashSet<(int, long)>();

            foreach (var p in all)
            {
                if (p.SessionId != session || ProcessGuard.KindOf(p) != ProcessKind.App) continue;

                var key = (p.Pid, p.CreateTime);
                alive.Add(key);
                if (!_paths.TryGetValue(key, out string? path))
                    _paths[key] = path = ProcessQuery.ImagePath(p.Pid);
                if (path is null || path.StartsWith(WindowsDir, StringComparison.OrdinalIgnoreCase)) continue;

                if (!byPath.TryGetValue(path, out var list)) byPath[path] = list = [];
                list.Add(p);

                cpuNow[key] = p.CpuTime;
                if (_cpu.TryGetValue(key, out long before))
                    cpuDelta[path] = cpuDelta.GetValueOrDefault(path) + Math.Max(0, p.CpuTime - before);
            }

            foreach (var gone in _paths.Keys.Where(k => !alive.Contains(k)).ToList())
                _paths.Remove(gone);

            // CPU share since the last collect, like Task Manager: 100 % = every core busy.
            bool hasBaseline = _cpuStamp != 0;
            double elapsed = (now - _cpuStamp) * 10_000_000.0 / Stopwatch.Frequency; // in 100 ns units
            double capacity = elapsed * Environment.ProcessorCount;
            _cpu = cpuNow;
            _cpuStamp = now;
            LastSnapshot = all;

            return byPath.Select(pair => new AppGroup(
                    pair.Key,
                    withNames ? NameOf(pair.Key) : "",
                    pair.Value,
                    pair.Value.Sum(p => p.PrivateBytes),
                    hasBaseline && capacity > 0 ? 100.0 * cpuDelta.GetValueOrDefault(pair.Key) / capacity : null))
                .ToList();
        }
    }

    /// <summary>Forget CPU history and caches (the panel closed).</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _cpu = [];
            _cpuStamp = 0;
            _names.Clear();
            LastSnapshot = [];
        }
    }

    /// <summary>The program's own name ("Google Chrome"), or the file name when it has none.</summary>
    private string NameOf(string path)
    {
        if (_names.TryGetValue(path, out string? name)) return name;
        try
        {
            name = FileVersionInfo.GetVersionInfo(path).FileDescription?.Trim();
        }
        catch (Exception)
        {
            name = null;
        }

        if (string.IsNullOrEmpty(name) || name.Length > 40) name = Path.GetFileNameWithoutExtension(path);
        _names[path] = name;
        return name;
    }
}
