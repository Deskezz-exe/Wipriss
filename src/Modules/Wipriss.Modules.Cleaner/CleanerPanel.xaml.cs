using System.Windows.Controls;
using System.Windows.Threading;
using Wipriss.Core.Modules;

namespace Wipriss.Modules.Cleaner;

public partial class CleanerPanel : UserControl
{
    internal CleanerPanel(CleanerModule module, IPanelContext panel)
    {
        InitializeComponent();
        Loaded += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            panel.Close();
            module.OpenWindow();
        });
    }
}
