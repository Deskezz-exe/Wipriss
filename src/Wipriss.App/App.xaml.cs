using System.Windows;
using Wipriss.App.Interop;
using Wipriss.App.Shell;
using Wipriss.Core;
using Wipriss.Core.Modules;
using Wipriss.Core.Settings;

namespace Wipriss.App;

public partial class App : Application
{
    private const int MenuSettings = 1, MenuToggleIsland = 2, MenuExit = 3;

    // Built modules. A factory runs only when its module is switched on.
    private static readonly ModuleRegistration[] Registrations =
    [
        new("notes", () => new Wipriss.Modules.Notes.NotesModule()),
        new("clipboard", () => new Wipriss.Modules.ClipboardHistory.ClipboardModule()),
        new("reminders", () => new Wipriss.Modules.Reminders.RemindersModule()),
        new("network", () => new Wipriss.Modules.Network.NetworkModule()),
        new("ports", () => new Wipriss.Modules.Ports.PortsModule()),
        new("capture", () => new Wipriss.Modules.Capture.CaptureModule()),
        new("converter", () => new Wipriss.Modules.Converter.ConverterModule()),
        new("reader", () => new Wipriss.Modules.Tables.TablesModule()),
        new("dictation", () => new Wipriss.Modules.Voice.VoiceModule()),
        new("cleaner", () => new Wipriss.Modules.Cleaner.CleanerModule()),
    ];

    private SingleInstance? _instance;
    private SettingsStore? _store;
    private ModuleManager? _modules;
    private IslandWindow? _island;
    private TrayIcon? _tray;
    private SettingsWindow? _settings;
    private FlyoutWindow? _flyout;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _instance = SingleInstance.TryAcquire();
        if (_instance is null)
        {
            // Started from Explorer's menu, this launch may bring a window forward; pass that right on to
            // the running Wipriss, which shows the window.
            Native.AllowSetForegroundWindow(Native.AsfwAny);
            SingleInstance.SendToRunningInstance(e.Args);
            Shutdown();
            return;
        }

        CrashLog.Install(this);
        Native.AllowDarkMenus();
        DataFolder.Prepare(); // one Wipriss folder; moves data from older versions before anything reads it

        _store = new SettingsStore(AppPaths.SettingsFile);
        _modules = new ModuleManager(_store, Registrations, Dispatcher);
        _modules.OpenRequested += OpenPanel;
        _modules.Stopped += id => { if (_flyout?.ModuleId == id) _flyout.Close(); };

        _island = new IslandWindow(_store, _modules);
        _island.SettingsRequested += ShowSettings;
        _island.ModuleRequested += OpenPanel;
        _island.Show();

        _tray = new TrayIcon("Wipriss");
        _tray.LeftClick += ShowSettings;
        _tray.MenuRequested += ShowTrayMenu;

        _instance.CommandReceived += args => Dispatcher.BeginInvoke(() => HandleCommand(args, fromOtherLaunch: true));
        _instance.StartListening();
        HandleCommand(e.Args, fromOtherLaunch: false);

        // Start-up allocations are done: give unused memory back once the UI is idle.
        CrashLog.RunWhenIdle(Dispatcher, MemoryTrim.Trim);
    }

    /// <summary>
    /// Command line: <c>--open &lt;module&gt;</c> opens a panel (handy for a shortcut with a hotkey),
    /// <c>--settings</c> opens settings. Launching again without arguments also opens settings.
    /// </summary>
    private void HandleCommand(string[] args, bool fromOtherLaunch)
    {
        // From the Explorer menu: --convert "<file>" ... (the new menu passes the whole selection at once,
        // the classic one starts one launch per file), or --convert-list "<list>" for a selection too long
        // for a command line.
        int convert = Array.IndexOf(args, "--convert");
        int list = Array.IndexOf(args, "--convert-list");
        if (convert >= 0 || list >= 0)
        {
            var files = convert >= 0
                ? args.Skip(convert + 1).Where(System.IO.Path.IsPathFullyQualified).ToList()
                : list + 1 < args.Length ? ExplorerList.Read(args[list + 1]) : [];
            if (files.Count > 0 && _modules?.Get("converter") is IFileTarget converter)
            {
                try { converter.OpenFiles(files); }
                catch (Exception ex) { CrashLog.Write(ex); }
            }
            return;
        }

        int open = Array.IndexOf(args, "--open");
        if (open >= 0 && open + 1 < args.Length)
        {
            OpenPanel(args[open + 1]);
            return;
        }

        if (args.Contains("--settings") || (fromOtherLaunch && !args.Contains("--autostart")))
            ShowSettings();
    }

    private void OpenPanel(string moduleId)
    {
        // Only modules that exist and are switched on: anything else is ignored.
        var module = _modules?.Get(moduleId);
        if (module is null) return;

        // Already open: keep it (new files show up in its list) instead of rebuilding it for every file.
        if (_flyout is { } open && open.ModuleId == moduleId && open.IsVisible)
        {
            open.Activate();
            return;
        }

        _flyout?.Close();
        var flyout = new FlyoutWindow(module);
        flyout.Closed += (_, _) =>
        {
            if (_flyout == flyout) _flyout = null;
            CrashLog.RunWhenIdle(Dispatcher, MemoryTrim.Trim);
        };
        _flyout = flyout;
        flyout.Show();
        flyout.Activate();
    }

    private void ShowTrayMenu()
    {
        if (_tray is null || _island is null) return;

        int chosen = _tray.ShowMenu(
        [
            (MenuSettings, "Настройки"),
            (MenuToggleIsland, _island.IsShownByUser ? "Скрыть островок" : "Показать островок"),
            (0, null),
            (MenuExit, "Выход"),
        ]);

        switch (chosen)
        {
            case MenuSettings: ShowSettings(); break;
            case MenuToggleIsland: _island.IsShownByUser = !_island.IsShownByUser; break;
            case MenuExit: ExitApp(); break;
        }
    }

    private void ShowSettings()
    {
        if (_store is null || _modules is null) return;

        if (_settings is null)
        {
            _settings = new SettingsWindow(_store, _modules);
            _settings.ExitRequested += ExitApp;
            _settings.MoveFolderRequested += MoveDataFolder;
            _settings.Closed += (_, _) =>
            {
                _settings = null;
                CrashLog.RunWhenIdle(Dispatcher, MemoryTrim.Trim);
            };
            _settings.Show();
        }

        if (_settings.WindowState == WindowState.Minimized) _settings.WindowState = WindowState.Normal;
        _settings.Activate();
    }

    /// <summary>
    /// Moves the Wipriss folder. Modules stop first so no file is open, and the app restarts
    /// afterwards because every path is read at startup.
    /// </summary>
    private void MoveDataFolder(string newRoot)
    {
        _modules?.StopAll();
        string? problem = DataFolder.MoveTo(newRoot);
        if (problem is not null)
            MessageBox.Show(problem, "Wipriss", MessageBoxButton.OK, MessageBoxImage.Warning);

        _flyout?.Close();
        _settings?.Close();
        _island?.Close();
        _tray?.Dispose();
        _instance?.Dispose(); // release single-instance first, so the new copy can start
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!, "--settings") { UseShellExecute = false });
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex);
        }

        Shutdown();
    }

    private void ExitApp()
    {
        _flyout?.Close();
        _settings?.Close();
        _modules?.StopAll();
        _island?.Close();
        _tray?.Dispose();
        _instance?.Dispose();
        Shutdown();
    }
}
