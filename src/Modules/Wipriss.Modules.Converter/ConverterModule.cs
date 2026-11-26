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
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".jfif", ".jpe", ".png", ".webp", ".avif", ".heic", ".heif",
        ".bmp", ".dib", ".ico", ".tif", ".tiff", ".gif",
    };

    private IModuleHost? _host;
    private ConverterSession? _session;

    public ModuleInfo Info { get; } = ModuleCatalog.Get("converter");

    public Size PanelSize => new(500, 660);

    internal ConverterSession Session => _session ??= new ConverterSession();

    public string Summary => _session switch
    {
        { IsRunning: true } => "работает…",
        { Items.Count: > 0 } s => Core.Text.RuText.Count(s.Items.Count, "картинка", "картинки", "картинок"),
        _ => "перетащите картинку",
    };

    public void Start(IModuleHost host) => _host = host;

    public void Stop()
    {
        _session?.Cancel();
        _session = null;
        _host = null;
    }

    public FrameworkElement CreatePanel(IPanelContext panel) => new ConverterPanel(this, panel);

    public bool CanOpen(string path) => Extensions.Contains(Path.GetExtension(path));

    public void OpenFiles(IReadOnlyList<string> paths)
    {
        Session.Add(paths.Where(CanOpen));
        _host?.OpenPanel(Info.Id);
    }

    internal void TrimMemory() => _host?.TrimMemory();
}
