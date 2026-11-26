using System.IO;
using System.Windows;
using Microsoft.Win32;
using Wipriss.Reader.Data;

namespace Wipriss.Reader;

/// <summary>
/// Wipriss.Reader.exe "file" ["file" …]: one window per table. Started by a double click on a table, by
/// "Открыть в Wipriss" in Explorer's menu or by the island. Without files it asks for one. The process ends with
/// its last window, so nothing of it stays in memory.
/// </summary>
public partial class App : Application
{
    /// <summary>A selection of hundreds of files in Explorer must not open hundreds of windows.</summary>
    private const int MaxWindows = 12;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) =>
        {
            Core.ErrorLog.Write(args.Exception);
            MessageBox.Show("Что-то пошло не так. Окно таблицы закроется.", "Wipriss", MessageBoxButton.OK, MessageBoxImage.Warning);
            args.Handled = true;
            Shutdown(1);
        };

        var files = e.Args.Where(a => !a.StartsWith("--", StringComparison.Ordinal))
                          .Where(Path.IsPathFullyQualified)
                          .Distinct(StringComparer.OrdinalIgnoreCase)
                          .Take(MaxWindows)
                          .ToList();
        if (files.Count == 0 && PickFile(null) is { } picked) files.Add(picked);
        if (files.Count == 0)
        {
            Shutdown(0);
            return;
        }

        foreach (var file in files) new TableWindow(file).Show();
    }

    public const string FileFilter =
        "Таблицы|*.xlsx;*.xlsm;*.xlsb;*.xls;*.ods;*.csv;*.tsv;*.tab|Excel|*.xlsx;*.xlsm;*.xlsb;*.xls|OpenDocument|*.ods|CSV и TSV|*.csv;*.tsv;*.tab|Все файлы|*.*";

    /// <summary>The usual "Open" dialog; null when the owner closed it.</summary>
    internal static string? PickFile(Window? owner)
    {
        var dialog = new OpenFileDialog { Title = "Открыть таблицу", Filter = FileFilter };
        return dialog.ShowDialog(owner) == true ? dialog.FileName : null;
    }

    internal static bool IsTable(string path) => Workbook.CanOpen(path);
}
