using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Wipriss.Core;
using Wipriss.Core.Modules;
using Wipriss.Core.Text;
using Wipriss.Core.Ui;
using Wipriss.ProcessTools;

namespace Wipriss.Modules.Network;

public partial class NetworkPanel : UserControl
{
    private readonly NetworkAnalyzer _analyzer;
    private readonly TrustList _trust;
    private readonly IPanelContext _panel;
    private readonly Dictionary<string, NetRow> _rows = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ImageSource?> _icons = new(StringComparer.OrdinalIgnoreCase);
    private readonly ObservableCollection<NetRow> _visible = [];
    private readonly DispatcherTimer _timer;
    private NetworkReport? _report;
    private bool _showAll;
    private bool _loaded;
    private bool _busy;

    internal NetworkPanel(NetworkAnalyzer analyzer, TrustList trust, IPanelContext panel)
    {
        InitializeComponent();
        _analyzer = analyzer;
        _trust = trust;
        _panel = panel;
        List.ItemsSource = _visible;

        // Connections and CPU change all the time; refresh every few seconds, only while the panel is open.
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(3) };
        _timer.Tick += (_, _) => _ = Refresh();

        Loaded += (_, _) =>
        {
            _loaded = true;
            if (_analyzer.Last is { } last) Apply(last); // show the last result at once
            _ = Refresh();
            _timer.Start();
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
            var report = await Task.Run(_analyzer.Analyze);
            if (_loaded) Apply(report);
        }
        catch (Exception ex)
        {
            ErrorLog.Write(ex);
            ShowStatus("Не получилось прочитать данные о сети. Подробности в журнале ошибок.", "DangerBrush");
        }
        finally
        {
            _busy = false;
        }
    }

    private void Apply(NetworkReport report)
    {
        _report = report;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var needIcons = new List<string>();
        foreach (var app in report.Apps)
        {
            seen.Add(app.Key);
            if (!_rows.TryGetValue(app.Key, out var row)) _rows[app.Key] = row = new NetRow(app.Key);
            row.Update(app, _trust.Contains(app.Key));
            if (app.IconPath is { } path)
            {
                if (_icons.TryGetValue(path, out var icon)) row.Icon = icon;
                else if (!needIcons.Contains(path, StringComparer.OrdinalIgnoreCase)) needIcons.Add(path);
            }
        }

        foreach (string gone in _rows.Keys.Where(k => !seen.Contains(k)).ToList())
            _rows.Remove(gone);

        var flagged = _rows.Values.Where(r => r.IsFlagged).OrderByDescending(r => r.Report.Verdict.Score).ThenBy(r => r.Name).ToList();
        var target = _showAll
            ? _rows.Values.OrderByDescending(r => r.Report.TotalToday).ThenByDescending(r => r.Report.ConnectionCount).ThenBy(r => r.Name).ToList()
            : flagged;
        // Keep a row that is asking a question on screen even if it would drop out.
        foreach (var asking in _visible.Where(r => r.IsAsking && !target.Contains(r)).ToList()) target.Add(asking);
        Sync(target);
        LoadIcons(needIcons);

        int programs = report.Apps.Count(a => !a.IsWindows || a.Path is not null);
        CalmText.Text = $"Сегодня сетью пользовались {RuText.Count(programs, "программа", "программы", "программ")}, "
            + $"всего {TrafficCheck.Bytes(report.Apps.Sum(a => a.TotalToday))}. Проверены подпись, место запуска, объём, "
            + "соединения и нагрузка на процессор.";
        LoadingText.Visibility = Visibility.Collapsed;
        CalmPanel.Visibility = !_showAll && flagged.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SuspectsTab.Content = flagged.Count > 0 ? $"Подозрительные · {flagged.Count}" : "Подозрительные";
        ResetTrustButton.Content = $"Доверенных: {_trust.Count} · сбросить";
        ResetTrustButton.Visibility = _trust.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Moves rows instead of recreating them, so their visuals and state are kept.</summary>
    private void Sync(List<NetRow> target)
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
                    if (row.Report.IconPath is { } path && _icons.TryGetValue(path, out var icon)) row.Icon = icon;
            }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void View_Checked(object sender, RoutedEventArgs e)
    {
        _showAll = ReferenceEquals(sender, AllTab);
        if (!_loaded) return;
        _visible.Clear();
        List.ItemTemplate = (DataTemplate)FindResource(_showAll ? "AppRow" : "SuspectCard");
        if (_report is not null) Apply(_report);
    }

    private static NetRow? RowOf(object sender) => (sender as FrameworkElement)?.DataContext as NetRow;

    private void Trust_Click(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is not { } row) return;
        _trust.Add(row.Key);
        ShowStatus($"«{row.Name}» больше не будет среди подозрительных. Отменить можно кнопкой «сбросить» внизу.", "OkBrush");
        if (_report is not null) Apply(_report);
    }

    private void ResetTrust_Click(object sender, RoutedEventArgs e)
    {
        _trust.Clear();
        ShowStatus("Список доверенных программ очищен.", "OkBrush");
        if (_report is not null) Apply(_report);
    }

    private void Folder_Click(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender)?.Report.Path is not { } path) return;
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ErrorLog.Write(ex);
        }
    }

    /// <summary>Opens the file's page on VirusTotal. Only the SHA-256 fingerprint leaves the PC, and only on this click.</summary>
    private async void VirusTotal_Click(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender)?.Report.Path is not { } path) return;
        try
        {
            string hash = await Task.Run(() =>
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                return Convert.ToHexStringLower(SHA256.HashData(stream));
            });
            Process.Start(new ProcessStartInfo($"https://www.virustotal.com/gui/file/{hash}") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ErrorLog.Write(ex);
            ShowStatus("Не получилось прочитать файл программы.", "DangerBrush");
        }
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is not { } row) return;
        foreach (var other in _rows.Values)
            if (other != row) other.Clear();
        row.AskStop();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => RowOf(sender)?.Clear();

    private async void Primary_Click(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is not { } row) return;
        if (row.Mode == AskMode.Admin)
        {
            var targets = row.PendingAdmin;
            (int Stopped, int NotStopped)? result;
            // The UAC prompt takes focus; the panel must not close because of that.
            using (_panel.KeepOpen())
                result = await ElevatedStop.RunAsync(targets);
            row.Clear();
            if (result is null) ShowStatus("Отменено: в окне Windows не дали разрешения.", "WarnBrush");
            else ShowResult(row.Name, result.Value.NotStopped);
            _ = Refresh();
            return;
        }

        // Oldest first: parents go before the processes they started.
        var refs = row.Report.Processes.OrderBy(p => p.CreateTime).Select(p => p.Ref).ToList();
        try
        {
            var report = await Task.Run(() => ProcessStopper.StopAll(refs));
            if (report.NeedAdmin.Count > 0)
            {
                row.AskAdmin(report.NeedAdmin);
                return;
            }

            row.Clear();
            ShowResult(row.Name, report.Refused + report.Failed);
        }
        catch (Exception ex)
        {
            ErrorLog.Write(ex);
            row.Clear();
            ShowStatus("Не получилось завершить. Подробности в журнале ошибок.", "DangerBrush");
        }

        _ = Refresh();
    }

    private void ShowResult(string name, int notStopped)
    {
        if (notStopped == 0) ShowStatus($"Завершено: {name}.", "OkBrush");
        else ShowStatus($"«{name}» завершён не полностью: часть процессов защищена или недоступна.", "DangerBrush");
    }

    private void ShowStatus(string text, string brushKey)
    {
        StatusText.Text = text;
        StatusText.Foreground = (Brush)FindResource(brushKey);
        StatusText.Visibility = Visibility.Visible;
    }
}
