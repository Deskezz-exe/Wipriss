using System.Windows;
using System.Windows.Interop;
using Wipriss.App.Interop;
using Wipriss.App.Shell;
using Wipriss.Core.Modules;
using Wipriss.Core.Settings;

namespace Wipriss.App;

/// <summary>Settings window. Created when opened and dropped when closed, so it costs nothing in between.</summary>
public partial class SettingsWindow : Window
{
    private readonly SettingsStore _store;

    public SettingsWindow(SettingsStore store)
    {
        InitializeComponent();
        _store = store;

        HideInFullscreenToggle.IsChecked = store.Current.HideInFullscreen;
        AutostartToggle.IsChecked = Autostart.IsEnabled;
        ModuleList.ItemsSource = ModuleCatalog.All.Select(m => new ModuleToggle(m, store)).ToList();

        var version = typeof(App).Assembly.GetName().Version;
        VersionText.Text = version is null ? "Wipriss" : $"Wipriss {version.Major}.{version.Minor}.{version.Build}";

        // Subscribe after the initial values are set, so opening the window saves nothing.
        HideInFullscreenToggle.Checked += (_, _) => _store.Update(s => s.HideInFullscreen = true);
        HideInFullscreenToggle.Unchecked += (_, _) => _store.Update(s => s.HideInFullscreen = false);
        AutostartToggle.Checked += (_, _) => Autostart.Set(true);
        AutostartToggle.Unchecked += (_, _) => Autostart.Set(false);
    }

    public event Action? ExitRequested;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        Native.UseDarkTitleBar(new WindowInteropHelper(this).Handle);
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => ExitRequested?.Invoke();
}

/// <summary>One row in the module list.</summary>
public sealed class ModuleToggle(ModuleInfo info, SettingsStore store)
{
    public ModuleInfo Info { get; } = info;

    public bool Enabled
    {
        get => store.Current.IsModuleEnabled(Info.Id);
        set => store.Update(s => s.Modules[Info.Id] = value);
    }
}
