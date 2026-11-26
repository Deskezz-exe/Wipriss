using System.IO;
using System.Windows;
using Wipriss.Core.Modules;

namespace Wipriss.Modules.Converter;

/// <summary>
/// Picture converter: format, size and weight. Idle it does nothing at all; the work runs in
/// Wipriss.Converter.exe, which exits when the batch is done.
/// </summary>
public sealed class ConverterModule : IModule, IFileTarget
{
    private static readonly HashSet<string> Extensions = new(ExplorerMenu.Extensions, StringComparer.OrdinalIgnoreCase);

    private IModuleHost? _host;
    private ConverterSession? _session;
    private ConverterWindow? _window;

    public ModuleInfo Info { get; } = ModuleCatalog.Get("converter");

    public Size PanelSize => new(460, 330);

    internal ConverterSession Session => _session ??= new ConverterSession();

    public string Summary => _session switch
    {
        { IsRunning: true } => "работает…",
        { Items.Count: > 0 } s => Core.Text.RuText.Count(s.Items.Count, "картинка", "картинки", "картинок"),
        _ => "перетащите картинку",
    };

    public void Start(IModuleHost host)
    {
        _host = host;
        ExplorerMenu.RefreshPath();
        // Drawn-on copies left by a crash: nothing of them should stay on disk.
        ConverterSession.DeleteTempFolder();
    }

    public void Stop()
    {
        _window?.Close();
        _session?.Cancel();
        _session = null;
        _host = null;
    }

    public FrameworkElement CreatePanel(IPanelContext panel) => new ConverterPanel(this, panel);

    public bool CanOpen(string path) => Extensions.Contains(Path.GetExtension(path));

    public void OpenFiles(IReadOnlyList<string> paths) => ShowWindow().AddFiles(paths);

    /// <summary>The window with previews and settings; one at a time, brought forward if already open.</summary>
    internal ConverterWindow ShowWindow()
    {
        _window ??= new ConverterWindow(this);
        if (!_window.IsVisible) _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
        return _window;
    }

    internal void WindowClosed()
    {
        _window = null;
        TrimMemory();
    }

    internal void TrimMemory() => _host?.TrimMemory();
}
