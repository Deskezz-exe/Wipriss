using System.Net;
using Wipriss.ProcessTools;

namespace Wipriss.Modules.Ports;

/// <summary>A port and the program holding it.</summary>
/// <param name="Launcher">The program that started the owner (npm, a watcher), if stopping only the owner may get it restarted.</param>
internal sealed record PortItem(
    PortProtocol Protocol,
    int Port,
    IReadOnlyList<IPAddress> Addresses,
    int Pid,
    ProcessEntry? Process,
    ProcessKind Kind,
    string? Path,
    string? CommandLine,
    string ShortCommand,
    ProcessEntry? Launcher,
    string? LauncherCommand);

/// <summary>
/// Reads the port table and matches each port with its process. Process details are cached
/// per process, so a refresh only asks Windows about new ones. One collect at a time.
/// </summary>
internal sealed class PortCollector
{
    private readonly object _gate = new();
    private readonly Dictionary<(int Pid, long Created), (string? Path, string? CommandLine)> _details = [];

    public IReadOnlyList<ProcessEntry> LastSnapshot { get; private set; } = [];

    public List<PortItem> Collect(PortProtocol protocol)
    {
        lock (_gate)
        {
            var ports = protocol == PortProtocol.Tcp ? PortTable.TcpListeners() : PortTable.Udp();
            var all = ProcessSnapshot.Take();
            var byPid = new Dictionary<int, ProcessEntry>();
            foreach (var p in all) byPid.TryAdd(p.Pid, p);

            var alive = new HashSet<(int, long)>();
            var items = new List<PortItem>();
            foreach (var group in ports.GroupBy(e => (e.Port, e.Pid)))
            {
                byPid.TryGetValue(group.Key.Pid, out var process);
                var kind = process is not null
                    ? ProcessGuard.KindOf(process)
                    : ProcessGuard.KindOf("", group.Key.Pid, ProcessQuery.SessionOf(group.Key.Pid));

                (string? path, string? commandLine) = process is null ? (null, null) : DetailsOf(process, alive);
                string shortCommand = process is null ? $"PID {group.Key.Pid}" : CommandLineText.Short(commandLine, process.Name);

                ProcessEntry? launcher = null;
                string? launcherCommand = null;
                if (process is not null && kind == ProcessKind.App && ProcessTree.ParentOf(all, process) is { } parent
                    && ProcessGuard.KindOf(parent) == ProcessKind.App)
                {
                    string? parentCommand = DetailsOf(parent, alive).CommandLine;
                    if (Launchers.IsLauncher(parent.Name, parentCommand))
                    {
                        launcher = parent;
                        launcherCommand = LauncherText(parent, parentCommand);
                    }
                }

                items.Add(new PortItem(protocol, group.Key.Port, group.Select(e => e.Address).Distinct().ToList(),
                    group.Key.Pid, process, kind, path, commandLine, shortCommand, launcher, launcherCommand));
            }

            foreach (var gone in _details.Keys.Where(k => !alive.Contains(k)).ToList())
                _details.Remove(gone);

            LastSnapshot = all;
            return Ordered(items);
        }
    }

    /// <summary>Your programs first (that is what you came for), then Windows, services, Docker and WSL; by port inside each.</summary>
    public static List<PortItem> Ordered(IEnumerable<PortItem> items) =>
        items.OrderBy(i => i.Kind == ProcessKind.App ? 0 : 1).ThenBy(i => i.Port).ThenBy(i => i.Pid).ToList();

    /// <summary>TCP ports that programs on this desktop listen on. For the island tile; about a millisecond.</summary>
    public static int CountDesktopListeners()
    {
        int session = ProcessQuery.CurrentSessionId;
        return PortTable.TcpListeners()
            .Where(e => e.Pid > 4 && ProcessQuery.SessionOf(e.Pid) == session)
            .Select(e => e.Port)
            .Distinct()
            .Count();
    }

    private (string? Path, string? CommandLine) DetailsOf(ProcessEntry process, HashSet<(int, long)> alive)
    {
        var key = (process.Pid, process.CreateTime);
        alive.Add(key);
        if (!_details.TryGetValue(key, out var details))
            _details[key] = details = (ProcessQuery.ImagePath(process.Pid), ProcessQuery.CommandLine(process.Pid));
        return details;
    }

    /// <summary>"npm run dev" rather than "cmd /c npm run dev".</summary>
    private static string LauncherText(ProcessEntry launcher, string? commandLine)
    {
        if (commandLine is null) return System.IO.Path.GetFileNameWithoutExtension(launcher.Name);
        if (!launcher.Name.Equals("cmd.exe", StringComparison.OrdinalIgnoreCase)) return CommandLineText.Short(commandLine, launcher.Name);

        var words = CommandLineText.Split(commandLine).Skip(1)
            .SkipWhile(w => w.StartsWith('/'))
            .Select(w => w.Contains('\\') ? System.IO.Path.GetFileNameWithoutExtension(w) : w)
            .Take(4);
        string text = string.Join(' ', words);
        return text.Length == 0 ? "cmd" : text;
    }
}
