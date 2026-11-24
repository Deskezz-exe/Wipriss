using System.Windows;
using System.Windows.Interop;
using Wipriss.App.Interop;
using Wipriss.App.Shell;
using Wipriss.Core;
using Wipriss.Core.Modules;
using Wipriss.Core.Settings;
using Wipriss.Core.Ui;

namespace Wipriss.App;

/// <summary>Settings window. Created when opened and dropped when closed, so it costs nothing in between.</summary>
public partial class SettingsWindow : Window
{
    private readonly SettingsStore _store;

    internal SettingsWindow(SettingsStore store, ModuleManager modules)
    {
        InitializeComponent();
        _store = store;

        HideInFullscreenToggle.IsChecked = store.Current.HideInFullscreen;
        AutostartToggle.IsChecked = Autostart.IsEnabled;
        ModuleList.ItemsSource = ModuleCatalog.All.Select(m => new ModuleToggle(m, store, modules.IsBuilt(m.Id))).ToList();

        FolderText.Text = AppPaths.Root;
        FolderText.ToolTip = AppPaths.Root;

        var version = typeof(App).Assembly.GetName().Version;
        VersionText.Text = version is null ? "Wipriss" : $"Wipriss {version.Major}.{version.Minor}.{version.Build}";

        // Subscribe after the initial values are set, so opening the window saves nothing.
        HideInFullscreenToggle.Checked += (_, _) => _store.Update(s => s.HideInFullscreen = true);
        HideInFullscreenToggle.Unchecked += (_, _) => _store.Update(s => s.HideInFullscreen = false);
        AutostartToggle.Checked += (_, _) => Autostart.Set(true);
        AutostartToggle.Unchecked += (_, _) => Autostart.Set(false);
    }

    public event Action? ExitRequested;

    /// <summary>The user picked a new place for the Wipriss folder (already checked).</summary>
    public event Action<string>? MoveFolderRequested;

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.IO.Directory.CreateDirectory(AppPaths.Root);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{AppPaths.Root}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex);
        }
    }

    private void ChangeFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Новое место для папки Wipriss" };
        if (dialog.ShowDialog(this) != true) return;

        // A "Wipriss" folder inside the chosen one, unless the user picked an empty folder for it.
        string chosen = dialog.FolderName;
        string target = System.IO.Directory.Exists(chosen) && System.IO.Directory.EnumerateFileSystemEntries(chosen).Any()
            ? System.IO.Path.Combine(chosen, "Wipriss")
            : chosen;

        if (DataFolder.Check(target) is { } problem)
        {
            FolderProblem.Text = problem;
            FolderProblem.Visibility = Visibility.Visible;
            return;
        }

        var answer = MessageBox.Show(this,
            $"Перенести папку Wipriss сюда?\n\n{target}\n\nWipriss перезапустится.",
            "Wipriss", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (answer == MessageBoxResult.OK) MoveFolderRequested?.Invoke(target);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        Native.UseDarkTitleBar(new WindowInteropHelper(this).Handle);
        NativeWindowStyle.FrameColors(this, ThemeColors.Hairline, caption: ThemeColors.Window, captionText: ThemeColors.Text);
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => ExitRequested?.Invoke();
}

/// <summary>One row in the module list.</summary>
public sealed class ModuleToggle(ModuleInfo info, SettingsStore store, bool built)
{
    public ModuleInfo Info { get; } = info;

    public bool Built => built;

    public string Subtitle => built ? Info.Description : Info.StageText;

    public bool Enabled
    {
        get => store.Current.IsModuleEnabled(Info.Id);
        set => store.Update(s => s.Modules[Info.Id] = value);
    }
}
