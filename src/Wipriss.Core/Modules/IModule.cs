using System.Windows;
using System.Windows.Threading;

namespace Wipriss.Core.Modules;

/// <summary>
/// A pluggable feature of the island. While idle a module may only react to Windows events
/// (never poll), and it builds its UI only when its panel opens.
/// </summary>
public interface IModule
{
    ModuleInfo Info { get; }

    /// <summary>Short status under the tile, e.g. "3 заметки". Read when the island opens.</summary>
    string Summary { get; }

    /// <summary>Panel size in device-independent pixels.</summary>
    Size PanelSize { get; }

    /// <summary>Called when the module is switched on. Subscribe to events here, never poll.</summary>
    void Start(IModuleHost host);

    /// <summary>Called when the module is switched off or the app exits. Release everything.</summary>
    void Stop();

    /// <summary>Builds the panel. Called on open; the panel is dropped on close.</summary>
    FrameworkElement CreatePanel(IPanelContext panel);
}

/// <summary>What the app offers to modules.</summary>
public interface IModuleHost
{
    Dispatcher Dispatcher { get; }

    /// <summary>The system clock or time zone changed.</summary>
    event Action? TimeChanged;

    /// <summary>Opens a module's panel under the island.</summary>
    void OpenPanel(string moduleId);
}

/// <summary>The window a panel lives in.</summary>
public interface IPanelContext
{
    void Close();

    /// <summary>Keeps the panel open while a dialog (e.g. Save As) takes the focus. Dispose to release.</summary>
    IDisposable KeepOpen();
}

/// <summary>How the app creates a module. The factory runs only when the module is switched on,
/// so a switched-off module never even loads its code.</summary>
public sealed record ModuleRegistration(string Id, Func<IModule> Create);
