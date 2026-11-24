using System.Windows;
using Wipriss.Core;
using Wipriss.Core.Modules;
using Wipriss.Core.Text;

namespace Wipriss.Modules.Processes;

/// <summary>
/// The user's running programs with end task. Nothing runs in the background: the list refreshes
/// every 2 seconds only while the panel is open.
/// </summary>
public sealed class ProcessesModule : IModule
{
    private readonly AppCollector _collector = new();
    private DateTime _summaryAt;
    private string _summary = "";

    public ModuleInfo Info { get; } = ModuleCatalog.Get("processes");

    public Size PanelSize => new(500, 640);

    /// <summary>Program count for the island tile. Counted at most every few seconds; takes a few ms.</summary>
    public string Summary
    {
        get
        {
            if (DateTime.UtcNow - _summaryAt < TimeSpan.FromSeconds(3)) return _summary;
            try
            {
                int count = _collector.Collect(withNames: false).Count;
                _summary = RuText.Count(count, "программа", "программы", "программ");
            }
            catch (Exception ex)
            {
                ErrorLog.Write(ex);
                _summary = "";
            }

            _summaryAt = DateTime.UtcNow;
            return _summary;
        }
    }

    public void Start(IModuleHost host) { }

    public void Stop() => _collector.Reset();

    public FrameworkElement CreatePanel(IPanelContext panel) => new ProcessesPanel(_collector, panel);
}
