using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace Wipriss.Cleaner;

/// <summary>
/// Runs a program's own uninstaller and waits until it and everything it started have finished (an
/// NSIS or Inno uninstaller copies itself to Temp, starts the copy and exits at once). The owner follows
/// its windows; Windows asks for administrator rights itself when the uninstaller needs them.
/// </summary>
internal static partial class Uninstall
{
    private static string System32 => Environment.GetFolderPath(Environment.SpecialFolder.System);

    /// <summary>
    /// The command to run: for a Windows Installer product «msiexec /X{code}» (its record often says /I,
    /// which opens the «change» dialog), otherwise the interactive uninstall string, otherwise the quiet one.
    /// </summary>
    public static string? Command(string? uninstall, string? quiet, string? msiProductCode)
    {
        if (msiProductCode is not null) return $"\"{Path.Combine(System32, "msiexec.exe")}\" /X{msiProductCode}";
        if (!string.IsNullOrWhiteSpace(uninstall))
        {
            var msi = MsiCommand().Match(uninstall);
            if (msi.Success) return $"\"{Path.Combine(System32, "msiexec.exe")}\" /X{msi.Groups[1].Value}";
            return uninstall.Trim();
        }
        return string.IsNullOrWhiteSpace(quiet) ? null : quiet.Trim();
    }

    [GeneratedRegex(@"^\s*""?(?:[A-Za-z]:\\[^""]*\\)?msiexec(?:\.exe)?""?\s+/[IX]\s*(\{[0-9A-Fa-f\-]{36}\})", RegexOptions.IgnoreCase)]
    private static partial Regex MsiCommand();

    /// <summary>«"C:\A b\un.exe" /x» → (C:\A b\un.exe, /x); «C:\A b\un.exe /S» → (C:\A b\un.exe, /S); «RunDll32 x,y» → (RunDll32, x,y).</summary>
    public static (string File, string Arguments)? Split(string command)
    {
        var text = command.Trim();
        if (text.Length == 0) return null;
        if (text[0] == '"')
        {
            int end = text.IndexOf('"', 1);
            if (end < 2) return null;
            return (text[1..end], text[(end + 1)..].Trim());
        }
        var exe = PathText.ExePath(text);
        if (exe is not null && text.StartsWith(exe, StringComparison.OrdinalIgnoreCase))
            return (exe, text[exe.Length..].Trim());
        int space = text.IndexOf(' ');
        return space < 0 ? (text, "") : (text[..space], text[(space + 1)..].Trim());
    }

    /// <summary>Starts the uninstaller. False when it could not start (the file is gone) or the owner said no to UAC.</summary>
    public static bool Start(string command, out Process? process, out bool refused)
    {
        process = null;
        refused = false;
        if (Split(command) is not { } parts) return false;
        try
        {
            process = Process.Start(new ProcessStartInfo(parts.File, parts.Arguments) { UseShellExecute = true });
            return process is not null;
        }
        catch (Win32Exception ex)
        {
            refused = ex.NativeErrorCode == 1223; // «No» in the UAC prompt
            return false;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException) { return false; }
    }

    /// <summary>Removes a Store app for this user (Windows' own PowerShell command, the name checked first).</summary>
    public static bool RemovePackage(string fullName, CancellationToken cancel)
    {
        if (!ProgramList.ValidPackageName(fullName)) return false;
        var powershell = Path.Combine(System32, @"WindowsPowerShell\v1.0\powershell.exe");
        try
        {
            using var process = Process.Start(new ProcessStartInfo(powershell,
                $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"Remove-AppxPackage -Package '{fullName}'\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (process is null) return false;
            while (!process.WaitForExit(300)) cancel.ThrowIfCancellationRequested();
            return process.ExitCode == 0;
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException) { return false; }
    }

    /// <summary>
    /// Waits until the uninstaller and all processes it started (and their children) have exited, or until
    /// <paramref name="stop"/> is set. Looks at the process list twice a second, only while an uninstall runs.
    /// </summary>
    public static void WaitForTree(Process root, Func<bool> stop, CancellationToken cancel)
    {
        var tracked = new Dictionary<int, long>(); // pid → creation time
        long rootCreated = CreationTime(root.Id);
        tracked[root.Id] = rootCreated;
        int quiet = 0;
        while (true)
        {
            cancel.ThrowIfCancellationRequested();
            if (stop()) return;
            Thread.Sleep(500);
            var all = Snapshot();
            var alive = all.Where(p => tracked.TryGetValue(p.Pid, out var created) && (created == 0 || CreationTime(p.Pid) == created)).Select(p => p.Pid).ToHashSet();
            // Children (and their children) of what we follow, started after their parent. A parent that
            // exited since the last look still counts: its child was started while it ran.
            bool added;
            do
            {
                added = false;
                foreach (var (pid, parent) in all)
                {
                    if (tracked.ContainsKey(pid) || !tracked.TryGetValue(parent, out var parentCreated)) continue;
                    long created = CreationTime(pid);
                    if (created != 0 && parentCreated != 0 && created < parentCreated) continue; // an old process with a reused parent id
                    tracked[pid] = created;
                    alive.Add(pid);
                    added = true;
                }
            } while (added);
            foreach (var gone in tracked.Keys.Where(k => !alive.Contains(k)).ToList()) tracked.Remove(gone);
            quiet = tracked.Count == 0 ? quiet + 1 : 0;
            if (quiet >= 2) return; // nothing left for a whole second
        }
    }

    private static List<(int Pid, int Parent)> Snapshot()
    {
        var list = new List<(int, int)>();
        var snap = CreateToolhelp32Snapshot(2 /* TH32CS_SNAPPROCESS */, 0);
        if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return list;
        try
        {
            var entry = new ProcessEntry { Size = Marshal.SizeOf<ProcessEntry>() };
            if (!Process32First(snap, ref entry)) return list;
            do list.Add((entry.ProcessId, entry.ParentProcessId));
            while (Process32Next(snap, ref entry));
        }
        finally { CloseHandle(snap); }
        return list;
    }

    private static long CreationTime(int pid)
    {
        var handle = OpenProcess(0x1000 /* QUERY_LIMITED_INFORMATION */, false, pid);
        if (handle == IntPtr.Zero) return 0;
        try { return GetProcessTimes(handle, out var created, out _, out _, out _) ? created : 0; }
        finally { CloseHandle(handle); }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        public int Size;
        public int Usage;
        public int ProcessId;
        public IntPtr DefaultHeapId;
        public int ModuleId;
        public int Threads;
        public int ParentProcessId;
        public int PriorityClassBase;
        public int Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(int flags, int processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "Process32FirstW")]
    private static extern bool Process32First(IntPtr snapshot, ref ProcessEntry entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "Process32NextW")]
    private static extern bool Process32Next(IntPtr snapshot, ref ProcessEntry entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(int access, bool inherit, int pid);

    [DllImport("kernel32.dll")]
    private static extern bool GetProcessTimes(IntPtr process, out long creation, out long exit, out long kernel, out long user);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
}
