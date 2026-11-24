using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Wipriss.ProcessTools;

public enum StopOutcome
{
    Stopped,
    /// <summary>Already exited, or its PID now belongs to a different process.</summary>
    Gone,
    /// <summary>Runs with admin rights: stopping it needs the admin helper.</summary>
    NeedsAdmin,
    /// <summary>Protected: Windows, a service, Docker, WSL or Wipriss itself.</summary>
    Refused,
    Failed,
}

/// <summary>What happened when stopping a group of processes.</summary>
public sealed record StopReport(int Stopped, int Gone, IReadOnlyList<ProcessRef> NeedAdmin, int Refused, int Failed)
{
    public bool AllDone => NeedAdmin.Count == 0 && Refused == 0 && Failed == 0;
}

/// <summary>
/// Stops processes. Every target is checked right before it is stopped: same start time (so a reused PID
/// is never hit), same desktop session, not protected by <see cref="ProcessGuard"/>, not critical to Windows.
/// </summary>
public static class ProcessStopper
{
    private const int ERROR_ACCESS_DENIED = 5;
    private const int ERROR_INVALID_PARAMETER = 87;

    public static StopOutcome Stop(ProcessRef target)
    {
        // Name, PID and self first: these checks don't need the process to be reachable.
        if (ProcessGuard.KindOf(target.Name, target.Pid, ProcessQuery.CurrentSessionId) != ProcessKind.App) return StopOutcome.Refused;

        // Only processes on this desktop. If Windows won't even tell the session, the process is gone or protected.
        int session = ProcessQuery.SessionOf(target.Pid);
        if (session < 0) return Marshal.GetLastWin32Error() == ERROR_INVALID_PARAMETER ? StopOutcome.Gone : StopOutcome.Refused;
        if (session != ProcessQuery.CurrentSessionId) return StopOutcome.Refused;

        using var handle = ProcessQuery.Open(target.Pid, ProcessQuery.PROCESS_TERMINATE | ProcessQuery.PROCESS_QUERY_LIMITED_INFORMATION);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            return error switch
            {
                ERROR_INVALID_PARAMETER => StopOutcome.Gone,
                ERROR_ACCESS_DENIED => StopOutcome.NeedsAdmin,
                _ => StopOutcome.Failed,
            };
        }

        if (!ProcessQuery.StartedAt(handle, target.CreateTime)) return StopOutcome.Gone;
        if (IsProcessCritical(handle, out bool critical) && critical) return StopOutcome.Refused;

        if (TerminateProcess(handle, 1)) return StopOutcome.Stopped;
        return Marshal.GetLastWin32Error() == ERROR_ACCESS_DENIED ? StopOutcome.NeedsAdmin : StopOutcome.Failed;
    }

    /// <summary>Stops the targets in order (pass parents first, so they can't restart their children).</summary>
    public static StopReport StopAll(IEnumerable<ProcessRef> targets)
    {
        int stopped = 0, gone = 0, refused = 0, failed = 0;
        var needAdmin = new List<ProcessRef>();
        foreach (var target in targets)
        {
            switch (Stop(target))
            {
                case StopOutcome.Stopped: stopped++; break;
                case StopOutcome.Gone: gone++; break;
                case StopOutcome.NeedsAdmin: needAdmin.Add(target); break;
                case StopOutcome.Refused: refused++; break;
                default: failed++; break;
            }
        }

        return new StopReport(stopped, gone, needAdmin, refused, failed);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateProcess(Microsoft.Win32.SafeHandles.SafeProcessHandle process, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool IsProcessCritical(Microsoft.Win32.SafeHandles.SafeProcessHandle process, out bool critical);
}

/// <summary>
/// Stopping processes that run as administrator. Wipriss itself never runs as admin, so it starts
/// Wipriss.Helper.exe through the usual Windows UAC prompt. The helper takes one fixed command
/// ("stop" plus PID:start-time pairs), checks every target the same way as above, and exits.
/// </summary>
public static class ElevatedStop
{
    public const string Command = "stop";
    public const int MaxTargets = 64;
    public const int InvalidArguments = 0xFFFF;
    public const string HelperFileName = "Wipriss.Helper.exe";

    public static string FormatArguments(IEnumerable<ProcessRef> targets) =>
        Command + " " + string.Join(' ', targets.Select(t => $"{t.Pid}:{t.CreateTime}"));

    /// <summary>Strict parsing: anything unexpected rejects the whole command.</summary>
    public static bool TryParse(IReadOnlyList<string> args, out List<(int Pid, long CreateTime)> targets)
    {
        targets = [];
        if (args.Count < 2 || args.Count > MaxTargets + 1 || args[0] != Command) return false;

        for (int i = 1; i < args.Count; i++)
        {
            string[] parts = args[i].Split(':');
            if (parts.Length != 2
                || !int.TryParse(parts[0], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int pid)
                || !long.TryParse(parts[1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out long created)
                || pid <= 4 || created <= 0)
            {
                targets = [];
                return false;
            }

            targets.Add((pid, created));
        }

        return true;
    }

    /// <summary>
    /// The helper's whole job. Names come from a fresh process list, never from the caller,
    /// so the protection rules can't be talked around.
    /// </summary>
    /// <returns>Exit code: stopped count in the low byte, not stopped in the next byte, or <see cref="InvalidArguments"/>.</returns>
    public static int RunHelper(IReadOnlyList<string> args)
    {
        if (!TryParse(args, out var targets)) return InvalidArguments;

        var all = ProcessSnapshot.Take();
        var refs = new List<ProcessRef>();
        int missing = 0;
        foreach (var (pid, created) in targets)
        {
            var entry = all.FirstOrDefault(p => p.Pid == pid && p.CreateTime == created);
            if (entry is null) missing++;
            else refs.Add(entry.Ref);
        }

        var report = ProcessStopper.StopAll(refs);
        int stopped = report.Stopped + report.Gone + missing;
        int notStopped = report.NeedAdmin.Count + report.Refused + report.Failed;
        return Math.Min(stopped, 255) | (Math.Min(notStopped, 255) << 8);
    }

    /// <summary>Asks Windows for admin rights and runs the helper. Null when the user said no in the UAC prompt.</summary>
    public static async Task<(int Stopped, int NotStopped)?> RunAsync(IReadOnlyList<ProcessRef> targets)
    {
        string helper = Path.Combine(AppContext.BaseDirectory, HelperFileName);
        if (!File.Exists(helper)) return (0, targets.Count);

        var start = new ProcessStartInfo(helper, FormatArguments(targets.Take(MaxTargets)))
        {
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
        };

        try
        {
            using var process = Process.Start(start);
            if (process is null) return (0, targets.Count);
            await process.WaitForExitAsync().ConfigureAwait(false);
            int code = process.ExitCode;
            if (code == InvalidArguments) return (0, targets.Count);
            return (code & 0xFF, (code >> 8) & 0xFF);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) // ERROR_CANCELLED: "No" in the UAC prompt
        {
            return null;
        }
    }
}
