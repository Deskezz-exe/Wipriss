using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Wipriss.Core;
using Wipriss.Core.Modules;

namespace Wipriss.Modules.Capture;

public partial class CapturePanel : UserControl
{
    private readonly CaptureModule _module;
    private bool _showing;

    internal CapturePanel(CaptureModule module, IPanelContext panel)
    {
        InitializeComponent();
        _module = module;
        Loaded += (_, _) => ShowState();
    }

    private void ShowState()
    {
        _showing = true;
        OwnKey.IsChecked = _module.PrintScreen.OwnedByWipriss;
        _showing = false;

        if (!_module.PrintScreen.OwnedByWipriss)
            Show("FaintTextBrush", "PrtSc открывает «Ножницы» Windows. Включите, и клавиша сразу будет у Wipriss.");
        else if (_module.HookActive)
            Show("OkBrush", "Работает сразу, перезагрузка не нужна. Wipriss держит клавишу за собой и после обновлений Windows. «Ножницы» остаются на Win+Shift+S.");
        else if (_module.KeyRegistered)
            Show("WarnBrush", "Windows не дала перехватить клавишу сразу. PrtSc заработает после следующего входа в Windows.");
        else
            Show("DangerBrush", "PrtSc занят другой программой, например Lightshot, Яндекс.Диском или OneDrive. Отключите PrtSc там и включите переключатель заново.");
    }

    private void Show(string brush, string text)
    {
        StateDot.Fill = (Brush)FindResource(brush);
        StateText.Text = text;
    }

    private void OwnKey_Changed(object sender, RoutedEventArgs e)
    {
        if (_showing || _module is null) return;
        try
        {
            _module.SetOwnPrintScreen(OwnKey.IsChecked == true);
        }
        catch (Exception ex)
        {
            ErrorLog.Write(ex);
        }

        ShowState();
    }
}
