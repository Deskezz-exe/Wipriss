using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Wipriss.Core.Modules;

namespace Wipriss.Modules.Converter;

/// <summary>The converter's panel under the island: where to start, and the Explorer-menu switch.</summary>
public partial class ConverterPanel : UserControl
{
    private readonly ConverterModule _module;
    private readonly IPanelContext _panel;
    private bool _ready;

    internal ConverterPanel(ConverterModule module, IPanelContext panel)
    {
        InitializeComponent();
        _module = module;
        _panel = panel;
        ShowMenuState(ExplorerMenu.State);
        _ready = true;
        if (module.Session.IsRunning) Status.Text = "Идёт конвертация: откройте окно правки, чтобы посмотреть.";
    }

    private void ShowMenuState(MenuState state)
    {
        bool was = _ready;
        _ready = false;
        MenuToggle.IsChecked = state != MenuState.Off;
        _ready = was;
        MenuHint.Text = state switch
        {
            MenuState.Modern => "Правый щелчок по картинкам → «Изменить через Wipriss».",
            MenuState.Classic when ExplorerMenu.CanUseModern =>
                "Сейчас пункт только в «Показать дополнительные параметры». Выключите и включите снова, чтобы добавить его в основное меню.",
            MenuState.Classic => "Правый щелчок по картинкам → «Изменить через Wipriss».",
            _ when ExplorerMenu.CanUseModern =>
                "Правый щелчок по одной или нескольким картинкам. При включении Windows один раз спросит разрешение администратора.",
            _ => "Правый щелчок по одной или нескольким картинкам.",
        };
    }

    private async void Menu_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        bool on = MenuToggle.IsChecked == true;
        MenuToggle.IsEnabled = false;
        Status.Text = on ? "Добавляю пункт в меню… Проводник может на пару секунд перезапуститься." : "Убираю пункт из меню…";
        MenuResult result;
        try
        {
            // The UAC prompt takes the focus: the panel must not close meanwhile.
            using (_panel.KeepOpen())
                result = on ? await ExplorerMenu.TurnOnAsync() : await ExplorerMenu.TurnOffAsync();
        }
        catch (Exception ex)
        {
            Core.ErrorLog.Write(ex);
            result = MenuResult.Failed;
        }
        MenuToggle.IsEnabled = true;
        ShowMenuState(ExplorerMenu.State);
        Status.Text = result switch
        {
            MenuResult.Modern => "Готово: пункт в меню Проводника.",
            MenuResult.ModernRestarted => "Готово: пункт в меню Проводника. Проводник перезапустился, чтобы его увидеть.",
            MenuResult.Classic when ExplorerMenu.CanUseModern =>
                "Windows не приняла пункт для основного меню. Он добавлен в «Показать дополнительные параметры».",
            MenuResult.Classic => "Готово: пункт добавлен в меню Проводника.",
            MenuResult.Declined => "Без разрешения администратора пункт будет только в «Показать дополнительные параметры».",
            MenuResult.Off => "Пункт убран из меню Проводника.",
            _ => "Windows не дала изменить меню Проводника.",
        };
    }

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        _module.ShowWindow();
        _panel.Close();
    }

    private void Pick_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Картинки для Wipriss",
            Multiselect = true,
            Filter = "Картинки|*.jpg;*.jpeg;*.jfif;*.jpe;*.png;*.webp;*.avif;*.heic;*.heif;*.bmp;*.dib;*.ico;*.tif;*.tiff;*.gif|Все файлы|*.*",
        };
        bool? ok;
        using (_panel.KeepOpen())
            ok = dialog.ShowDialog(Window.GetWindow(this));
        if (ok != true) return;
        _module.OpenFiles(dialog.FileNames);
        _panel.Close();
    }

    private void Panel_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = DroppedFiles(e).Any(_module.CanOpen) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Panel_Drop(object sender, DragEventArgs e)
    {
        var files = DroppedFiles(e).Where(_module.CanOpen).ToList();
        e.Handled = true;
        if (files.Count == 0) return;
        _module.OpenFiles(files);
        _panel.Close();
    }

    private static string[] DroppedFiles(DragEventArgs e) =>
        e.Data.GetDataPresent(DataFormats.FileDrop) && e.Data.GetData(DataFormats.FileDrop) is string[] files ? files : [];
}
