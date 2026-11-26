using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using Wipriss.ExplorerMenu;

namespace Wipriss.Modules.Converter;

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
/// "Изменить через Wipriss" in Explorer's right-click menu for pictures, switched on and off only by the owner's
/// switch in the converter panel.
/// <list type="bullet">
/// <item>Windows 11: a sparse package with WiprissExplorerMenu.dll (see src/Wipriss.ExplorerMenu). Registering it needs
/// admin rights once (Wipriss.Helper.exe, UAC prompt). After that the switch only writes or deletes a small title file,
/// which the DLL checks on every right-click; switching off also removes the package (no admin needed).</item>
/// <item>Fallback and Windows 10: Wipriss's own keys under
/// <c>HKCU\Software\Classes\SystemFileAssociations\&lt;ext&gt;\shell\Wipriss.Convert</c>; nothing of Windows or other
/// programs is changed, and switching off deletes exactly those keys.</item>
/// </list>
/// Explorer is never restarted: it reads the menu anew on the next right-click.
/// </summary>
internal static class ExplorerMenu
{
    private const string VerbName = "Wipriss.Convert";
    public const string Title = "Изменить через Wipriss";
    private const int ErrorCancelled = 1223; // "No" in the UAC prompt

    internal static readonly string[] Extensions =
    [
        ".jpg", ".jpeg", ".jfif", ".jpe", ".png", ".webp", ".avif", ".heic", ".heif",
        ".bmp", ".dib", ".ico", ".tif", ".tiff", ".gif",
    ];

    private static string Folder => Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);

    private static string KeyPath(string ext) => $@"Software\Classes\SystemFileAssociations\{ext}\shell\{VerbName}";

    private static string? ExePath => Environment.ProcessPath;

    private static string CommandFor(string exe) => $"\"{exe}\" --convert \"%1\"";

    /// <summary>The package files were built next to Wipriss.exe (they are not in a plain debug build).</summary>
    public static bool CanUseModern =>
        MenuPackage.IsSupportedOs
        && File.Exists(Path.Combine(Folder, MenuPackage.PackageFileName))
        && File.Exists(Path.Combine(Folder, MenuPackage.DllFileName));

    public static MenuState State
    {
        get
        {
            if (File.Exists(MenuPackage.TitleFilePath) && PackageIsHere()) return MenuState.Modern;
            return ClassicIsOn ? MenuState.Classic : MenuState.Off;
        }
    }

    public static bool IsOn => State != MenuState.Off;

    private static bool ClassicIsOn
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(KeyPath(".png"));
                return key is not null;
            }
            catch (Exception) { return false; }
        }
    }

    /// <summary>Switches the menu on: the new menu if possible, else the classic one. May show a UAC prompt.</summary>
    public static async Task<MenuResult> TurnOnAsync()
    {
        if (!CanUseModern) return SetClassic(true) ? MenuResult.Classic : MenuResult.Failed;

        if (!PackageIsHere())
        {
            bool? added = await RunHelperAsync().ConfigureAwait(false);
            if (added != true || !PackageIsHere())
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

    /// <summary>Hides the item at once, then removes the package and the keys.</summary>
    public static async Task<MenuResult> TurnOffAsync()
    {
        bool ok = DeleteTitle();
        ok &= SetClassic(false);
        if (PackageRegistered())
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
    public static void RefreshPath()
    {
        if (ExePath is not { } exe) return;
        try
        {
            using var command = Registry.CurrentUser.OpenSubKey(KeyPath(".png") + @"\command");
            if (command?.GetValue(null) is string current && current != CommandFor(exe)) WriteClassic();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException) { }
    }

    // ---- Classic menu (registry) ----

    private static bool SetClassic(bool on)
    {
        try
        {
            if (on) WriteClassic();
            else
            {
                foreach (var ext in Extensions)
                    Registry.CurrentUser.DeleteSubKeyTree(KeyPath(ext), throwOnMissingSubKey: false);
            }
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            return false;
        }
    }

    private static void WriteClassic()
    {
        if (ExePath is not { } exe || !Path.IsPathFullyQualified(exe)) throw new IOException("unknown exe path");
        foreach (var ext in Extensions)
        {
            using var verb = Registry.CurrentUser.CreateSubKey(KeyPath(ext));
            verb.SetValue("MUIVerb", Title);
            verb.SetValue("Icon", $"\"{exe}\",0");
            // One launch per selected file (they gather in one window), and the item stays visible for more than 15 files.
            verb.SetValue("MultiSelectModel", "Player");
            using var command = verb.CreateSubKey("command");
            command.SetValue(null, CommandFor(exe));
        }
    }

    // ---- New menu (package) ----

    private static bool WriteTitle()
    {
        try
        {
            string path = MenuPackage.TitleFilePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, Title + "\r\n", new UTF8Encoding(false));
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { return false; }
    }

    private static bool DeleteTitle()
    {
        try
        {
            File.Delete(MenuPackage.TitleFilePath);
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { return false; }
    }

    /// <summary>Registered for this account with this folder as the place of the DLL.</summary>
    private static bool PackageIsHere()
    {
        try
        {
            return FindPackage() is { } fullName && ExternalLocation(fullName) is { } location
                && string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(location)), Folder, StringComparison.OrdinalIgnoreCase);
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
