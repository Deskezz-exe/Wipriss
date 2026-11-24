using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Wipriss.Core.Text;
using Wipriss.ProcessTools;

namespace Wipriss.Tests;

public class CommandLineTextTests
{
    [Theory]
    [InlineData("\"C:\\Program Files\\nodejs\\node.exe\" \"C:\\proj\\node_modules\\vite\\bin\\vite.js\"", "node.exe", "node vite")]
    [InlineData("node \"C:\\Program Files\\nodejs\\node_modules\\npm\\bin\\npm-cli.js\" run dev", "node.exe", "node npm run dev")]
    [InlineData("node C:\\p\\node_modules\\@angular\\cli\\bin\\ng.js serve", "node.exe", "node @angular/cli serve")]
    [InlineData("node --inspect server.js", "node.exe", "node server.js")]
    [InlineData("python -m uvicorn main:app --reload", "python.exe", "python uvicorn")]
    [InlineData("python manage.py runserver 0.0.0.0:8000", "python.exe", "python manage.py runserver")]
    [InlineData("java -jar C:\\apps\\app.jar", "java.exe", "java app.jar")]
    [InlineData("\"C:\\Apps\\Code.exe\" --type=renderer", "Code.exe", "Code")]
    [InlineData("node", "node.exe", "node")]
    [InlineData(null, "node.exe", "node")]
    public void Short_names_what_runs(string? commandLine, string image, string expected) =>
        Assert.Equal(expected, CommandLineText.Short(commandLine, image));

    [Theory]
    [InlineData("C:\\p\\node_modules\\.bin\\next.cmd", "next")]
    [InlineData("C:/p/node_modules/vite/bin/vite.js", "vite")]
    [InlineData("C:\\app\\manage.py", "manage.py")]
    [InlineData("run", "run")]
    public void Script_name_is_the_package_or_file(string arg, string expected) =>
        Assert.Equal(expected, CommandLineText.ScriptName(arg));

    [Fact]
    public void Empty_command_line_splits_to_nothing() => Assert.Empty(CommandLineText.Split(""));

    [Fact]
    public void Split_follows_windows_quoting() =>
        Assert.Equal(["a b", "c", "d"], CommandLineText.Split("\"a b\" c d"));
}

public class ProcessTreeTests
{
    private static ProcessEntry P(int pid, int parent, long created, string name = "x.exe") =>
        new(pid, parent, name, 1, created, 0, 0);

    [Fact]
    public void Descendants_follow_parents_and_skip_reused_pids()
    {
        var root = P(10, 1, 100);
        var all = new List<ProcessEntry>
        {
            P(1, 0, 10), root,
            P(11, 10, 110), P(12, 11, 120),
            P(13, 10, 50), // its parent PID 10 belonged to an older process: not a child of root
            P(20, 1, 130),
            P(14, 10, 115, "conhost.exe"), // closes with its program by itself
        };

        var tree = ProcessTree.WithDescendants(all, [root]);

        Assert.Equal([10, 11, 12], tree.Select(p => p.Pid));
    }

    [Fact]
    public void Parent_that_started_later_is_not_the_parent()
    {
        var child = P(30, 31, 100);
        Assert.Null(ProcessTree.ParentOf([child, P(31, 1, 200)], child));
        Assert.Equal(31, ProcessTree.ParentOf([child, P(31, 1, 90)], child)?.Pid);
    }

    [Theory]
    [InlineData("node.exe", null, true)]
    [InlineData("python.exe", "python -m flask run", true)]
    [InlineData("cmd.exe", "C:\\WINDOWS\\system32\\cmd.exe /d /s /c \"npm run dev\"", true)]
    [InlineData("cmd.exe", "cmd /c echo hi", false)]
    [InlineData("explorer.exe", null, false)]
    public void Launchers_are_runtimes_and_package_managers(string image, string? commandLine, bool expected) =>
        Assert.Equal(expected, Launchers.IsLauncher(image, commandLine));
}

public class ProcessGuardTests
{
    [Theory]
    [InlineData("svchost.exe", ProcessKind.Windows)]
    [InlineData("csrss.exe", ProcessKind.Windows)]
    [InlineData("LSASS.EXE", ProcessKind.Windows)]
    [InlineData("com.docker.backend.exe", ProcessKind.Docker)]
    [InlineData("wslrelay.exe", ProcessKind.Wsl)]
    [InlineData("node.exe", ProcessKind.App)]
    public void Kinds_on_this_desktop(string name, ProcessKind expected) =>
        Assert.Equal(expected, ProcessGuard.KindOf(name, 5000, ProcessQuery.CurrentSessionId));

    [Fact]
    public void Other_session_is_a_service() =>
        Assert.Equal(ProcessKind.Service, ProcessGuard.KindOf("postgres.exe", 5000, ProcessQuery.CurrentSessionId + 1));

    [Fact]
    public void Self_and_system_pids_are_protected()
    {
        Assert.Equal(ProcessKind.Self, ProcessGuard.KindOf("testhost.exe", Environment.ProcessId, ProcessQuery.CurrentSessionId));
        Assert.Equal(ProcessKind.Windows, ProcessGuard.KindOf("System", 4, 0));
        Assert.Null(ProcessGuard.WhyNotStop(ProcessKind.App));
        Assert.NotNull(ProcessGuard.WhyNotStop(ProcessKind.Service));
    }
}

public class ElevatedStopTests
{
    [Fact]
    public void Arguments_round_trip()
    {
        var refs = new[] { new ProcessRef(1234, 133_000_000_000_000_000, "a.exe"), new ProcessRef(99, 5, "b.exe") };
        string[] args = ElevatedStop.FormatArguments(refs).Split(' ');

        Assert.True(ElevatedStop.TryParse(args, out var targets));
        Assert.Equal([(1234, 133_000_000_000_000_000L), (99, 5L)], targets);
    }

    [Theory]
    [InlineData("kill 1234:5")]
    [InlineData("stop")]
    [InlineData("stop 4:5")]
    [InlineData("stop 1234:0")]
    [InlineData("stop -1234:5")]
    [InlineData("stop +1234:5")]
    [InlineData("stop 1234:5:6")]
    [InlineData("stop 1234")]
    [InlineData("stop 1234:5 C:\\evil.exe")]
    public void Anything_unexpected_is_rejected(string commandLine) =>
        Assert.False(ElevatedStop.TryParse(commandLine.Split(' '), out _));

    [Fact]
    public void Too_many_targets_are_rejected()
    {
        var args = new List<string> { "stop" };
        args.AddRange(Enumerable.Range(100, ElevatedStop.MaxTargets + 1).Select(i => $"{i}:5"));
        Assert.False(ElevatedStop.TryParse(args, out _));
        Assert.Equal(ElevatedStop.InvalidArguments, ElevatedStop.RunHelper(args));
    }
}

public class LiveProcessTests
{
    private static Process StartPing(string viaShell = "")
    {
        var start = viaShell.Length > 0
            ? new ProcessStartInfo("cmd.exe", "/c ping -n 60 127.0.0.1 >nul")
            : new ProcessStartInfo("ping.exe", "-n 60 127.0.0.1");
        start.CreateNoWindow = true;
        start.UseShellExecute = false;
        start.RedirectStandardOutput = viaShell.Length == 0;
        return Process.Start(start)!;
    }

    private static ProcessEntry Find(int pid)
    {
        for (int i = 0; i < 50; i++)
        {
            var entry = ProcessSnapshot.Take().FirstOrDefault(p => p.Pid == pid);
            if (entry is not null) return entry;
            Thread.Sleep(20);
        }

        throw new InvalidOperationException("Process not in the snapshot.");
    }

    [Fact]
    public void Snapshot_knows_this_process()
    {
        using var self = Process.GetCurrentProcess();
        var entry = Find(Environment.ProcessId);

        Assert.Equal(ProcessQuery.CurrentSessionId, entry.SessionId);
        Assert.True(entry.PrivateBytes > 0);
        Assert.True(Math.Abs(entry.CreateTime - self.StartTime.ToFileTime()) < 100_000); // within 10 ms
        Assert.Equal(self.MainModule!.FileName, ProcessQuery.ImagePath(entry.Pid), ignoreCase: true);
    }

    [Fact]
    public void Wrong_start_time_never_stops_a_process()
    {
        using var ping = StartPing();
        try
        {
            var entry = Find(ping.Id);
            Assert.Contains("-n 60", ProcessQuery.CommandLine(ping.Id));

            Assert.Equal(StopOutcome.Gone, ProcessStopper.Stop(entry.Ref with { CreateTime = entry.CreateTime - 1 }));
            Assert.False(ping.HasExited);

            Assert.Equal(StopOutcome.Stopped, ProcessStopper.Stop(entry.Ref));
            Assert.True(ping.WaitForExit(5000));
        }
        finally
        {
            if (!ping.HasExited) ping.Kill();
        }
    }

    [Fact]
    public void Tree_stop_takes_the_children_too()
    {
        using var shell = StartPing(viaShell: "cmd");
        try
        {
            var root = Find(shell.Id);
            ProcessEntry? child = null;
            for (int i = 0; i < 50 && child is null; i++, Thread.Sleep(20))
                child = ProcessSnapshot.Take().FirstOrDefault(p => p.ParentPid == root.Pid && p.Name.Equals("PING.EXE", StringComparison.OrdinalIgnoreCase));
            Assert.NotNull(child);

            var tree = ProcessTree.WithDescendants(ProcessSnapshot.Take(), [root]);
            Assert.Contains(tree, p => p.Pid == child.Pid);

            var report = ProcessStopper.StopAll(tree.Select(p => p.Ref));
            Assert.True(report.AllDone);
            Assert.True(shell.WaitForExit(5000));
            Assert.DoesNotContain(ProcessSnapshot.Take(), p => p.Pid == child.Pid && p.CreateTime == child.CreateTime);
        }
        finally
        {
            if (!shell.HasExited) shell.Kill(entireProcessTree: true);
        }
    }

    [Fact]
    public void Helper_logic_stops_a_target_and_reports_it()
    {
        using var ping = StartPing();
        try
        {
            var entry = Find(ping.Id);
            int code = ElevatedStop.RunHelper(ElevatedStop.FormatArguments([entry.Ref]).Split(' '));

            Assert.Equal(1, code & 0xFF);
            Assert.Equal(0, (code >> 8) & 0xFF);
            Assert.True(ping.WaitForExit(5000));
        }
        finally
        {
            if (!ping.HasExited) ping.Kill();
        }
    }

    [Fact]
    public void Protected_processes_are_refused()
    {
        var csrss = ProcessSnapshot.Take().First(p => p.Name.Equals("csrss.exe", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(StopOutcome.Refused, ProcessStopper.Stop(csrss.Ref));

        var self = Find(Environment.ProcessId);
        Assert.Equal(StopOutcome.Refused, ProcessStopper.Stop(self.Ref));
    }
}

public class PortTableTests
{
    [Fact]
    public void Tcp_listener_is_found_with_its_process()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var entry = Assert.Single(PortTable.TcpListeners(), e => e.Port == port && e.Pid == Environment.ProcessId);
            Assert.True(entry.IsLoopback);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public void Udp_port_is_found_on_ipv6_too()
    {
        using var udp = new UdpClient(new IPEndPoint(IPAddress.IPv6Any, 0));
        int port = ((IPEndPoint)udp.Client.LocalEndPoint!).Port;
        var entry = Assert.Single(PortTable.Udp(), e => e.Port == port && e.Pid == Environment.ProcessId);
        Assert.True(entry.IsAnyAddress);
    }
}

public class RuTextTests
{
    [Theory]
    [InlineData(20, "только что")]
    [InlineData(5 * 60, "5 мин")]
    [InlineData(3 * 3600 + 120, "3 ч")]
    [InlineData(2 * 86400 + 5, "2 дн")]
    public void Age_is_short(int seconds, string expected) => Assert.Equal(expected, RuText.Age(TimeSpan.FromSeconds(seconds)));

    [Theory]
    [InlineData(500_000L, "<1 МБ")]
    [InlineData(84L * 1024 * 1024, "84 МБ")]
    [InlineData(1258L * 1024 * 1024, "1,2 ГБ")]
    public void Memory_reads_like_task_manager(long bytes, string expected) => Assert.Equal(expected, RuText.Memory(bytes));
}
