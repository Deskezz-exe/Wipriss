using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using Wipriss.Core.Modules;
using Wipriss.ExplorerMenu;

namespace Wipriss.Modules.Tables;

/// <summary>
/// Tables: XLSX, XLS, XLSB, ODS, CSV and TSV in a dark, comfortable window. The window is Wipriss.Reader.exe, a
/// separate process that ends when it closes, so the island stays light. Idle this module does nothing at all.
/// </summary>
public sealed class TablesModule : IModule, IFileTarget
{
    private static readonly HashSet<string> Extensions = new(MenuEntry.Tables.Extensions, StringComparer.OrdinalIgnoreCase);

    /// <summary>"Открыть в Wipriss" in Explorer's menu.</summary>
    internal static readonly MenuSwitch Menu = new(MenuEntry.Tables);

    private const int MaxWindows = 12;

    public ModuleInfo Info { get; } = ModuleCatalog.Get("reader");

    public Size PanelSize => new(460, 310);

    public string Summary => "XLSX, CSV и другие";

    public void Start(IModuleHost host)
    {
        Menu.RefreshPath();
    }

    public void Stop() { }

    public FrameworkElement CreatePanel(IPanelContext panel) => new TablesPanel(panel);

    public bool CanOpen(string path) => Extensions.Contains(Path.GetExtension(path));

    public void OpenFiles(IReadOnlyList<string> paths) => Open(paths);

    /// <summary>Starts the viewer with these files (one window each); false if it could not start.</summary>
    internal static bool Open(IReadOnlyList<string> paths)
    {
        string exe = Path.Combine(AppContext.BaseDirectory, MenuPackage.ReaderExeName);
        if (!File.Exists(exe)) return false;
        var start = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = AppContext.BaseDirectory };
        foreach (var path in paths.Where(Path.IsPathFullyQualified).Take(MaxWindows)) start.ArgumentList.Add(path);
        try
        {
            Process.Start(start)?.Dispose();
            return true;
        }
        catch (Win32Exception)
        {
            return false;
        }
    }
}
