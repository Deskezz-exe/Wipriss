using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Wipriss.Core;
using Wipriss.Core.Modules;
using Wipriss.Core.Ui;
using Wipriss.ProcessTools;

namespace Wipriss.Modules.Ports;

public partial class PortsPanel : UserControl
{
    private readonly PortCollector _collector;
    private readonly IPanelContext _panel;
    private readonly Dictionary<string, PortRow> _rows = [];
    private readonly Dictionary<string, ImageSource?> _icons = new(StringComparer.OrdinalIgnoreCase);
    private readonly ObservableCollection<PortRow> _visible = [];
    private readonly DispatcherTimer _timer;
    private PortProtocol _protocol = PortProtocol.Tcp;
    private bool _loaded;
    private bool _busy;
    private bool _hasData;

    internal PortsPanel(PortCollector collector, IPanelContext panel)
    {
        InitializeComponent();
        _collector = collector;
        _panel = panel;
        List.ItemsSource = _visible;

        // Refreshes only while the panel is open.
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2) };
        _timer.Tick += (_, _) => _ = Refresh();

        Loaded += (_, _) =>
        {
            _loaded = true;
            _ = Refresh();
            _timer.Start();
            SearchBox.Focus();
        };
        Unloaded += (_, _) =>
        {
            _loaded = false;
            _timer.Stop();
            _rows.Clear();
            _icons.Clear();
            _visible.Clear();
        };
    }

    private async Task Refresh()
    {
        if (_busy || !_loaded) return;
        _busy = true;
        try
        {
            var protocol = _protocol;
            var items = await Task.Run(() => _collector.Collect(protocol));
            if (_loaded && protocol == _protocol) Apply(items);
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

    private void Apply(List<PortItem> items)
    {
        var seen = new HashSet<string>();
        var target = new List<PortRow>();
        var needIcons = new List<string>();
        string query = SearchBox.Text.Trim();

        foreach (var item in items)
        {
            string key = $"{item.Protocol}:{item.Port}:{item.Pid}";
            seen.Add(key);
            if (!_rows.TryGetValue(key, out var row)) _rows[key] = row = new PortRow(key);
            row.Update(item);

            if (item.Path is { } path)
            {
                if (_icons.TryGetValue(path, out var icon)) row.Icon = icon;
                else if (!needIcons.Contains(path, StringComparer.OrdinalIgnoreCase)) needIcons.Add(path);
            }

            if (Matches(item, query)) target.Add(row);
        }

        foreach (string gone in _rows.Keys.Where(k => !seen.Contains(k)).ToList())
            _rows.Remove(gone);

        _hasData = true;
        Sync(target);
        LoadIcons(needIcons);

        EmptyText.Text = query.Length > 0 ? "Ничего не нашлось."
            : _protocol == PortProtocol.Tcp ? "Ни одна программа не ждёт подключений." : "Нет открытых UDP-портов.";
        EmptyText.Visibility = _hasData && _visible.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Digits match the start of the port number, words match the program.</summary>
    private static bool Matches(PortItem item, string query)
    {
        if (query.Length == 0) return true;
        if (query.All(char.IsAsciiDigit)) return item.Port.ToString(System.Globalization.CultureInfo.InvariantCulture).StartsWith(query, StringComparison.Ordinal);
        return item.ShortCommand.Contains(query, StringComparison.OrdinalIgnoreCase)
            || (item.Process?.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    /// <summary>Moves rows instead of recreating them, so their visuals and state are kept.</summary>
    private void Sync(List<PortRow> target)
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

    private void LoadIcons(List<string> paths)
    {
        if (paths.Count == 0) return;
        foreach (string path in paths) _icons[path] = null; // loading; don't ask twice

        Task.Run(() => paths.Select(p => (Path: p, Icon: FileIcons.For(p))).ToList())
            .ContinueWith(task =>
            {
                if (!task.IsCompletedSuccessfully)
                {
                    ErrorLog.Write(task.Exception);
                    return;
                }

                if (!_loaded) return;
                foreach (var (path, icon) in task.Result) _icons[path] = icon;
                foreach (var row in _rows.Values)
                    if (row.Item.Path is { } path && _icons.TryGetValue(path, out var icon)) row.Icon = icon;
            }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void Search_Changed(object sender, TextChangedEventArgs e)
    {
        if (_loaded) Apply(PortCollector.Ordered(_rows.Values.Select(r => r.Item)));
    }

    private void Protocol_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string tag }) return;
        _protocol = tag == "udp" ? PortProtocol.Udp : PortProtocol.Tcp;
        if (!_loaded) return;

        _rows.Clear();
        _visible.Clear();
        _hasData = false;
        EmptyText.Visibility = Visibility.Collapsed;
        _ = Refresh();
    }

    private static PortRow? RowOf(object sender) => (sender as FrameworkElement)?.DataContext as PortRow;

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is not { Item.Process: { } owner } row) return;
        foreach (var other in _rows.Values)
            if (other != row) other.Clear();

        int children = ProcessTree.WithDescendants(_collector.LastSnapshot, [owner]).Count - 1;
        row.AskStop(children);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => RowOf(sender)?.Clear();

    private async void Primary_Click(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is not { } row) return;
        if (row.Mode == AskMode.Admin) await StopElevated(row);
        else if (row.Item.Process is { } owner) await StopTree(row, owner);
    }

    private async void Launcher_Click(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { Item.Launcher: { } launcher } row) await StopTree(row, launcher);
    }

    /// <summary>Stops a process with everything it started (otherwise npm or a watcher may start the server again).</summary>
    private async Task StopTree(PortRow row, ProcessEntry root)
    {
        var targets = ProcessTree.WithDescendants(_collector.LastSnapshot, [root]).Select(p => p.Ref).ToList();
        int port = row.Item.Port;
        try
        {
            var report = await Task.Run(() => ProcessStopper.StopAll(targets));
            if (report.NeedAdmin.Count > 0)
            {
                row.AskAdmin(report.NeedAdmin);
                return;
            }

            row.Clear();
            await ReportPort(port, report.Refused + report.Failed);
        }
        catch (Exception ex)
        {
            ErrorLog.Write(ex);
            row.Clear();
            ShowStatus("Не получилось остановить. Подробности в журнале ошибок.", "DangerBrush");
        }
    }

    private async Task StopElevated(PortRow row)
    {
        var targets = row.PendingAdmin;
        int port = row.Item.Port;
        (int Stopped, int NotStopped)? result;
        // The UAC prompt takes focus; the panel must not close because of that.
        using (_panel.KeepOpen())
            result = await ElevatedStop.RunAsync(targets);

        row.Clear();
        if (result is null) ShowStatus("Отменено: в окне Windows не дали разрешения.", "WarnBrush");
        else await ReportPort(port, result.Value.NotStopped);
    }

    /// <summary>Checks the port again after a short pause and says plainly whether it is free now.</summary>
    private async Task ReportPort(int port, int notStopped)
    {
        await Task.Delay(400);
        var protocol = _protocol;
        var items = await Task.Run(() => _collector.Collect(protocol));
        if (!_loaded) return;
        Apply(items);

        var holder = items.FirstOrDefault(i => i.Port == port);
        if (holder is null) ShowStatus($"Порт {port} свободен.", "OkBrush");
        else if (notStopped > 0) ShowStatus($"Порт {port} занят: часть процессов защищена или недоступна.", "DangerBrush");
        else ShowStatus($"Порт {port} снова занят: его открыл «{holder.ShortCommand}». Попробуйте «Вместе с запуском».", "WarnBrush");
    }

    private void ShowStatus(string text, string brushKey)
    {
        StatusText.Text = text;
        StatusText.Foreground = (Brush)FindResource(brushKey);
        StatusText.Visibility = Visibility.Visible;
    }
}
