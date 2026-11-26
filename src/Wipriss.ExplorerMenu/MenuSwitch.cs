using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace Wipriss.ExplorerMenu;

// Compiled into the modules that show a switch for an Explorer-menu entry (converter, tables).

internal enum MenuState
{
    Off,
    /// <summary>Windows 11: in the new menu (the package is registered for this folder and the title file exists).</summary>
    Modern,
    /// <summary>Only the classic menu ("Show more options", or Windows 10).</summary>
    Classic,
}

internal enum MenuResult { Modern, ModernRestarted, Classic, Off, Declined, Failed }

/// <summary>
/// One entry of Wipriss in Explorer's right-click menu: its title, the file types it shows for, the exe it starts.
/// <paramref name="MinPackage"/> is the first package version whose manifest and DLL carry this entry.
/// </summary>
internal sealed record MenuEntry(string VerbName, string Title, string TitleFileName, string[] Extensions, string ExeName,
    string Arguments, Version MinPackage)
{
    public static readonly MenuEntry Convert = new(
        "Wipriss.Convert", "Изменить через Wipriss", MenuPackage.TitleFileName,
        [".jpg", ".jpeg", ".jfif", ".jpe", ".png", ".webp", ".avif", ".heic", ".heif", ".bmp", ".dib", ".ico", ".tif", ".tiff", ".gif"],
        "Wipriss.exe", "--convert", new Version(1, 0, 0, 0));

    public static readonly MenuEntry Tables = new(
        "Wipriss.Table", "Открыть в Wipriss", MenuPackage.TablesTitleFileName,
        [".xlsx", ".xlsm", ".xlsb", ".xls", ".ods", ".csv", ".tsv", ".tab"],
        MenuPackage.ReaderExeName, "", new Version(1, 2, 0, 0));
}

/// <summary>
/// An entry of Wipriss in Explorer's right-click menu, switched on and off only by the owner's switch in a panel.
/// <list type="bullet">
/// <item>Windows 11: a sparse package with WiprissExplorerMenu.dll (see src/Wipriss.ExplorerMenu), shared by all
/// entries. Registering it needs admin rights once (Wipriss.Helper.exe, UAC prompt). After that a switch only writes
/// or deletes its small title file, which the DLL checks on every right-click; the last switch turned off also
/// removes the package (no admin needed).</item>
/// <item>Fallback and Windows 10: Wipriss's own keys under
/// <c>HKCU\Software\Classes\SystemFileAssociations\&lt;ext&gt;\shell\&lt;verb&gt;</c>; nothing of Windows or other
/// programs is changed, and switching off deletes exactly those keys.</item>
/// </list>
/// Explorer is restarted only when it started before the package was registered (it would not show the entry).
/// </summary>
internal sealed class MenuSwitch(MenuEntry entry)
{
    private const int ErrorCancelled = 1223; // "No" in the UAC prompt

    public MenuEntry Entry => entry;

    private static string Folder => Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);

    private string KeyPath(string ext) => $@"Software\Classes\SystemFileAssociations\{ext}\shell\{entry.VerbName}";

    private string ExePath => Path.Combine(Folder, entry.ExeName);

    private string CommandFor(string exe) =>
        entry.Arguments.Length > 0 ? $"\"{exe}\" {entry.Arguments} \"%1\"" : $"\"{exe}\" \"%1\"";

    private string TitlePath => MenuPackage.TitlePath(entry.TitleFileName);

    /// <summary>The package files were built next to Wipriss.exe (they are not in a plain debug build).</summary>
    public static bool CanUseModern =>
        MenuPackage.IsSupportedOs
        && File.Exists(Path.Combine(Folder, MenuPackage.PackageFileName))
        && File.Exists(Path.Combine(Folder, MenuPackage.DllFileName));

    public MenuState State
    {
        get
        {
            if (File.Exists(TitlePath) && PackageIsHere(entry.MinPackage)) return MenuState.Modern;
            return ClassicIsOn ? MenuState.Classic : MenuState.Off;
        }
    }

    public bool IsOn => State != MenuState.Off;

    private bool ClassicIsOn
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(KeyPath(entry.Extensions[0]));
                return key is not null;
            }
            catch (Exception) { return false; }
        }
    }

    /// <summary>Switches the entry on: the new menu if possible, else the classic one. May show a UAC prompt.</summary>
    public async Task<MenuResult> TurnOnAsync()
    {
        if (!CanUseModern) return SetClassic(true) ? MenuResult.Classic : MenuResult.Failed;

        if (!PackageIsHere(entry.MinPackage))
        {
            // Not registered, registered for another folder, or an older package without this entry.
            bool? added = await RunHelperAsync().ConfigureAwait(false);
            if (added != true || !PackageIsHere(entry.MinPackage))
            {
                // The owner said no, or Windows refused: the classic menu still works without admin rights.
                bool classic = SetClassic(true);
                if (added is null) return classic ? MenuResult.Declined : MenuResult.Failed;
                return classic ? MenuResult.Classic : MenuResult.Failed;
            }
        }

        if (!WriteTitle()) return MenuResult.Failed;
        // Windows also lists packaged commands under "Show more options", so the classic item would be there twice.
        SetClassic(false);

        // Explorer learns about packaged menu commands only when it starts: restart it once if it is older than
        // the package, so the item shows at once without the owner doing anything.
        if (RegisteredAt() is { } registered && ExplorerRestart.IsNeeded(registered))
        {
            await Task.Run(ExplorerRestart.Run).ConfigureAwait(false);
            return MenuResult.ModernRestarted;
        }
        return MenuResult.Modern;
    }

    /// <summary>Hides the entry at once, then removes its keys, and the package when no other entry uses it.</summary>
    public async Task<MenuResult> TurnOffAsync()
    {
        bool ok = DeleteTitle();
        ok &= SetClassic(false);
        bool othersOn = MenuPackage.TitleFileNames.Any(name => name != entry.TitleFileName && File.Exists(MenuPackage.TitlePath(name)));
        if (!othersOn && PackageRegistered())
        {
            await RunPowerShellAsync(MenuPackage.RemoveScript()).ConfigureAwait(false);
            ok &= !PackageRegistered();
        }
        return ok ? MenuResult.Off : MenuResult.Failed;
    }

    /// <summary>
    /// Wipriss was moved or updated to another folder: the classic item would start an old path. Runs at start-up and
    /// only rewrites when the path really changed. (A package of another folder shows as "off": switching on again
    /// registers it for this folder.)
    /// </summary>
    public void RefreshPath()
    {
        try
        {
            using var command = Registry.CurrentUser.OpenSubKey(KeyPath(entry.Extensions[0]) + @"\command");
            if (command?.GetValue(null) is string current && current != CommandFor(ExePath)) WriteClassic();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException) { }
    }

    // ---- Classic menu (registry) ----

    private bool SetClassic(bool on)
    {
        try
        {
            if (on) WriteClassic();
            else
            {
                foreach (var ext in entry.Extensions)
                    Registry.CurrentUser.DeleteSubKeyTree(KeyPath(ext), throwOnMissingSubKey: false);
            }
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            return false;
        }
    }

    private void WriteClassic()
    {
        string exe = ExePath;
        if (!Path.IsPathFullyQualified(exe) || !File.Exists(exe)) throw new IOException("exe not found");
        foreach (var ext in entry.Extensions)
        {
            using var verb = Registry.CurrentUser.CreateSubKey(KeyPath(ext));
            verb.SetValue("MUIVerb", entry.Title);
            verb.SetValue("Icon", $"\"{Path.Combine(Folder, "Wipriss.exe")}\",0");
            // One launch per selected file, and the item stays visible for more than 15 files.
            verb.SetValue("MultiSelectModel", "Player");
            using var command = verb.CreateSubKey("command");
            command.SetValue(null, CommandFor(exe));
        }
    }

    // ---- New menu (package) ----

    private bool WriteTitle()
    {
        try
        {
            string path = TitlePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, entry.Title + "\r\n", new UTF8Encoding(false));
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { return false; }
    }

    private bool DeleteTitle()
    {
        try
        {
            File.Delete(TitlePath);
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { return false; }
    }

    /// <summary>Registered for this account with this folder as the place of the DLL, and new enough.</summary>
    private static bool PackageIsHere(Version minimum)
    {
        try
        {
            return FindPackage() is { } fullName && ExternalLocation(fullName) is { } location
                && string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(location)), Folder, StringComparison.OrdinalIgnoreCase)
                && MenuPackage.VersionOf(fullName) is { } version && version >= minimum;
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException) { return false; }
    }

    /// <returns>True when done, false when Windows refused, null when the owner said no in the UAC prompt.</returns>
    private static async Task<bool?> RunHelperAsync()
    {
        string helper = Path.Combine(Folder, "Wipriss.Helper.exe");
        if (!File.Exists(helper)) return false;
        var start = new ProcessStartInfo(helper, "menu-add") { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
        try
        {
            using var process = Process.Start(start);
            if (process is null) return false;
            await process.WaitForExitAsync().ConfigureAwait(false);
            return process.ExitCode == 0;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            return null;
        }
    }

    private static async Task RunPowerShellAsync(string script)
    {
        var start = new ProcessStartInfo(MenuPackage.PowerShellPath, MenuPackage.PowerShellArguments(script))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        try
        {
            using var process = Process.Start(start);
            if (process is null) return;
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            try { await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { try { process.Kill(); } catch (InvalidOperationException) { } }
        }
        catch (Win32Exception) { }
    }

    /// <summary>When the package was registered: the creation time of its folder under WindowsApps.</summary>
    private static DateTime? RegisteredAt()
    {
        try
        {
            if (FindPackage() is not { } fullName || InstallPath(fullName) is not { } path || !Directory.Exists(path)) return null;
            return Directory.GetCreationTime(path);
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool PackageRegistered()
    {
        try { return FindPackage() is not null; }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException) { return false; }
    }

    private static string? _familyName;

    private static string? FamilyName => _familyName ??= ComputeFamilyName();

    /// <summary>The full name of the package registered for this account, or null.</summary>
    private static unsafe string? FindPackage()
    {
        if (!MenuPackage.IsSupportedOs || FamilyName is not { } family) return null;
        uint count = 0, length = 0;
        if (GetPackagesByPackageFamily(family, ref count, null, ref length, null) != ErrorInsufficientBuffer || count == 0) return null;
        var names = new nint[count];
        var buffer = new char[length];
        fixed (nint* pNames = names)
        fixed (char* pBuffer = buffer)
        {
            if (GetPackagesByPackageFamily(family, ref count, (char**)pNames, ref length, pBuffer) != 0 || count == 0) return null;
            return new string((char*)pNames[0]);
        }
    }

    private static string? ExternalLocation(string fullName) => PackagePath(fullName, PackagePathTypeEffectiveExternal);

    private static string? InstallPath(string fullName) => PackagePath(fullName, PackagePathTypeInstall);

    private static unsafe string? PackagePath(string fullName, int type)
    {
        uint length = 0;
        if (GetPackagePathByFullName2(fullName, type, ref length, null) != ErrorInsufficientBuffer || length == 0)
            return null;
        var buffer = new char[length];
        fixed (char* pBuffer = buffer)
            return GetPackagePathByFullName2(fullName, type, ref length, pBuffer) == 0 ? new string(pBuffer) : null;
    }

    private static unsafe string? ComputeFamilyName()
    {
        if (!MenuPackage.IsSupportedOs) return null;
        fixed (char* name = MenuPackage.PackageName)
        fixed (char* publisher = MenuPackage.Publisher)
        {
            var id = new PackageId { Name = name, Publisher = publisher };
            uint length = 0;
            if (PackageFamilyNameFromId(&id, ref length, null) != ErrorInsufficientBuffer) return null;
            var buffer = new char[length];
            fixed (char* pBuffer = buffer)
                return PackageFamilyNameFromId(&id, ref length, pBuffer) == 0 ? new string(pBuffer) : null;
        }
    }

    private const int ErrorInsufficientBuffer = 122;
    private const int PackagePathTypeInstall = 0;
    private const int PackagePathTypeEffectiveExternal = 5;

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct PackageId
    {
        public uint Reserved;
        public uint ProcessorArchitecture;
        public ulong Version;
        public char* Name;
        public char* Publisher;
        public char* ResourceId;
        public char* PublisherId;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern unsafe int GetPackagesByPackageFamily(string family, ref uint count, char** names, ref uint length, char* buffer);

    // Not exported by kernel32.dll (only by kernelbase.dll): imported from its API set, Windows 10 1903 and later.
    [DllImport("api-ms-win-appmodel-runtime-l1-1-3.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern unsafe int GetPackagePathByFullName2(string fullName, int type, ref uint length, char* path);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern unsafe int PackageFamilyNameFromId(PackageId* id, ref uint length, char* name);
}
