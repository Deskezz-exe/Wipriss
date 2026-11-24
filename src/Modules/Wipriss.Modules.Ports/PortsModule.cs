using System.Windows;
using Wipriss.Core;
using Wipriss.Core.Modules;

namespace Wipriss.Modules.Ports;

/// <summary>
/// Which program holds which port, with a stop button. Nothing runs in the background:
/// the table refreshes every 2 seconds only while the panel is open.
/// </summary>
public sealed class PortsModule : IModule
{
    private readonly PortCollector _collector = new();

    public ModuleInfo Info { get; } = ModuleCatalog.Get("ports");

    public Size PanelSize => new(500, 640);

    public string Summary
    {
        get
        {
            try
            {
                int count = PortCollector.CountDesktopListeners();
                return count == 0 ? "все свободны" : $"занято: {count}";
            }
            catch (Exception ex)
            {
                ErrorLog.Write(ex);
                return "";
            }
        }
    }

    public void Start(IModuleHost host) { }

    public void Stop() { }

    public FrameworkElement CreatePanel(IPanelContext panel) => new PortsPanel(_collector, panel);
}
