using System.Windows;

namespace Wipriss.Core.Modules;

/// <summary>
/// A pluggable feature of the island. A module must do nothing while idle except react to
/// Windows events, and must build its UI only when the panel opens.
/// </summary>
public interface IModule
{
    ModuleInfo Info { get; }

    /// <summary>Called when the module is switched on. Subscribe to events here, never poll.</summary>
    void Start();

    /// <summary>Called when the module is switched off or the app exits. Release everything.</summary>
    void Stop();

    /// <summary>Builds the panel. Called on open; the panel is dropped on close.</summary>
    FrameworkElement CreatePanel();
}
