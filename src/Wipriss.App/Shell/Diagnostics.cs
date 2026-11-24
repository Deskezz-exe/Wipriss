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

    public static void Write(Exception? ex) => ErrorLog.Write(ex);

    public static void RunWhenIdle(Dispatcher dispatcher, Action action) =>
        dispatcher.BeginInvoke(action, DispatcherPriority.ApplicationIdle);
}
