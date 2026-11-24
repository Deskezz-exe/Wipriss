using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Wipriss.Core;
using Wipriss.Core.Modules;

namespace Wipriss.Modules.Capture;

public partial class CapturePanel : UserControl
{
    private readonly CaptureModule _module;
    private readonly IPanelContext _panel;

    internal CapturePanel(CaptureModule module, IPanelContext panel)
    {
        InitializeComponent();
        _module = module;
        _panel = panel;
        Loaded += (_, _) => ShowState();
    }

    private void ShowState()
    {
        bool snipping = PrintScreenSetting.SnippingToolTakesKey;
        if (!_module.KeyRegistered)
        {
            Show("DangerBrush", "PrtSc занят другой программой",
                "Например, Lightshot, Яндекс.Диск или OneDrive. Отключите там PrtSc и включите этот модуль заново. Пока можно снимать кнопкой ниже.");
            FixButton.Visibility = Visibility.Collapsed;
        }
        else if (snipping)
        {
            Show("WarnBrush", "Сейчас PrtSc открывает «Ножницы» Windows",
                "В Windows 11 включено «Открывать Ножницы клавишей PrtSc». Кнопка выключит только эту настройку, вернуть её можно здесь же.");
            FixButton.Visibility = Visibility.Visible;
        }
        else
        {
            Show("OkBrush", "PrtSc открывает Wipriss", "Работает поверх любых окон, в том числе запущенных от имени администратора.");
            FixButton.Visibility = Visibility.Collapsed;
        }

        RestoreButton.Visibility = !snipping ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Show(string brush, string title, string text)
    {
        StateDot.Fill = (Brush)FindResource(brush);
        StateTitle.Text = title;
        StateText.Text = text;
    }

    private void Fix_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            PrintScreenSetting.GiveKeyToWipriss(true);
            _module.RetryKey();
        }
        catch (Exception ex)
        {
            ErrorLog.Write(ex);
        }

        ShowState();
    }

    private void Restore_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            PrintScreenSetting.GiveKeyToWipriss(false);
        }
        catch (Exception ex)
        {
            ErrorLog.Write(ex);
        }

        ShowState();
    }

    /// <summary>Closes the panel first so it isn't in the picture, then freezes the screen.</summary>
    private void Shoot_Click(object sender, RoutedEventArgs e)
    {
        _panel.Close();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            _module.TakeScreenshot();
        };
        timer.Start();
    }

    private void Folder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.ScreenshotsDir);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AppPaths.ScreenshotsDir}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ErrorLog.Write(ex);
        }
    }
}
