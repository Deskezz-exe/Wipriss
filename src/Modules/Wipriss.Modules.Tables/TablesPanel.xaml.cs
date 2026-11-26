using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Wipriss.Core.Modules;
using Wipriss.ExplorerMenu;

namespace Wipriss.Modules.Tables;

/// <summary>The tables panel under the island: open a table, and the Explorer-menu switch.</summary>
public partial class TablesPanel : UserControl
{
    private const string Filter =
        "Таблицы|*.xlsx;*.xlsm;*.xlsb;*.xls;*.ods;*.csv;*.tsv;*.tab|Все файлы|*.*";

    private readonly IPanelContext _panel;
    private bool _ready;

    internal TablesPanel(IPanelContext panel)
    {
        InitializeComponent();
        _panel = panel;
        ShowMenuState(TablesModule.Menu.State);
        _ready = true;
    }

    private void Pick_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Открыть таблицу", Multiselect = true, Filter = Filter };
        bool? ok;
        using (_panel.KeepOpen())
            ok = dialog.ShowDialog(Window.GetWindow(this));
        if (ok != true) return;
        if (TablesModule.Open(dialog.FileNames)) _panel.Close();
        else Status.Text = "Не получилось открыть окно таблицы: файл Wipriss.Reader.exe не найден рядом с Wipriss.";
    }

    // ---- Explorer menu ----

    private void ShowMenuState(MenuState state)
    {
        bool was = _ready;
        _ready = false;
        MenuToggle.IsChecked = state != MenuState.Off;
        _ready = was;
        MenuHint.Text = state switch
        {
            MenuState.Modern => "Правый щелчок по таблице → «Открыть в Wipriss».",
            MenuState.Classic when MenuSwitch.CanUseModern =>
                "Сейчас пункт только в «Показать дополнительные параметры». Выключите и включите снова, чтобы добавить его в основное меню.",
            MenuState.Classic => "Правый щелчок по таблице → «Открыть в Wipriss».",
            _ when MenuSwitch.CanUseModern =>
                "Пункт с иконкой Wipriss в меню по правому щелчку. Если Windows спросит разрешение администратора, это один раз.",
            _ => "Пункт с иконкой Wipriss в меню по правому щелчку.",
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
                result = on ? await TablesModule.Menu.TurnOnAsync() : await TablesModule.Menu.TurnOffAsync();
        }
        catch (Exception ex)
        {
            Core.ErrorLog.Write(ex);
            result = MenuResult.Failed;
        }
        MenuToggle.IsEnabled = true;
        ShowMenuState(TablesModule.Menu.State);
        Status.Text = result switch
        {
            MenuResult.Modern => "Готово: «Открыть в Wipriss» в меню Проводника.",
            MenuResult.ModernRestarted => "Готово: пункт в меню Проводника. Проводник перезапустился, чтобы его увидеть.",
            MenuResult.Classic when MenuSwitch.CanUseModern =>
                "Windows не приняла пункт для основного меню. Он добавлен в «Показать дополнительные параметры».",
            MenuResult.Classic => "Готово: пункт добавлен в меню Проводника.",
            MenuResult.Declined => "Без разрешения администратора пункт будет только в «Показать дополнительные параметры».",
            MenuResult.Off => "Пункт убран из меню Проводника.",
            _ => "Windows не дала изменить меню Проводника.",
        };
    }

    // ---- Drop ----

    private void Panel_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = DroppedTables(e).Length > 0 ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Panel_Drop(object sender, DragEventArgs e)
    {
        var files = DroppedTables(e);
        e.Handled = true;
        if (files.Length > 0 && TablesModule.Open(files)) _panel.Close();
    }

    private static string[] DroppedTables(DragEventArgs e) =>
        e.Data.GetDataPresent(DataFormats.FileDrop) && e.Data.GetData(DataFormats.FileDrop) is string[] files
            ? Array.FindAll(files, f => MenuEntry.Tables.Extensions.Contains(System.IO.Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
            : [];
}
