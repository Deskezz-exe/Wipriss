using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Wipriss.Cleaner;

namespace Wipriss.Modules.Cleaner;

/// <summary>
/// The cleaner window: sections on the left, the programs of a section on the right (each opens into its
/// kinds of data, each of those into a card), the selection and «Удалить» at the bottom.
/// </summary>
public partial class CleanerWindow : Window
{
    private readonly CleanerModule _module;
    private readonly ObservableCollection<Row> _rows = [];
    private readonly SectionRow[] _sections =
    [
        new("Система", "", [CleanCategory.Windows, CleanCategory.Graphics]),
        new("Браузеры", "", [CleanCategory.Browsers]),
        new("Приложения", "", [CleanCategory.Apps, CleanCategory.Developer]),
        new("Прочее", "", [CleanCategory.Models, CleanCategory.Leftovers, CleanCategory.Installers, CleanCategory.Projects]),
    ];
    private SectionRow _section;
    private List<CategoryRow> _categories = [];
    private List<CleanGroup>? _shown;
    private int _shownItems;

    /// <summary>Programs whose files are locked while they run: group id → process name.</summary>
    private static readonly Dictionary<string, (string Process, string Name)> RunningCheck = new()
    {
        ["browser:firefox"] = ("firefox", "Firefox"),
        ["browser:chrome"] = ("chrome", "Google Chrome"),
        ["browser:edge"] = ("msedge", "Microsoft Edge"),
        ["browser:yandex"] = ("browser", "Яндекс Браузер"),
        ["browser:brave"] = ("brave", "Brave"),
        ["browser:opera"] = ("opera", "Opera"),
        ["browser:operagx"] = ("opera", "Opera GX"),
        ["browser:vivaldi"] = ("vivaldi", "Vivaldi"),
        ["app:telegram"] = ("Telegram", "Telegram"),
        ["app:spotify"] = ("Spotify", "Spotify"),
        ["app:steam"] = ("steam", "Steam"),
        ["app:epic"] = ("EpicGamesLauncher", "Epic Games"),
    };

    internal CleanerWindow(CleanerModule module)
    {
        InitializeComponent();
        _module = module;
        _section = _sections[0];
        _section.IsSelected = true;
        List.ItemsSource = _rows;
        SectionList.ItemsSource = _sections;
        _module.Changed += Refresh;
        Closed += (_, _) => _module.Changed -= Refresh;
        InitPrograms();
        Loaded += (_, _) => { Refresh(); UpdateDisk(); };
        KeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            ConfirmBox.Visibility = Visibility.Collapsed;
            SelectAllBox.Visibility = Visibility.Collapsed;
            EscapePrograms();
        };

    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        Wipriss.Core.Ui.NativeWindowStyle.DarkTitleBar(this);
        Wipriss.Core.Ui.NativeWindowStyle.FrameColors(this, (System.Windows.Media.Color)FindResource("HairlineColor"),
            (System.Windows.Media.Color)FindResource("WindowColor"), (System.Windows.Media.Color)FindResource("TextColor"));
    }

    private void Refresh()
    {
        bool busy = _module.Busy;
        var results = _module.Results;
        IntroBox.Visibility = !busy && results is null && !_programsMode ? Visibility.Visible : Visibility.Collapsed;
        ProgressBox.Visibility = busy && !_programsMode ? Visibility.Visible : Visibility.Collapsed;
        CancelButton.Visibility = _module.Deleting ? Visibility.Collapsed : Visibility.Visible; // a deletion is not cut in half
        ErrorText.Text = _module.LastError ?? "";
        ErrorText.Visibility = _module.LastError is null ? Visibility.Collapsed : Visibility.Visible;

        if (busy)
        {
            ConfirmBox.Visibility = Visibility.Collapsed;
            // After a deletion the scanner searches again: say why.
            ProgressText.Text = (!_module.Deleting && _module.LastDelete is not null ? "Удалено. Проверяю, что осталось" : _module.ProgressText) + "…";
            ProgressFill.Width = ProgressTrack.ActualWidth > 0 ? ProgressTrack.ActualWidth * _module.ProgressPercent / 100.0 : 0;
            return;
        }

        ShowDeleteResult();
        UnhideButton.Content = $"Показать скрытые ({_module.Settings.Hidden.Count})";
        UnhideButton.Visibility = _module.Settings.Hidden.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (results is null) return;

        int items = results.Sum(g => g.Items.Count);
        if (!ReferenceEquals(results, _shown) || items != _shownItems) Build(results);
    }

    /// <summary>All rows of all sections; selection survives switching sections and hiding items.</summary>
    private void Build(List<CleanGroup> results)
    {
        var expandedGroups = _categories.SelectMany(c => c.Groups).Where(g => g.Expanded).Select(g => g.Group.Id).ToHashSet();
        var collapsed = _categories.Where(c => !c.Expanded).Select(c => c.Category).ToHashSet();
        bool same = ReferenceEquals(results, _shown);
        var selected = same
            ? _categories.SelectMany(c => c.Groups).SelectMany(g => g.Items).ToDictionary(i => i.Item, i => i.IsChecked)
            : [];
        _shown = results;
        _shownItems = results.Sum(g => g.Items.Count);
        _categories = results
            .GroupBy(g => g.Category)
            .OrderBy(g => g.Key)
            .Select(c => new CategoryRow(c.Key, c.Select(g => new GroupRow(g)).OrderByDescending(g => g.Size).ToList())
            {
                Expanded = !collapsed.Contains(c.Key),
            })
            .ToList();
        foreach (var group in _categories.SelectMany(c => c.Groups))
        {
            group.Expanded = expandedGroups.Contains(group.Group.Id);
            foreach (var item in group.Items)
                if (selected.TryGetValue(item.Item, out var was)) item.SetChecked(was, notifyGroup: false);
            group.SelectionChanged += UpdateTotals;
        }
        foreach (var section in _sections)
        {
            section.Scanned = true;
            // In the order the section names them (models and leftovers before projects).
            section.Rows = section.Categories.Select(k => _categories.FirstOrDefault(c => c.Category == k)).OfType<CategoryRow>().ToList();
            section.Refresh();
        }
        long all = _categories.SelectMany(c => c.Groups).Sum(g => g.Size);
        FoundText.Text = $"Найдено {Sizes.Format(all)}";
        ShowSection();
    }

    private void ShowSection()
    {
        _rows.Clear();
        foreach (var category in _section.Rows)
        {
            // A section of one kind needs no sub-heading.
            if (_section.Rows.Count > 1) _rows.Add(category);
            if (!category.Expanded && _section.Rows.Count > 1) continue;
            foreach (var group in category.Groups)
            {
                _rows.Add(group);
                if (!group.Expanded) continue;
                foreach (var item in group.Items)
                {
                    _rows.Add(item);
                    if (item.Expanded) _rows.Add(item.Details);
                }
            }
        }
        SectionTitle.Text = _section.Name;
        EmptyText.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateTotals();
    }

    private IEnumerable<ItemRow> AllItems => _categories.SelectMany(c => c.Groups).SelectMany(g => g.Items);
    private IEnumerable<ItemRow> SectionItems => _section.Rows.SelectMany(c => c.Groups).SelectMany(g => g.Items);

    private void UpdateTotals()
    {
        var section = SectionItems.ToList();
        long total = section.Where(i => i.Item.Size > 0).Sum(i => i.Item.Size);
        long green = section.Where(i => i.Item.Color == CleanColor.Green && i.CanSelect && i.Item.Size > 0).Sum(i => i.Item.Size);
        SectionHint.Text = total == 0 ? "" : $"Найдено {Sizes.Format(total)}, из них можно удалить {Sizes.Format(green)}";

        var chosen = AllItems.Where(i => i.IsChecked).ToList();
        long size = chosen.Where(i => i.Item.Size > 0).Sum(i => i.Item.Size);
        SelectedText.Text = chosen.Count == 0 ? "Ничего не выбрано" : $"Выбрано {chosen.Count}: освободится примерно {Sizes.Format(size)}";
        var bySection = _sections
            .Select(s => (s.Name, Size: s.Rows.SelectMany(c => c.Groups).SelectMany(g => g.Items).Where(i => i.IsChecked && i.Item.Size > 0).Sum(i => i.Item.Size)))
            .Where(x => x.Size > 0)
            .Select(x => $"{x.Name} {Sizes.Format(x.Size)}");
        SelectedHint.Text = chosen.Count == 0 ? "Отметьте, что удалить: галочкой у программы или у отдельного пункта" : string.Join(" · ", bySection);
        DeleteButton.IsEnabled = chosen.Count > 0;
        DeleteButton.Opacity = chosen.Count > 0 ? 1 : 0.45;
        ClearButton.IsEnabled = chosen.Count > 0;
        ClearButton.Opacity = chosen.Count > 0 ? 1 : 0.45;
        _selectedByDrive.Clear();
        foreach (var item in chosen.Where(i => i.Item.Size > 0 && i.Item.Paths.Count > 0))
        {
            var root = Path.GetPathRoot(item.Item.Paths[0]) ?? "";
            _selectedByDrive[root] = _selectedByDrive.GetValueOrDefault(root) + item.Item.Size;
        }
        UpdateDisk();
    }

    private readonly Dictionary<string, long> _selectedByDrive = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every fixed drive: used part, the part the selection would free (green), free.</summary>
    private void UpdateDisk()
    {
        DiskList.ItemsSource = Disk.All()
            .Select(d => new DiskRow(d.Name, d.Total, d.Free, _selectedByDrive.GetValueOrDefault(d.Name)))
            .ToList();
    }
    private void ShowDeleteResult()
    {
        var result = _module.LastDelete;
        ResultBanner.Visibility = result is null ? Visibility.Collapsed : Visibility.Visible;
        if (result is null) return;
        ResultTitle.Text = result.Freed > 0 ? $"Освобождено {Sizes.Format(result.Freed)}" : "Ничего не удалилось";
        var lines = new List<string>();
        long free = Disk.Free();
        if (result.FreeBefore >= 0 && free >= 0)
            lines.Add($"Свободно на дисках: было {Sizes.Format(result.FreeBefore)}, стало {Sizes.Format(free)}.");
        if (result.Failed > 0)
            lines.Add($"{result.Failed} файл(ов) пропущено: они заняты программой или к ним нет доступа. Закройте программы и удалите ещё раз.");
        lines.Add("Список ниже уже обновлён: в нём то, что осталось.");
        ResultText.Text = string.Join("\n", lines);
    }

    private void DismissResult_Click(object sender, RoutedEventArgs e) => _module.DismissDelete();

    private void SelectGreen_Click(object sender, RoutedEventArgs e) =>
        SetAll(i => i.Item.Color == CleanColor.Green && i.Item.Size != 0);

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        var yellow = AllItems.Where(i => i.CanSelect && i.Item.Color == CleanColor.Yellow).ToList();
        long size = yellow.Where(i => i.Item.Size > 0).Sum(i => i.Item.Size);
        SelectAllText.Text = $"Вместе с зелёным будут выбраны {Wipriss.Core.Text.RuText.Count(yellow.Count, "пункт", "пункта", "пунктов")} «возможно, важно» ({Sizes.Format(size)}): "
            + "входы на сайтах, история, модели ИИ, ваши проекты, настройки удалённых программ. Среди них могут быть нужные вам данные. "
            + "Перед удалением лучше пройтись по списку и снять лишнее.";
        SelectAllBox.Visibility = Visibility.Visible;
    }

    private void SelectAllCancel_Click(object sender, RoutedEventArgs e) => SelectAllBox.Visibility = Visibility.Collapsed;

    private void SelectAllConfirm_Click(object sender, RoutedEventArgs e)
    {
        SelectAllBox.Visibility = Visibility.Collapsed;
        SetAll(i => i.Item.Size != 0);
    }

    private void Clear_Click(object sender, RoutedEventArgs e) => SetAll(_ => false);

    /// <summary>Selects in every section; red and Windows-only items stay unselectable (SetChecked refuses them).</summary>
    private void SetAll(Func<ItemRow, bool> select)
    {
        foreach (var item in AllItems) item.SetChecked(select(item), notifyGroup: false);
        foreach (var group in _categories.SelectMany(c => c.Groups)) group.Changed();
        UpdateTotals();
    }

    private void Section_Checked(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not SectionRow section) return;
        if (_programsMode)
        {
            LeavePrograms();
            Refresh();
        }
        if (section == _section) { ShowSection(); return; }
        _section.IsSelected = false;
        _section = section;
        _section.IsSelected = true;
        ShowSection();
    }

    private void Scan_Click(object sender, RoutedEventArgs e) => _module.StartScan();

    private void Cancel_Click(object sender, RoutedEventArgs e) => _module.Cancel();

    private void Unhide_Click(object sender, RoutedEventArgs e) => _module.ShowHiddenAgain();

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        var chosen = AllItems.Where(i => i.IsChecked).ToList();
        if (chosen.Count == 0) return;
        long size = chosen.Where(i => i.Item.Size > 0).Sum(i => i.Item.Size);
        ConfirmSize.Text = $"Освободится примерно {Sizes.Format(size)} ({Wipriss.Core.Text.RuText.Count(chosen.Count, "пункт", "пункта", "пунктов")}).";
        int yellow = chosen.Count(i => i.Item.Color == CleanColor.Yellow);
        ConfirmText.Text = "Файлы удаляются сразу, мимо Корзины, и вернуть их будет нельзя. Занятые программами файлы будут пропущены."
            + (yellow > 0 ? $"\nСреди выбранного {yellow} «возможно, важных»: проверьте, что они вам не нужны." : "");

        var running = chosen.Select(i => i.Group.Group.Id).Distinct()
            .Where(RunningCheck.ContainsKey)
            .Select(id => RunningCheck[id])
            .Where(p => IsRunning(p.Process))
            .Select(p => p.Name)
            .Distinct()
            .ToList();
        ConfirmWarning.Text = running.Count == 0 ? "" : $"Сейчас открыты: {string.Join(", ", running)}. Закройте их, иначе часть их файлов не удалится.";
        ConfirmWarning.Visibility = running.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        ConfirmBox.Visibility = Visibility.Visible;
    }

    private static bool IsRunning(string name)
    {
        var processes = Process.GetProcessesByName(name);
        foreach (var p in processes) p.Dispose();
        return processes.Length > 0;
    }

    private void ConfirmCancel_Click(object sender, RoutedEventArgs e) => ConfirmBox.Visibility = Visibility.Collapsed;

    private void ConfirmDelete_Click(object sender, RoutedEventArgs e)
    {
        ConfirmBox.Visibility = Visibility.Collapsed;
        var chosen = AllItems.Where(i => i.IsChecked && i.CanSelect).Select(i => (i.Group.Group, i.Item)).ToList();
        _module.Delete(chosen);
    }

    private void Category_Click(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not CategoryRow category) return;
        category.Expanded = !category.Expanded;
        ShowSection();
    }

    private void Group_Click(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not GroupRow group) return;
        group.Expanded = !group.Expanded;
        ShowSection();
    }

    private void GroupTick_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is GroupRow group) group.Toggle();
    }

    private void Item_Click(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ItemRow item) return;
        item.Expanded = !item.Expanded;
        int index = _rows.IndexOf(item);
        if (index < 0) return;
        if (item.Expanded) _rows.Insert(index + 1, item.Details);
        else if (index + 1 < _rows.Count && _rows[index + 1] == item.Details) _rows.RemoveAt(index + 1);
    }

    private void Folder_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not DetailRow detail) return;
        var path = detail.Owner.Item.Paths.FirstOrDefault(p => File.Exists(p) || Directory.Exists(p));
        if (path is null) return;
        try
        {
            // The folder itself; for a file, its folder with the file selected.
            var args = Directory.Exists(path) ? $"\"{path}\"" : $"/select,\"{path}\"";
            Process.Start(new ProcessStartInfo("explorer.exe", args) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException) { }
    }

    private void Hide_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is DetailRow detail) _module.Hide(detail.Owner.Item);
    }
}

/// <summary>One drive in the left column.</summary>
public sealed class DiskRow
{
    internal DiskRow(string root, long total, long free, long selected)
    {
        long used = Math.Max(0, total - free);
        selected = Math.Min(selected, used);
        Name = "Диск " + root.TrimEnd('\\');
        TotalText = Sizes.Format(total);
        UsedText = $"Занято {Sizes.Format(used)} · свободно {Sizes.Format(free)}";
        AfterText = selected > 0 ? $"После удаления свободно {Sizes.Format(free + selected)}" : "";
        AfterVisibility = selected > 0 ? Visibility.Visible : Visibility.Collapsed;
        UsedStar = new GridLength(Math.Max(0, used - selected), GridUnitType.Star);
        SelectedStar = new GridLength(selected, GridUnitType.Star);
        FreeStar = new GridLength(Math.Max(0, free), GridUnitType.Star);
    }

    public string Name { get; }
    public string TotalText { get; }
    public string UsedText { get; }
    public string AfterText { get; }
    public Visibility AfterVisibility { get; }
    public GridLength UsedStar { get; }
    public GridLength SelectedStar { get; }
    public GridLength FreeStar { get; }
}
