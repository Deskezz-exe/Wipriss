using System.IO;
using System.Text;

namespace Wipriss.ExplorerMenu;

// Names and scripts of the Windows 11 Explorer-menu package. Compiled into the converter module (which shows the
// switch and checks the state) and into Wipriss.Helper.exe (which registers the package with admin rights).
// The PowerShell parts follow Filee's ExplorerMenuRegistration (https://github.com/KnifeLemon/Filee, MIT,
// Myeonggu Kim and Filee contributors); changes: Wipriss names, the helper derives every path itself.

internal static class MenuPackage
{
    /// <summary>Identity/@Name in AppxManifest.xml.</summary>
    public const string PackageName = "Wipriss.ExplorerMenu";

    /// <summary>Identity/@Publisher: the OID marks an unsigned package, so it can never pose as a signed one.</summary>
    public const string Publisher = "CN=Wipriss, OID.2.25.311729368913984317654407730594956997722=1";

    public const string DllFileName = "WiprissExplorerMenu.dll";
    public const string PackageFileName = "WiprissExplorerMenu.msix";

    /// <summary>Same as LIST_PREFIX in WiprissExplorerMenu.c: very long selections come as a list file in %TEMP%.</summary>
    public const string ListFilePrefix = "Wipriss-convert-";

    /// <summary>Windows 11: the first version with the new menu and unsigned packages.</summary>
    public const int MinimumBuild = 22000;

    public static bool IsSupportedOs =>
        OperatingSystem.IsWindowsVersionAtLeast(10, 0, MinimumBuild) && Environment.Is64BitOperatingSystem
        && System.Runtime.InteropServices.RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.X64;

    /// <summary>
    /// While this file exists the DLL shows the entry (TITLE_FILE in WiprissExplorerMenu.c); first line = the title.
    /// So the switch hides and shows the entry at once, without admin rights. It lies next to the DLL: the process that
    /// hosts the DLL runs in the package context and doesn't see files Wipriss writes into AppData.
    /// </summary>
    public const string TitleFileName = "explorer-menu.txt";

    /// <summary>The same for "Открыть в Wipriss" on tables (TABLES_TITLE_FILE in WiprissExplorerMenu.c).</summary>
    public const string TablesTitleFileName = "explorer-menu-tables.txt";

    /// <summary>All entries' title files: the package stays registered while any of them exists.</summary>
    public static readonly string[] TitleFileNames = [TitleFileName, TablesTitleFileName];

    /// <summary>The table viewer next to Wipriss.exe (READER_EXE in WiprissExplorerMenu.c).</summary>
    public const string ReaderExeName = "Wipriss.Reader.exe";

    /// <summary>Identity/@Version in AppxManifest.xml. Version 1.1 added the tables entry, 1.2 registers it for "*" (the DLL checks the type).</summary>
    public const string Version = "1.2.0.0";

    public static string TitleFilePath => TitlePath(TitleFileName);

    public static string TitlePath(string fileName) => Path.Combine(AppContext.BaseDirectory, fileName);

    /// <summary>The version inside a package full name: Name_Version_Architecture_ResourceId_PublisherId.</summary>
    public static Version? VersionOf(string fullName)
    {
        var parts = fullName.Split('_');
        return parts.Length >= 2 && System.Version.TryParse(parts[1], out var version) ? version : null;
    }

    public static string PowerShellPath =>
        Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");

    public static string AddScript(string packagePath, string folder) => Wrap(
        $"Get-AppxPackage -Name {Quote(PackageName)} | Remove-AppxPackage; " +
        $"Add-AppxPackage -Path {Quote(packagePath)} -ExternalLocation {Quote(folder)} -AllowUnsigned -ForceUpdateFromAnyVersion");

    public static string RemoveScript() => Wrap($"Get-AppxPackage -Name {Quote(PackageName)} | Remove-AppxPackage");

    /// <summary>
    /// Arguments for Windows PowerShell. The script holds only single-quoted strings, so double quotes around it are
    /// safe; a script with a double quote is refused.
    /// </summary>
    public static string PowerShellArguments(string script)
    {
        if (script.Contains('"')) throw new ArgumentException("double quote in script", nameof(script));
        return $"-NoLogo -NoProfile -NonInteractive -Command \"{script}\"";
    }

    /// <summary>
    /// A PowerShell single-quoted literal. PowerShell also treats ‘ ’ ‚ ‛ as single quotes, so those are doubled too.
    /// </summary>
    public static string Quote(string value)
    {
        var text = new StringBuilder(value.Length + 2).Append('\'');
        foreach (char c in value)
        {
            text.Append(c);
            if (c is '\'' or '‘' or '’' or '‚' or '‛') text.Append(c);
        }
        return text.Append('\'').ToString();
    }

    private static string Wrap(string command) =>
        $"$ErrorActionPreference = 'Stop'; try {{ {command}; exit 0 }} catch {{ exit 1 }}";
}
