using System.Windows;
using Wipriss.App.Interop;
using Wipriss.App.Shell;
using Wipriss.Core;
using Wipriss.Core.Settings;

namespace Wipriss.App;

public partial class App : Application
{
    private const int MenuSettings = 1, MenuToggleIsland = 2, MenuExit = 3;

    private SingleInstance? _instance;
    private SettingsStore? _store;
    private IslandWindow? _island;
    private TrayIcon? _tray;
    private SettingsWindow? _settings;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _instance = SingleInstance.TryAcquire();
        if (_instance is null)
        {
            SingleInstance.SignalRunningInstance();
            Shutdown();
            return;
        }

        CrashLog.Install(this);
        Native.AllowDarkMenus();

        _store = new SettingsStore(AppPaths.SettingsFile);

        _island = new IslandWindow(_store);
        _island.SettingsRequested += ShowSettings;
        _island.Show();

        _tray = new TrayIcon("Wipriss");
        _tray.LeftClick += ShowSettings;
        _tray.MenuRequested += ShowTrayMenu;

        _instance.Activated += () => Dispatcher.BeginInvoke(ShowSettings);

        // Start-up allocations are done: give unused memory back once the UI is idle.
        CrashLog.RunWhenIdle(Dispatcher, MemoryTrim.Trim);
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
        if (_store is null) return;

        if (_settings is null)
        {
            _settings = new SettingsWindow(_store);
            _settings.ExitRequested += ExitApp;
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

    private void ExitApp()
    {
        _settings?.Close();
        _island?.Close();
        _tray?.Dispose();
        _instance?.Dispose();
        Shutdown();
    }
}
