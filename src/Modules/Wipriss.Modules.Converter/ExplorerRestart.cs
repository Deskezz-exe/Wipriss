using System.IO;
using System.Diagnostics;

namespace Wipriss.Modules.Converter;

/// <summary>
/// Explorer reads the list of packaged menu commands only when it starts, so a menu package registered while it
/// runs stays invisible until Explorer restarts. This closes this session's Explorer; Windows itself starts it again
/// at once (as after a crash), with the desktop and the taskbar. If it hasn't after a few seconds, Wipriss starts it.
/// Runs only right after the owner switched the menu on. (The Restart Manager way was tried and left the screen
/// black for a long time, so it is not used.)
/// </summary>
internal static class ExplorerRestart
{
    /// <summary>True if Explorer started before <paramref name="registered"/> (it doesn't know the package yet).</summary>
    public static bool IsNeeded(DateTime registered)
    {
        var explorers = SessionExplorers();
        try { return explorers.Any(p => p.StartTime < registered); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
        finally { foreach (var p in explorers) p.Dispose(); }
    }

    public static void Run()
    {
        var explorers = SessionExplorers();
        try
        {
            foreach (var p in explorers)
            {
                try { p.Kill(); p.WaitForExit(3000); }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            }
        }
        finally
        {
            foreach (var p in explorers) p.Dispose();
            EnsureRunning();
        }
    }

    /// <summary>Whatever happened, the owner must not be left without a desktop and taskbar.</summary>
    private static void EnsureRunning()
    {
        for (int i = 0; i < 16; i++)
        {
            Thread.Sleep(250);
            var now = SessionExplorers();
            bool any = now.Count > 0;
            foreach (var p in now) p.Dispose();
            if (any) return;
        }
        try { Process.Start(new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe")) { UseShellExecute = true }); }
        catch (System.ComponentModel.Win32Exception) { }
    }

    private static List<Process> SessionExplorers()
    {
        int session = Process.GetCurrentProcess().SessionId;
        var result = new List<Process>();
        foreach (var p in Process.GetProcessesByName("explorer"))
        {
            if (p.SessionId == session) result.Add(p);
            else p.Dispose();
        }
        return result;
    }
}
