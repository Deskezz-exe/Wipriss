using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Wipriss.Cleaner;

/// <summary>
/// What to delete, as plain text: paths, «code|key», «code|key|value», service names, task paths. Written by
/// Wipriss.Cleaner.exe for Wipriss.Helper.exe (administrator), which checks every line again with
/// <see cref="Purge"/>'s rules before it touches anything.
/// </summary>
public sealed class PurgeJob
{
    public List<string> Paths { get; set; } = [];
    public List<string> Keys { get; set; } = [];
    public List<string> Values { get; set; } = [];
    public List<string> Services { get; set; } = [];
    public List<string> Tasks { get; set; } = [];

    [JsonIgnore]
    public int Count => Paths.Count + Keys.Count + Values.Count + Services.Count + Tasks.Count;
}

[JsonSerializable(typeof(PurgeJob))]
internal sealed partial class PurgeJson : JsonSerializerContext;

/// <summary>
/// Deleting tails of a removed program, and the rules that hold whoever asks (the cleaner or the
/// administrator helper): no forbidden or protected folder, no link, no key outside the places programs
/// keep their own records, no service of Windows, no task of Microsoft.
/// </summary>
internal static partial class Purge
{
    public const string HelperCommand = "purge", HelperRestore = "restore";
    public const int HelperInvalid = 0xFFFF;

    // ---------- rules ----------

    /// <summary>Names in Classes that are Windows' own: never offered, whatever they point to.</summary>
    internal static readonly HashSet<string> SystemClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "*", "AllFilesystemObjects", "Applications", "AppID", "AppX", "CLSID", "Directory", "Drive", "Folder", "Interface", "Installer",
        "Local Settings", "MIME", "SystemFileAssociations", "TypeLib", "Unknown", "Wow6432Node", "DesktopBackground", "DirectShow",
        "Media Type", "MediaFoundation", "PackagedCom", "ActivatableClasses", "Extensions", "exefile", "txtfile", "lnkfile", "batfile",
        "cmdfile", "comfile", "dllfile", "htmlfile", "http", "https", "mailto", "ftp", "InternetShortcut", "Network", "Printers",
        "inifile", "regfile", "VBSFile", "JSFile", "WSFFile", "Msi.Package", "Msi.Patch", "Paint.Picture", "giffile", "jpegfile", "pngfile",
        "CompressedFolder", "SearchFolder", "LibraryFolder", "ms-settings", "Protocols", "Filter", "PROTOCOLS", "Component Categories",
        "Record", "FileType", "Elevation", "DeviceDisplayObject",
    };


    /// <summary>A path a removed program's tail may be: not forbidden, and not protected unless it is a shortcut place.</summary>
    public static bool Offerable(string? path)
    {
        var normal = PathText.Normalize(path);
        if (normal is null || normal.Length <= 3 || Deleter.IsForbidden(normal)) return false;
        return ShortcutPlace(normal) || !Verdict.IsProtected(normal);
    }

    /// <summary>
    /// Inside a Start-menu «Programs» folder (a program's own sub-folder or shortcut), or a shortcut right on the
    /// desktop, the taskbar or in autostart. These live under the protected «Microsoft» folders, but belong to programs.
    /// </summary>
    internal static bool ShortcutPlace(string normal)
    {
        foreach (var menu in new[] { Environment.SpecialFolder.Programs, Environment.SpecialFolder.CommonPrograms })
        {
            var root = PathText.Normalize(Environment.GetFolderPath(menu));
            if (root is not null && normal.Length > root.Length && PathText.IsInside(normal, root)) return true;
        }
        if (!normal.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)) return false;
        var folder = Path.GetDirectoryName(normal);
        foreach (var place in new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.Startup), Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup),
            Path.Combine(Places.Roaming, @"Microsoft\Internet Explorer\Quick Launch"),
            Path.Combine(Places.Roaming, @"Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar"),
        })
            if (PathText.Normalize(place) is { } p && string.Equals(folder, p, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    public static bool PathAllowed(string? path)
    {
        var normal = PathText.Normalize(path);
        if (normal is null || !Offerable(normal)) return false;
        try { return (File.GetAttributes(normal) & FileAttributes.ReparsePoint) == 0; }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return true; } // already gone
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return true; } // the deleter decides
    }

    private static readonly string[] SoftwareDeny = ["Classes", "Clients", "Policies", "RegisteredApplications", "WOW6432Node", "Wipriss"];

    /// <summary>Keys a program keeps for itself. <paramref name="path"/> is relative to the hive («Software\Foo»).</summary>
    public static bool KeyAllowed(RegRoot root, string path)
    {
        if (!Segments(path, out var s) || s.Length < 2 || !s[0].Equals("Software", StringComparison.OrdinalIgnoreCase)) return false;
        bool Is(int i, string text) => s.Length > i && s[i].Equals(text, StringComparison.OrdinalIgnoreCase);
        bool Packed(int i) => s.Length == i + 1 && PackedGuid().IsMatch(s[i]);

        if (Is(1, "Classes"))
        {
            if (s.Length < 3) return false;
            if (Is(2, "CLSID") || Is(2, "TypeLib")) return s.Length == 4 && Guid.TryParseExact(s[3], "B", out _);
            if (Is(2, "AppID")) return s.Length == 4 && (Guid.TryParseExact(s[3], "B", out _) || s[3].EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
            if (Is(2, "Installer")) return (Is(3, "Products") || Is(3, "Features")) && Packed(4);
            return s.Length == 3 && !s[2].StartsWith('.') && !SystemClasses.Contains(s[2])
                   && !s[2].StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase) && !s[2].StartsWith("Windows", StringComparison.OrdinalIgnoreCase);
        }
        if (Is(1, "Microsoft"))
        {
            if (Is(2, "Windows") && Is(3, "CurrentVersion"))
            {
                if (Is(4, "Uninstall") || Is(4, "App Paths")) return s.Length == 6;
                if (Is(4, "Installer") && Is(5, "UserData") && Is(6, "S-1-5-18") && Is(7, "Products")) return root.IsMachine && Packed(8);
            }
            if (Is(2, "Installer") && (Is(3, "Products") || Is(3, "Features"))) return !root.IsMachine && Packed(4);
            return false;
        }
        return !SoftwareDeny.Contains(s[1], StringComparer.OrdinalIgnoreCase) && !s[1].StartsWith("Windows", StringComparison.OrdinalIgnoreCase);
    }

    private static readonly string[] ValueKeys =
    [
        @"Software\Microsoft\Windows\CurrentVersion\Run", @"Software\Microsoft\Windows\CurrentVersion\RunOnce",
        @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run", @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run32",
    ];

    public static bool ValueAllowed(string key, string name) =>
        name.Length > 0 && ValueKeys.Contains(key, StringComparer.OrdinalIgnoreCase);

    public static bool TaskAllowed(string path) =>
        path.StartsWith('\\') && path.Length > 1 && !path.StartsWith(@"\Microsoft\", StringComparison.OrdinalIgnoreCase)
        && !path.Contains('"') && !path.Contains("..", StringComparison.Ordinal) && path.All(c => c >= ' ');

    [GeneratedRegex(@"^[A-Za-z0-9_.\- ]{1,128}$")]
    private static partial Regex ServiceName();

    /// <summary>A service that is not Windows': its program lives outside the Windows folder.</summary>
    public static bool ServiceAllowed(string name)
    {
        if (!ServiceName().IsMatch(name)) return false;
        using var service = RegRoot.Machine64.Open(@"SYSTEM\CurrentControlSet\Services\" + name);
        if (service is null) return false;
        using var parameters = Reg.SafeOpen(service, "Parameters");
        var image = PathText.ExePath(Reg.Text(service, "ImagePath")) ?? PathText.ExePath(Reg.Text(parameters, "ServiceDll"));
        return image is not null && !PathText.IsInside(image, Places.Windows) && !Deleter.IsForbidden(image);
    }

    [GeneratedRegex("^[0-9A-F]{32}$")]
    private static partial Regex PackedGuid();

    private static bool Segments(string path, out string[] segments)
    {
        segments = path.Split('\\');
        return segments.Length > 0 && segments.All(s => s.Length > 0 && s != "." && s != ".." && s.All(c => c >= ' '));
    }

    // ---------- the job ----------

    public static PurgeJob ToJob(IEnumerable<TailItem> tails)
    {
        var job = new PurgeJob();
        foreach (var tail in tails)
        {
            var place = tail.Id.Length > 2 ? tail.Id[2..] : "";
            switch (tail.Kind)
            {
                case TailKind.Folder or TailKind.File: job.Paths.Add(place); break;
                case TailKind.RegistryKey: job.Keys.Add(place); break;
                case TailKind.RegistryValue: job.Values.Add(place); break;
                case TailKind.Service: job.Services.Add(place); break;
                case TailKind.Task: job.Tasks.Add(place); break;
            }
        }
        return job;
    }

    /// <summary>
    /// Deletes what the rules allow. Without administrator rights, what needs them is returned in
    /// <paramref name="forAdmin"/> (paths outside the profile that refused, HKLM, services, tasks that refused).
    /// </summary>
    public static (long Freed, int Failed) Run(PurgeJob job, bool elevated, PurgeJob? forAdmin, CancellationToken cancel)
    {
        long freed = 0;
        int failed = 0;
        foreach (var path in job.Paths)
        {
            cancel.ThrowIfCancellationRequested();
            if (!PathAllowed(path)) { failed++; continue; }
            var item = new CleanItem { Action = CleanAction.Whole, Color = CleanColor.Green, Paths = [path] };
            var (f, bad) = Deleter.Delete([item], _ => { }, cancel);
            freed += f;
            if (bad == 0 || !Exists(path)) continue;
            if (!elevated && forAdmin is not null && !PathText.IsInside(PathText.Normalize(path)!, Places.Profile)) forAdmin.Paths.Add(path);
            else failed += bad;
        }

        foreach (var line in job.Keys)
        {
            var parts = line.Split('|');
            if (parts.Length != 2 || RegRoot.Parse(parts[0]) is not { } root || !KeyAllowed(root, parts[1])) { failed++; continue; }
            if (root.IsMachine && !elevated) { forAdmin?.Keys.Add(line); if (forAdmin is null) failed++; continue; }
            if (!DeleteKey(root, parts[1])) failed++;
        }

        foreach (var line in job.Values)
        {
            var parts = line.Split('|');
            if (parts.Length != 3 || RegRoot.Parse(parts[0]) is not { } root || !ValueAllowed(parts[1], parts[2])) { failed++; continue; }
            if (root.IsMachine && !elevated) { forAdmin?.Values.Add(line); if (forAdmin is null) failed++; continue; }
            try
            {
                using var key = root.Open(parts[1], writable: true);
                key?.DeleteValue(parts[2], throwOnMissingValue: false);
            }
            catch (Exception ex) when (Reg.Expected(ex)) { failed++; }
        }

        foreach (var name in job.Services)
        {
            if (!ServiceAllowed(name)) { if (RegRoot.Machine64.Exists(@"SYSTEM\CurrentControlSet\Services\" + name)) failed++; continue; }
            if (!elevated) { forAdmin?.Services.Add(name); if (forAdmin is null) failed++; continue; }
            if (!DeleteService(name)) failed++;
        }

        foreach (var task in job.Tasks)
        {
            if (!TaskAllowed(task)) { failed++; continue; }
            if (Run("schtasks.exe", $"/Delete /TN \"{task}\" /F") == 0) continue;
            if (!elevated && forAdmin is not null) forAdmin.Tasks.Add(task);
            else failed++;
        }
        return (freed, failed);
    }

    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    private static bool DeleteKey(RegRoot root, string path)
    {
        try
        {
            using var hive = root.OpenBase();
            hive.DeleteSubKeyTree(path, throwOnMissingSubKey: false);
            return true;
        }
        catch (Exception ex) when (Reg.Expected(ex)) { return false; }
    }

    private static bool DeleteService(string name)
    {
        var manager = OpenSCManager(null, null, 0x1 /* CONNECT */);
        if (manager == IntPtr.Zero) return false;
        try
        {
            var service = OpenService(manager, name, 0x10000 /* DELETE */ | 0x20 /* STOP */ | 0x4 /* QUERY_STATUS */);
            if (service == IntPtr.Zero) return Marshal.GetLastWin32Error() == 1060; // already gone
            try
            {
                var status = new ServiceStatus();
                ControlService(service, 1 /* STOP */, ref status);
                for (int i = 0; i < 20 && QueryServiceStatus(service, ref status) && status.CurrentState != 1 /* STOPPED */; i++) Thread.Sleep(250);
                return DeleteServiceNative(service) || Marshal.GetLastWin32Error() == 1072; // marked for deletion already
            }
            finally { CloseServiceHandle(service); }
        }
        finally { CloseServiceHandle(manager); }
    }

    /// <summary>Runs a Windows tool from System32 without a window; its exit code, or -1.</summary>
    internal static int Run(string tool, string arguments, int timeoutMs = 60_000)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), tool), arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (process is null) return -1;
            process.StandardOutput.ReadToEndAsync();
            process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(timeoutMs)) { try { process.Kill(); } catch (InvalidOperationException) { } return -1; }
            return process.ExitCode;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException) { return -1; }
    }

    /// <summary>Asks for administrator rights and lets the helper do <paramref name="job"/>. Failed count; all of it when the owner said no.</summary>
    public static int RunElevated(PurgeJob job)
    {
        if (job.Count == 0) return 0;
        var file = Path.Combine(Path.GetTempPath(), $"wipriss-purge-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(file, JsonSerializer.Serialize(job, PurgeJson.Default.PurgeJob), new UTF8Encoding(false));
            int code = RunHelper($"{HelperCommand} \"{file}\"");
            return code is < 0 or HelperInvalid ? job.Count : Math.Min(code, job.Count);
        }
        catch (IOException) { return job.Count; }
        finally { try { File.Delete(file); } catch (IOException) { } }
    }

    /// <summary>Runs Wipriss.Helper.exe through UAC; its exit code, or -1 (no helper, or «No» in the prompt).</summary>
    internal static int RunHelper(string arguments)
    {
        var helper = Path.Combine(AppContext.BaseDirectory, "Wipriss.Helper.exe");
        if (!File.Exists(helper)) return -1;
        try
        {
            using var process = Process.Start(new ProcessStartInfo(helper, arguments) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden });
            if (process is null) return -1;
            process.WaitForExit();
            return process.ExitCode;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException) { return -1; }
    }

    /// <summary>The helper's side: read the job, check every line again, delete. Exit code = what failed.</summary>
    public static int HelperMain(string file)
    {
        var normal = PathText.Normalize(file);
        if (normal is null || !PathText.IsInside(normal, Path.GetTempPath().TrimEnd('\\'))
            || !Path.GetFileName(normal).StartsWith("wipriss-purge-", StringComparison.Ordinal) || !File.Exists(normal)) return HelperInvalid;
        PurgeJob? job;
        try { job = JsonSerializer.Deserialize(File.ReadAllText(normal), PurgeJson.Default.PurgeJob); }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return HelperInvalid; }
        if (job is null || job.Count > 2000) return HelperInvalid;
        var (_, failed) = Run(job, elevated: true, forAdmin: null, CancellationToken.None);
        return Math.Min(failed, 0xFFFE);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus
    {
        public int ServiceType, CurrentState, ControlsAccepted, Win32ExitCode, ServiceSpecificExitCode, CheckPoint, WaitHint;
    }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "OpenSCManagerW")]
    private static extern IntPtr OpenSCManager(string? machine, string? database, int access);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "OpenServiceW")]
    private static extern IntPtr OpenService(IntPtr manager, string name, int access);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool ControlService(IntPtr service, int control, ref ServiceStatus status);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool QueryServiceStatus(IntPtr service, ref ServiceStatus status);

    [DllImport("advapi32.dll", SetLastError = true, EntryPoint = "DeleteService")]
    private static extern bool DeleteServiceNative(IntPtr service);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool CloseServiceHandle(IntPtr handle);
}
