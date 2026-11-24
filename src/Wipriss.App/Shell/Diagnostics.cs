using System.IO;
using System.Windows;
using System.Windows.Threading;
using Wipriss.App.Interop;
using Wipriss.Core;

namespace Wipriss.App.Shell;

internal static class MemoryTrim
{
    /// <summary>Compacts the heap and returns unused pages to Windows. Call when a window closes.</summary>
    public static void Trim()
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        Native.SetProcessWorkingSetSize(new IntPtr(-1), -1, -1);
    }
}

/// <summary>Writes unexpected errors to a local log. The user's profile path is masked before writing.</summary>
internal static class CrashLog
{
    public static void Install(Application app)
    {
        app.DispatcherUnhandledException += (_, e) =>
        {
            Write(e.Exception);
            e.Handled = true; // keep the island alive after a UI error
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Write(e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => { Write(e.Exception); e.SetObserved(); };
    }

    public static void Write(Exception? ex)
    {
        if (ex is null) return;
        try
        {
            Directory.CreateDirectory(AppPaths.LogsDir);
            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var text = ex.ToString();
            if (profile.Length > 0) text = text.Replace(profile, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
            File.AppendAllText(Path.Combine(AppPaths.LogsDir, "crash.log"), $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {text}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (IOException)
        {
            // Logging must never crash the app.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    public static void RunWhenIdle(Dispatcher dispatcher, Action action) =>
        dispatcher.BeginInvoke(action, DispatcherPriority.ApplicationIdle);
}
