namespace Wipriss.ProcessTools;

/// <summary>What a process is, as far as stopping it goes.</summary>
public enum ProcessKind
{
    /// <summary>A program the user runs. The only kind Wipriss may stop.</summary>
    App,
    /// <summary>A core part of Windows.</summary>
    Windows,
    /// <summary>Runs outside the user's desktop session: a service or another user's process.</summary>
    Service,
    Docker,
    Wsl,
    /// <summary>Wipriss itself.</summary>
    Self,
}

/// <summary>
/// Decides which processes may be stopped. Everything except plain user programs is off limits:
/// core Windows processes, services, Docker and WSL (they forward ports for containers and Linux),
/// and Wipriss itself.
/// </summary>
public static class ProcessGuard
{
    private static readonly HashSet<string> WindowsNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Idle", "System", "Secure System", "Registry", "Memory Compression",
        "smss.exe", "csrss.exe", "wininit.exe", "winlogon.exe", "services.exe", "lsass.exe", "LsaIso.exe",
        "svchost.exe", "dwm.exe", "fontdrvhost.exe", "sihost.exe", "explorer.exe", "ctfmon.exe",
        "spoolsv.exe", "lsm.exe", "MsMpEng.exe", "NisSrv.exe", "SecurityHealthService.exe",
    };

    private static readonly HashSet<string> DockerNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Docker Desktop.exe", "dockerd.exe", "docker.exe", "vpnkit.exe", "docker-proxy.exe",
    };

    private static readonly HashSet<string> WslNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "wsl.exe", "wslhost.exe", "wslrelay.exe", "wslservice.exe", "vmmem", "vmmemWSL", "vmcompute.exe", "vmwp.exe",
    };

    public static ProcessKind KindOf(ProcessEntry process) => KindOf(process.Name, process.Pid, process.SessionId);

    public static ProcessKind KindOf(string name, int pid, int sessionId)
    {
        if (pid == Environment.ProcessId) return ProcessKind.Self;
        if (pid is 0 or 4 || WindowsNames.Contains(name)) return ProcessKind.Windows;
        if (name.StartsWith("com.docker.", StringComparison.OrdinalIgnoreCase) || DockerNames.Contains(name)) return ProcessKind.Docker;
        if (WslNames.Contains(name)) return ProcessKind.Wsl;
        if (sessionId != ProcessQuery.CurrentSessionId) return ProcessKind.Service;
        return ProcessKind.App;
    }

    /// <summary>Why this kind can't be stopped, in words for the user. Null when it can.</summary>
    public static string? WhyNotStop(ProcessKind kind) => kind switch
    {
        ProcessKind.App => null,
        ProcessKind.Windows => "Часть Windows, её не останавливаем.",
        ProcessKind.Service => "Служба Windows. Остановить её можно в «Службах».",
        ProcessKind.Docker => "Это Docker. Остановите контейнер в Docker.",
        ProcessKind.Wsl => "Порт проброшен из WSL. Остановите программу внутри WSL.",
        ProcessKind.Self => "Это сам Wipriss.",
        _ => "Не останавливаем.",
    };

    /// <summary>Short tag for a list row, or null for plain programs.</summary>
    public static string? Tag(ProcessKind kind) => kind switch
    {
        ProcessKind.Windows => "Windows",
        ProcessKind.Service => "служба",
        ProcessKind.Docker => "Docker",
        ProcessKind.Wsl => "WSL",
        ProcessKind.Self => "Wipriss",
        _ => null,
    };
}
