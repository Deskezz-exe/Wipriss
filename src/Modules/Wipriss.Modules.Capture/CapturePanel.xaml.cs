using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Wipriss.Core;
using Wipriss.Core.Modules;

namespace Wipriss.Modules.Capture;

public partial class CapturePanel : UserControl
{
    private readonly CaptureModule _module;
    private readonly IPanelContext _panel;
    private bool _showing;

    internal CapturePanel(CaptureModule module, IPanelContext panel)
    {
        InitializeComponent();
        _module = module;
        _panel = panel;
        Loaded += (_, _) => ShowState();
    }

    private void ShowState()
    {
        _showing = true;
        OwnKey.IsChecked = _module.PrintScreen.OwnedByWipriss;
        _showing = false;

        bool waiting = false;
        if (!_module.PrintScreen.OwnedByWipriss)
        {
            Show("FaintTextBrush", "PrtSc открывает «Ножницы» Windows. Включите, чтобы клавиша была у Wipriss.");
            waiting = _module.PrintScreen.WaitingForExplorer;
        }
        else if (!_module.KeyRegistered)
        {
            Show("DangerBrush", "PrtSc занят другой программой, например Lightshot, Яндекс.Диском или OneDrive. Отключите PrtSc там и включите переключатель заново.");
        }
        else if (_module.PrintScreen.WaitingForExplorer)
        {
            Show("WarnBrush", "Почти готово: Windows применит это после перезапуска Проводника. До тех пор PrtSc ещё открывает «Ножницы».");
            waiting = true;
        }
        else
        {
            Show("OkBrush", "Работает поверх любых окон, в том числе запущенных от имени администратора. Wipriss следит, чтобы после обновлений Windows клавиша не вернулась к «Ножницам». «Ножницы» остаются на Win+Shift+S.");
        }

        RestartBox.Visibility = waiting ? Visibility.Visible : Visibility.Collapsed;
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

    private async void Restart_Click(object sender, RoutedEventArgs e)
    {
        RestartButton.IsEnabled = false;
        // Explorer going away moves focus around; the panel must stay to show the result.
        using (_panel.KeepOpen())
        {
            try
            {
                await PrintScreenKey.RestartExplorerAsync();
                await Task.Delay(1500); // let the new Explorer finish starting before checking again
            }
            catch (Exception ex)
            {
                ErrorLog.Write(ex);
            }
        }

        RestartButton.IsEnabled = true;
        _module.RetryKey();
        ShowState();
    }
}
