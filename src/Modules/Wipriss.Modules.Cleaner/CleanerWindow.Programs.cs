using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Wipriss.Cleaner;

namespace Wipriss.Modules.Cleaner;

// The «Установленные программы» tab: the list, then one removal step by step (confirm → the program's
// uninstaller → its tails → confirm → deleted), all driven by ProgramsFlow.
public partial class CleanerWindow
{
    private bool _programsMode;
    private readonly ObservableCollection<ProgramRow> _programRows = [];
    private readonly ObservableCollection<Row> _tailRows = [];
    private List<ProgramRow> _allPrograms = [];
    private List<AppEntry>? _shownPrograms;
    private readonly Dictionary<string, ImageSource?> _icons = [];
    private int _sort; // 0 name, 1 size, 2 date
    private List<TailRow> _tails = [];
    private List<TailItem>? _shownTails;
    private AppEntry? _pendingApp;
    private bool _confirmTails;
    private Action? _flowFirst, _flowSecond;

    private ProgramsFlow Flow => _module.Programs;

    private void InitPrograms()
    {
        ProgramList.ItemsSource = _programRows;
        TailList.ItemsSource = _tailRows;
        Flow.Changed += RefreshPrograms;
        Closed += (_, _) => Flow.Changed -= RefreshPrograms;
    }

    private void ProgramsTab_Checked(object sender, RoutedEventArgs e)
    {
        _programsMode = true;
        ResultsBox.Visibility = Visibility.Collapsed;
        ProgramsBox.Visibility = Visibility.Visible;
        Refresh();
        if (Flow.Programs is null && !Flow.Loading) Flow.Load();
        RefreshPrograms();
    }

    /// <summary>Back to the cleaner's sections.</summary>
    private void LeavePrograms()
    {
        _programsMode = false;
        ProgramsBox.Visibility = Visibility.Collapsed;
        ResultsBox.Visibility = Visibility.Visible;
        ProgramsTab.IsChecked = false;
    }

    private bool FlowBusy => Flow.State is not (FlowState.Idle or FlowState.Done or FlowState.Failed);

    private void RefreshPrograms()
    {
        if (!_programsMode) return;
        var flow = Flow;

        if (!ReferenceEquals(flow.Programs, _shownPrograms)) BuildPrograms(flow.Programs);
        ShowProgramsState();

        // After a removal (or a restore): what happened.
        bool banner = flow.State == FlowState.Done && (flow.Target is not null || flow.RestoreText is not null);
        ProgramBanner.Visibility = banner ? Visibility.Visible : Visibility.Collapsed;
        if (banner)
        {
            if (flow.RestoreText is { } restored)
            {
                ProgramBannerTitle.Text = "Готово";
                ProgramBannerText.Text = restored;
                RestoreButton.Visibility = Visibility.Collapsed;
            }
            else
            {
                ProgramBannerTitle.Text = $"«{flow.Target!.Name}» удалена";
                var lines = new List<string> { flow.Freed > 0 ? $"Вместе с остатками освобождено {Sizes.Format(flow.Freed)}." : "Остатки удалены." };
                if (flow.NotDeleted > 0) lines.Add($"{flow.NotDeleted} пункт(ов) удалить не удалось: заняты другой программой или в окне Windows было «Нет».");
                if (flow.Backup is not null) lines.Add("Удалённые записи реестра скопированы: их можно вернуть.");
                ProgramBannerText.Text = string.Join("\n", lines);
                RestoreButton.Visibility = flow.Backup is null ? Visibility.Collapsed : Visibility.Visible;
            }
        }

        bool tails = flow.State == FlowState.Tails;
        TailsBox.Visibility = tails ? Visibility.Visible : Visibility.Collapsed;
        ProgramListBox.Visibility = tails ? Visibility.Collapsed : Visibility.Visible;
        if (tails && !ReferenceEquals(flow.Tails, _shownTails)) BuildTails(flow.Tails ?? []);
        if (!tails) _confirmTails = false;
        ShowFlow();
    }

    // ---------- the list ----------

    private void BuildPrograms(List<AppEntry>? programs)
    {
        _shownPrograms = programs;
        _allPrograms = (programs ?? []).Select(p => new ProgramRow(p)).ToList();
        foreach (var row in _allPrograms)
            if (_icons.TryGetValue(row.App.Id, out var known)) row.Icon = known;
        var missing = _allPrograms.Where(r => !_icons.ContainsKey(r.App.Id)).ToList();
        if (missing.Count > 0)
        {
            var dispatcher = Dispatcher;
            Task.Run(() =>
            {
                foreach (var row in missing)
                {
                    var icon = ProgramRow.LoadIcon(row.App);
                    dispatcher.BeginInvoke(() => { _icons[row.App.Id] = icon; row.Icon = icon; });
                }
            });
        }
        ApplyProgramFilter();
    }

    private void ApplyProgramFilter()
    {
        var text = ProgramSearch.Text.Trim();
        IEnumerable<ProgramRow> rows = _allPrograms;
        if (text.Length > 0)
            rows = rows.Where(r => r.App.Name.Contains(text, StringComparison.CurrentCultureIgnoreCase)
                                   || (r.App.Publisher?.Contains(text, StringComparison.CurrentCultureIgnoreCase) ?? false));
        rows = _sort switch
        {
            1 => rows.OrderByDescending(r => r.App.Size),
            2 => rows.OrderByDescending(r => r.App.Installed ?? DateTime.MinValue),
            _ => rows.OrderBy(r => r.App.Name, StringComparer.CurrentCultureIgnoreCase),
        };
        _programRows.Clear();
        foreach (var row in rows) _programRows.Add(row);
        ShowProgramsState();
    }

    private void ShowProgramsState()
    {
        var flow = Flow;
        long total = _allPrograms.Sum(r => Math.Max(0, r.App.Size));
        ProgramsHint.Text = _allPrograms.Count == 0 ? "" : $"{_allPrograms.Count} программ, вместе {Sizes.Format(total)}";
        string? empty = flow.Loading && _allPrograms.Count == 0 ? $"Читаю список программ… {flow.ProgressText}"
            : flow.ListError is { } error && _allPrograms.Count == 0 ? error
            : _programRows.Count == 0 && _allPrograms.Count > 0 ? "Ничего не нашлось"
            : null;
        ProgramsEmpty.Text = empty ?? "";
        ProgramsEmpty.Visibility = empty is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ProgramSearch_Changed(object sender, TextChangedEventArgs e)
    {
        if (ProgramList is null) return; // during InitializeComponent
        ApplyProgramFilter();
    }

    private void Sort_Click(object sender, RoutedEventArgs e)
    {
        _sort = (_sort + 1) % 3;
        SortButton.Content = _sort switch { 1 => "По размеру", 2 => "По дате", _ => "По имени" };
        ApplyProgramFilter();
    }

    private void ReloadPrograms_Click(object sender, RoutedEventArgs e) => Flow.Load();

    private void Uninstall_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ProgramRow row || FlowBusy) return;
        _pendingApp = row.App;
        ShowFlow();
    }

    private void ProgramBanner_Click(object sender, RoutedEventArgs e) => Flow.Dismiss();

    private void RestoreLast_Click(object sender, RoutedEventArgs e)
    {
        if (Flow.Backup is { } folder) Flow.Restore(folder);
    }

    // ---------- the tails ----------

    private void BuildTails(List<TailItem> tails)
    {
        _shownTails = tails;
        _tails = tails.Select(t => new TailRow(t, null)).ToList();
        foreach (var row in _tails) row.SelectionChanged += UpdateTailTotals;
        TailsTitle.Text = $"Что осталось от «{Flow.Target?.Name}»";
        TailsHint.Text = tails.Count == 0 ? ""
            : (Flow.Forced ? "Программа удаляется принудительно: её папка и записи ниже. " : "Деинсталлятор закончил. ")
              + "Отметьте, что удалить. Зелёное точно её, жёлтое посмотрите сами.";
        ShowTails();
    }

    private void ShowTails()
    {
        _tailRows.Clear();
        var disk = _tails.Where(t => t.Item.Kind is TailKind.Folder or TailKind.File).ToList();
        var other = _tails.Except(disk).ToList();
        if (disk.Count > 0)
        {
            _tailRows.Add(new TailHeaderRow("На дисках", disk.Sum(t => t.Item.Size)));
            foreach (var row in disk) AddTail(row);
        }
        if (other.Count > 0)
        {
            _tailRows.Add(new TailHeaderRow("В реестре и системе", 0));
            foreach (var row in other) AddTail(row);
        }
        TailsEmpty.Visibility = _tails.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateTailTotals();
    }

    private void AddTail(TailRow row)
    {
        _tailRows.Add(row);
        if (row.Expanded) foreach (var child in row.Children) _tailRows.Add(child);
    }

    private void UpdateTailTotals()
    {
        var chosen = _tails.Where(t => t.AnyChosen).ToList();
        int count = chosen.Sum(t => t.ChosenIds().Count());
        long size = chosen.Sum(t => t.ChosenSize);
        TailsSelected.Text = count == 0 ? "Ничего не выбрано" : $"Выбрано {count}" + (size > 0 ? $": освободится примерно {Sizes.Format(size)}" : "");
        TailsSelectedHint.Text = count == 0 ? "Отметьте галочками, что удалить"
            : chosen.Any(t => t.ChosenNeedsAdmin) ? "Для части пунктов Windows попросит права администратора" : "";
        TailsDeleteButton.IsEnabled = count > 0;
        TailsDeleteButton.Opacity = count > 0 ? 1 : 0.45;
    }

    private void Tail_Click(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not TailRow row || row.Children.Count == 0) return;
        row.Expanded = !row.Expanded;
        ShowTails();
    }

    private void TailTick_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not TailRow row) return;
        if (row.Parent is null) row.Toggle(); else row.ToggleChild();
    }

    private void SelectTails(Func<TailItem, bool> pick)
    {
        foreach (var row in _tails) row.SelectBy(pick);
        UpdateTailTotals();
    }

    private void TailsGreen_Click(object sender, RoutedEventArgs e) => SelectTails(i => i.Color == CleanColor.Green);
    private void TailsAll_Click(object sender, RoutedEventArgs e) => SelectTails(_ => true);
    private void TailsNone_Click(object sender, RoutedEventArgs e) => SelectTails(_ => false);
    private void TailsSkip_Click(object sender, RoutedEventArgs e) => Flow.Quit();

    private void TailsDelete_Click(object sender, RoutedEventArgs e)
    {
        if (!_tails.Any(t => t.AnyChosen)) return;
        _confirmTails = true;
        ShowFlow();
    }

    // ---------- the step on top ----------

    private void ShowFlow()
    {
        var flow = Flow;
        var name = (_pendingApp ?? flow.Target)?.Name ?? "";
        FlowWarning.Visibility = Visibility.Collapsed;
        FlowTrack.Visibility = Visibility.Collapsed;
        FlowFirst.Style = (Style)FindResource("PrimaryButton");
        FlowIcon.Content = "";
        FlowIcon.Foreground = (Brush)FindResource("TextBrush");
        bool show = true;

        if (_pendingApp is { } app)
        {
            if (app.Store)
                Set($"Удалить «{name}»?", "Приложение из Microsoft Store удалит сама Windows. Потом Wipriss покажет, что от него осталось. Без вашего выбора ничего больше не удалится.",
                    "Удалить", () => StartUninstall(app, false), "Отмена", () => { _pendingApp = null; ShowFlow(); });
            else if (app.HasUninstaller)
                Set($"Удалить «{name}»?", "Сначала запустится деинсталлятор самой программы: следуйте его окнам. Потом Wipriss найдёт, что от неё осталось на дисках и в реестре, и покажет список. Без вашего выбора ничего больше не удалится.",
                    "Удалить", () => StartUninstall(app, false), "Отмена", () => { _pendingApp = null; ShowFlow(); });
            else
                Set($"У «{name}» нет деинсталлятора", "Её можно удалить принудительно: Wipriss найдёт папку программы и её записи и покажет список. Удалится только то, что вы отметите.",
                    "Удалить принудительно", () => StartUninstall(app, true), "Отмена", () => { _pendingApp = null; ShowFlow(); });
            FlowFirst.Style = (Style)FindResource("DangerButton");
        }
        else if (_confirmTails && flow.State == FlowState.Tails)
        {
            var chosen = _tails.Where(t => t.AnyChosen).ToList();
            int count = chosen.Sum(t => t.ChosenIds().Count());
            long size = chosen.Sum(t => t.ChosenSize);
            int yellow = chosen.Count(t => t.Item.Color == CleanColor.Yellow || t.Children.Any(c => c.Item.Color == CleanColor.Yellow && t.ChosenIds().Contains(c.Item.Id)));
            Set("Удалить выбранные остатки?",
                $"{count} пунктов" + (size > 0 ? $", освободится примерно {Sizes.Format(size)}" : "") + ". Файлы удаляются сразу, мимо Корзины. Записи реестра сначала копируются: их можно будет вернуть.",
                "Да, удалить", () => { _confirmTails = false; Flow.DeleteTails(chosen.SelectMany(t => t.ChosenIds()).ToList()); },
                "Отмена", () => { _confirmTails = false; ShowFlow(); });
            FlowFirst.Style = (Style)FindResource("DangerButton");
            var warnings = new List<string>();
            if (yellow > 0) warnings.Add($"Среди выбранного {yellow} «возможно, важных»: проверьте, что они вам не нужны.");
            if (chosen.Any(t => t.ChosenNeedsAdmin)) warnings.Add("Для части пунктов Windows спросит разрешение администратора.");
            Warn(string.Join("\n", warnings));
        }
        else switch (flow.State)
        {
            case FlowState.Running:
                if (flow.Target?.Store == true) Set($"Удаляется «{name}»", "Windows удаляет приложение. Это займёт несколько секунд.", null, null, null, null);
                else Set($"Удаляется «{name}»", "Работает деинсталлятор программы: следуйте его окнам. Когда он закончит, Wipriss сам поищет, что осталось.",
                    null, null, "Деинсталлятор уже закрылся", flow.ContinueWaiting);
                break;
            case FlowState.NotRemoved:
                var why = flow.StageText ?? (flow.NoUninstaller ? "У программы нет деинсталлятора."
                    : "Деинсталлятор закрылся, а программа всё ещё в списке установленных: удаление отменили, или деинсталлятор не справился.");
                bool store = flow.Target?.Store == true;
                Set(flow.NoUninstaller ? $"У «{name}» нет рабочего деинсталлятора" : $"«{name}» не удалилась",
                    why + (store ? "" : "\n\nМожно удалить принудительно: Wipriss найдёт папку программы и её записи и покажет список. Удалится только то, что вы отметите."),
                    store ? null : "Удалить принудительно", store ? null : flow.Force, "Закрыть", flow.Quit);
                FlowIcon.Content = "";
                FlowIcon.Foreground = (Brush)FindResource("WarnBrush");
                if (!store) FlowFirst.Style = (Style)FindResource("DangerButton");
                break;
            case FlowState.Searching or FlowState.Deleting or FlowState.Restoring:
                Set(flow.State == FlowState.Deleting ? "Удаляю остатки" : flow.State == FlowState.Restoring ? "Возвращаю записи" : $"Ищу, что осталось от «{name}»",
                    flow.ProgressText + "…", null, null, null, null);
                if (flow.State == FlowState.Deleting) Warn("Если Windows спросит разрешение, это для папок в Program Files и записей HKLM.");
                FlowTrack.Visibility = Visibility.Visible;
                FlowFill.Width = 360.0 * Math.Clamp(flow.ProgressPercent, 0, 100) / 100;
                break;
            case FlowState.Failed:
                Set("Удаление прервалось", flow.Error ?? "", "Понятно", flow.Dismiss, null, null);
                FlowIcon.Content = "";
                FlowIcon.Foreground = (Brush)FindResource("WarnBrush");
                break;
            default:
                show = false;
                break;
        }
        FlowBox.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    private void StartUninstall(AppEntry app, bool forced)
    {
        _pendingApp = null;
        Flow.Uninstall(app, forced);
        ShowFlow();
    }

    private void Set(string title, string text, string? first, Action? firstAction, string? second, Action? secondAction)
    {
        FlowTitle.Text = title;
        FlowText.Text = text;
        FlowFirst.Content = first;
        FlowFirst.Visibility = first is null ? Visibility.Collapsed : Visibility.Visible;
        FlowSecond.Content = second;
        FlowSecond.Visibility = second is null ? Visibility.Collapsed : Visibility.Visible;
        _flowFirst = firstAction;
        _flowSecond = secondAction;
    }

    private void Warn(string text)
    {
        FlowWarning.Text = text;
        FlowWarning.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void FlowFirst_Click(object sender, RoutedEventArgs e) => _flowFirst?.Invoke();
    private void FlowSecond_Click(object sender, RoutedEventArgs e) => _flowSecond?.Invoke();

    // ---------- registry copies ----------

    private void Backups_Click(object sender, RoutedEventArgs e)
    {
        var list = ProgramsFlow.Backups().Select(b => new BackupRow(b.Folder, b.Program, b.When)).ToList();
        BackupList.ItemsSource = list;
        BackupsEmpty.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        BackupsBox.Visibility = Visibility.Visible;
    }

    private void BackupsClose_Click(object sender, RoutedEventArgs e) => BackupsBox.Visibility = Visibility.Collapsed;

    private void BackupRestore_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not BackupRow row || FlowBusy) return;
        BackupsBox.Visibility = Visibility.Collapsed;
        Flow.Restore(row.Folder);
    }

    /// <summary>Esc closes what is on top (never a running step).</summary>
    private void EscapePrograms()
    {
        BackupsBox.Visibility = Visibility.Collapsed;
        if (_pendingApp is not null || _confirmTails)
        {
            _pendingApp = null;
            _confirmTails = false;
            ShowFlow();
        }
    }
}
