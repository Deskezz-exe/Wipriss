using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Wipriss.Core;
using Wipriss.Core.Modules;
using Wipriss.Core.Text;
using Wipriss.Core.Ui;
using Wipriss.ProcessTools;

namespace Wipriss.Modules.Processes;

public partial class ProcessesPanel : UserControl
{
    private readonly AppCollector _collector;
    private readonly IPanelContext _panel;
    private readonly Dictionary<string, AppRow> _rows = new(StringComparer.OrdinalIgnoreCase);
    private readonly ObservableCollection<AppRow> _visible = [];
    private readonly DispatcherTimer _timer;
    private string _sort = "memory";
    private bool _loaded;
    private bool _busy;
    private bool _hasData;

    internal ProcessesPanel(AppCollector collector, IPanelContext panel)
    {
        InitializeComponent();
        _collector = collector;
        _panel = panel;
        List.ItemsSource = _visible;

        // Refreshes only while the panel is open.
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2) };
        _timer.Tick += (_, _) => Refresh();

        Loaded += (_, _) =>
        {
            _loaded = true;
            Refresh();
            _timer.Start();
            SearchBox.Focus();
        };
        Unloaded += (_, _) =>
        {
            _loaded = false;
            _timer.Stop();
            _rows.Clear();
            _visible.Clear();
        };
    }

    private async void Refresh()
    {
        if (_busy || !_loaded) return;
        _busy = true;
        try
        {
            var groups = await Task.Run(() => _collector.Collect(withNames: true));
            if (_loaded) Apply(groups);
        }
        catch (Exception ex)
        {
            ErrorLog.Write(ex);
        }
        finally
        {
            _busy = false;
        }
    }

    private void Apply(List<AppGroup> groups)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var fresh = new List<AppRow>();
        foreach (var group in groups)
        {
            seen.Add(group.Path);
            if (!_rows.TryGetValue(group.Path, out var row))
            {
                _rows[group.Path] = row = new AppRow(group.Path);
                fresh.Add(row);
            }

            row.Update(group);
        }

        foreach (string gone in _rows.Keys.Where(k => !seen.Contains(k)).ToList())
            _rows.Remove(gone);

        _hasData = true;
        LoadIcons(fresh);
        TotalText.Text = $"{RuText.Count(groups.Count, "программа", "программы", "программ")}, "
            + $"{RuText.Memory(groups.Sum(g => g.PrivateBytes))}. Части Windows и службы не показаны.";
        ApplyOrder();
    }

    /// <summary>Filters and sorts. While the pointer is over the list, rows stay where they are, so nothing jumps under the cursor.</summary>
    private void ApplyOrder()
    {
        string query = SearchBox.Text.Trim();
        var rows = _rows.Values.Where(r => query.Length == 0
            || r.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
            || System.IO.Path.GetFileName(r.Path).Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();

        List<AppRow> target;
        if (List.IsMouseOver || _rows.Values.Any(r => r.IsAsking))
        {
            var wanted = rows.ToHashSet();
            target = _visible.Where(wanted.Contains).ToList();
            var placed = target.ToHashSet();
            target.AddRange(rows.Where(r => !placed.Contains(r)));
        }
        else
        {
            target = (_sort switch
            {
                "cpu" => rows.OrderByDescending(r => r.Cpu).ThenByDescending(r => r.PrivateBytes),
                "name" => rows.OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase),
                _ => rows.OrderByDescending(r => r.PrivateBytes),
            }).ToList();
        }

        Sync(target);
        EmptyText.Text = query.Length > 0 ? "Ничего не нашлось." : "Нет запущенных программ.";
        EmptyText.Visibility = _hasData && _visible.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Moves rows instead of recreating them, so their visuals and state are kept.</summary>
    private void Sync(List<AppRow> target)
    {
        for (int i = 0; i < target.Count; i++)
        {
            int current = _visible.IndexOf(target[i]);
            if (current < 0) _visible.Insert(i, target[i]);
            else if (current != i) _visible.Move(current, i);
        }

        while (_visible.Count > target.Count)
            _visible.RemoveAt(_visible.Count - 1);
    }

    private void LoadIcons(List<AppRow> rows)
    {
        if (rows.Count == 0) return;
        var paths = rows.Select(r => r.Path).ToList();
        Task.Run(() => paths.Select(p => (Path: p, Icon: FileIcons.For(p))).ToList())
            .ContinueWith(task =>
            {
                if (!task.IsCompletedSuccessfully)
                {
                    ErrorLog.Write(task.Exception);
                    return;
                }

                foreach (var (path, icon) in task.Result)
                    if (_rows.TryGetValue(path, out var row)) row.Icon = icon;
            }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void Search_Changed(object sender, TextChangedEventArgs e)
    {
        if (_loaded) ApplyOrder();
    }

    private void Sort_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string sort }) _sort = sort;
        if (_loaded) ApplyOrder();
    }

    private static AppRow? RowOf(object sender) => (sender as FrameworkElement)?.DataContext as AppRow;

    private void Folder_Click(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is not { } row) return;
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{row.Path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ErrorLog.Write(ex);
        }
    }

    private void End_Click(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is not { } row) return;
        foreach (var other in _rows.Values)
            if (other != row) other.Clear();

        var tree = ProcessTree.WithDescendants(_collector.LastSnapshot, row.Processes);
        row.AskEnd(tree.Count - row.Processes.Count);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => RowOf(sender)?.Clear();

    private async void Primary_Click(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is not { } row) return;
        if (row.Mode == AskMode.Admin) await StopElevated(row);
        else await StopRow(row, withChildren: false);
    }

    private async void Tree_Click(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { } row) await StopRow(row, withChildren: true);
    }

    private async Task StopRow(AppRow row, bool withChildren)
    {
        // Oldest first: parents go before the processes they started, so they can't start them again.
        var roots = row.Processes.OrderBy(p => p.CreateTime).ToList();
        var targets = (withChildren ? ProcessTree.WithDescendants(_collector.LastSnapshot, roots) : roots)
            .Select(p => p.Ref).ToList();

        try
        {
            var report = await Task.Run(() => ProcessStopper.StopAll(targets));
            if (report.NeedAdmin.Count > 0)
            {
                row.AskAdmin(report.NeedAdmin);
                return;
            }

            row.Clear();
            ShowResult(row.Name, report.Stopped + report.Gone, report.Refused + report.Failed);
        }
        catch (Exception ex)
        {
            ErrorLog.Write(ex);
            row.Clear();
            ShowStatus("Не получилось завершить. Подробности в журнале ошибок.", "DangerBrush");
        }

        Refresh();
    }

    private async Task StopElevated(AppRow row)
    {
        var targets = row.PendingAdmin;
        (int Stopped, int NotStopped)? result;
        // The UAC prompt takes focus; the panel must not close because of that.
        using (_panel.KeepOpen())
            result = await ElevatedStop.RunAsync(targets);

        row.Clear();
        if (result is null) ShowStatus("Отменено: в окне Windows не дали разрешения.", "WarnBrush");
        else ShowResult(row.Name, result.Value.Stopped, result.Value.NotStopped);
        Refresh();
    }

    private void ShowResult(string name, int stopped, int notStopped)
    {
        if (notStopped == 0) ShowStatus($"Завершено: {name}.", "OkBrush");
        else ShowStatus($"Завершено {stopped}, не удалось {notStopped}: эти процессы защищены или уже недоступны.", "DangerBrush");
    }

    private void ShowStatus(string text, string brushKey)
    {
        StatusText.Text = text;
        StatusText.Foreground = (Brush)FindResource(brushKey);
        StatusText.Visibility = Visibility.Visible;
    }
}
