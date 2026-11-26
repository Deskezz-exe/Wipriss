using System.Diagnostics;
using Wipriss.ExplorerMenu;
using Wipriss.ProcessTools;

namespace Wipriss.Helper;

/// <summary>
/// Wipriss admin helper. Only Wipriss starts it, with one of two fixed commands:
/// <c>stop PID:STARTTIME [...]</c> (exit code = how many processes stopped), or
/// <c>menu-add</c>: registers the Explorer-menu package that lies next to this exe (exit code 0 = done).
/// Any other input is rejected.
/// </summary>
internal static class Program
{
    public const string MenuAdd = "menu-add";
    private const int MenuFailed = 1, MenuMissing = 2;

    private static int Main(string[] args)
    {
        try
        {
            if (args.Length == 1 && args[0] == MenuAdd) return AddMenu();
            return ElevatedStop.RunHelper(args);
        }
        catch
        {
            // Nothing to log: no user data passes through here. The caller sees "not done".
            return args.Length == 1 && args[0] == MenuAdd ? MenuFailed : ElevatedStop.InvalidArguments;
        }
    }

    /// <summary>
    /// Every path comes from this exe's own folder, never from the caller: the helper registers only Wipriss's
    /// own package, with Wipriss's folder as the place of its DLL.
    /// </summary>
    private static int AddMenu()
    {
        if (!MenuPackage.IsSupportedOs) return MenuFailed;
        string folder = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
        string package = Path.Combine(folder, MenuPackage.PackageFileName);
        if (!File.Exists(package) || !File.Exists(Path.Combine(folder, MenuPackage.DllFileName))) return MenuMissing;

        var start = new ProcessStartInfo(MenuPackage.PowerShellPath, MenuPackage.PowerShellArguments(MenuPackage.AddScript(package, folder)))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var process = Process.Start(start);
        if (process is null) return MenuFailed;
        if (!process.WaitForExit(TimeSpan.FromMinutes(3)))
        {
            try { process.Kill(); } catch (InvalidOperationException) { }
            return MenuFailed;
        }
        return process.ExitCode == 0 ? 0 : MenuFailed;
    }
}
