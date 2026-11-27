using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using Wipriss.Cleaner;

namespace Wipriss.Modules.Cleaner;

// The list is flat (so it can virtualize): a section header, then each program, then its items when the
// program is opened, and the card of an item under it when the item is opened.

public abstract class Row : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Notify([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    internal static Brush ColorBrush(CleanColor color) => (Brush)Application.Current.FindResource(color switch
    {
        CleanColor.Green => "OkBrush",
        CleanColor.Yellow => "WarnBrush",
        _ => "DangerBrush",
    });
}

public sealed class CategoryRow(CleanCategory category, IReadOnlyList<GroupRow> groups) : Row
{
    public CleanCategory Category { get; } = category;
    public IReadOnlyList<GroupRow> Groups { get; } = groups;

    public string Title => Category switch
    {
        CleanCategory.Windows => "Windows",
        CleanCategory.Browsers => "Браузеры",
        CleanCategory.Apps => "Программы",
        CleanCategory.Graphics => "Видеокарта",
        CleanCategory.Developer => "Для разработчиков",
        CleanCategory.Projects => "Ваши проекты",
        CleanCategory.Leftovers => "Остатки удалённых программ",
        CleanCategory.Models => "Модели ИИ",
        _ => "Установщики",
    };

    public string SizeText => Sizes.Format(Groups.Sum(g => g.Size));

    private bool _expanded = true;
    public bool Expanded { get => _expanded; set { _expanded = value; Notify(); Notify(nameof(Chevron)); } }
    public string Chevron => Expanded ? "" : "";
}

public sealed class GroupRow : Row
{
    public GroupRow(CleanGroup group)
    {
        Group = group;
        Items = group.Items.Select(i => new ItemRow(i, this)).ToList();
    }

    public CleanGroup Group { get; }
    public IReadOnlyList<ItemRow> Items { get; }

    public string Title => Group.Title;
    public string Status => string.IsNullOrEmpty(Group.Publisher)
        || Group.Status.Contains(Group.Publisher, StringComparison.OrdinalIgnoreCase)
        || Group.Title.Contains(Group.Publisher, StringComparison.OrdinalIgnoreCase)
        ? Group.Status : $"{Group.Publisher} · {Group.Status}";

    public long Size => Items.Where(i => i.Item.Size > 0).Sum(i => i.Item.Size);
    public string SizeText => Sizes.Format(Size);

    public bool CanSelect => Items.Any(i => i.CanSelect);

    /// <summary>True: every item that may be selected is; false: none; null: some.</summary>
    public bool? IsChecked
    {
        get
        {
            var selectable = Items.Where(i => i.CanSelect).ToList();
            if (selectable.Count == 0 || selectable.All(i => !i.IsChecked)) return false;
            return selectable.All(i => i.IsChecked) ? true : null;
        }
    }

    public void Toggle()
    {
        bool select = IsChecked != true;
        foreach (var item in Items.Where(i => i.CanSelect)) item.SetChecked(select, notifyGroup: false);
        Changed();
    }

    internal void Changed()
    {
        Notify(nameof(IsChecked));
        SelectionChanged?.Invoke();
    }

    internal event Action? SelectionChanged;

    // Up to three dots: which colours this program has.
    public Visibility HasGreen => Items.Any(i => i.Item.Color == CleanColor.Green) ? Visibility.Visible : Visibility.Collapsed;
    public Visibility HasYellow => Items.Any(i => i.Item.Color == CleanColor.Yellow) ? Visibility.Visible : Visibility.Collapsed;
    public Visibility HasRed => Items.Any(i => i.Item.Color == CleanColor.Red) ? Visibility.Visible : Visibility.Collapsed;

    private bool _expanded;
    public bool Expanded { get => _expanded; set { _expanded = value; Notify(); Notify(nameof(Chevron)); } }
    public string Chevron => Expanded ? "" : "";
}

public sealed class ItemRow : Row
{
    public ItemRow(CleanItem item, GroupRow group)
    {
        Item = item;
        Group = group;
        _checked = false; // nothing is selected until the owner chooses
        Details = new DetailRow(this);
    }

    public CleanItem Item { get; }
    public GroupRow Group { get; }
    public DetailRow Details { get; }

    public string Title => Item.Title;
    public string SizeText => Item.Size < 0 ? "нет доступа" : Sizes.Format(Item.Size);
    public Brush Brush => ColorBrush(Item.Color);
    /// <summary>Red items and what only Windows may clean are never selectable.</summary>
    public bool CanSelect => Item.Color != CleanColor.Red && Item.Action != CleanAction.WindowsOnly;
    public Visibility LockVisibility => CanSelect ? Visibility.Collapsed : Visibility.Visible;
    public Visibility BoxVisibility => CanSelect ? Visibility.Visible : Visibility.Collapsed;

    public string ColorText => Item.Color == CleanColor.Red ? "не удаляйте"
        : Item.Action == CleanAction.WindowsOnly ? "через Windows"
        : Item.Color == CleanColor.Green ? "можно удалить"
        : "возможно, важно";

    public Brush TextBrush => Item.Color != CleanColor.Red && Item.Action == CleanAction.WindowsOnly
        ? (Brush)Application.Current.FindResource("SubtleTextBrush") : Brush;

    private bool _checked;
    public bool IsChecked
    {
        get => _checked;
        set => SetChecked(value, notifyGroup: true);
    }

    internal void SetChecked(bool value, bool notifyGroup)
    {
        value &= CanSelect;
        if (_checked == value) return;
        _checked = value;
        Notify(nameof(IsChecked));
        if (notifyGroup) Group.Changed();
    }

    private bool _expanded;
    public bool Expanded { get => _expanded; set { _expanded = value; Notify(); } }
}

/// <summary>The card of one item: what it is, what happens if it is deleted, why we think so.</summary>
public sealed class DetailRow(ItemRow owner) : Row
{
    public ItemRow Owner { get; } = owner;
    private CleanItem Item => Owner.Item;

    public Brush Brush => Owner.Brush;
    public string ColorText => Item.Color == CleanColor.Red ? "Не удаляйте: может сломать программу или Windows"
        : Item.Action == CleanAction.WindowsOnly ? "Удаляется только средствами Windows"
        : Item.Color == CleanColor.Green ? "Можно удалить"
        : "Возможно, важно: посмотрите перед удалением";
    public string What => Item.What;
    public string IfDeleted => "Если удалить: " + Item.IfDeleted;
    public string? HowToClean => Item.HowToClean;
    public Visibility HideVisibility => Item.Paths.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    public Visibility HowToVisibility => string.IsNullOrEmpty(Item.HowToClean) ? Visibility.Collapsed : Visibility.Visible;
    public IReadOnlyList<string> Evidence => Item.Evidence;
    public Visibility EvidenceVisibility => Item.Evidence.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

    public string Facts
    {
        get
        {
            var parts = new List<string>();
            parts.Add(Item.Size < 0 ? "размер: нет доступа" : $"{Sizes.Format(Item.Size)}, файлов: {Item.Files:N0}");
            if (Item.LastChanged is { } changed) parts.Add($"изменялось {changed:dd.MM.yyyy}");
            if (!string.IsNullOrEmpty(Item.Confidence)) parts.Add($"уверенность: {Item.Confidence}");
            return string.Join(" · ", parts);
        }
    }

    public string PathsText
    {
        get
        {
            var shown = Item.Paths.Take(4).ToList();
            var text = string.Join("\n", shown);
            if (Item.Paths.Count > shown.Count) text += $"\n…и ещё {Item.Paths.Count - shown.Count}";
            return text;
        }
    }

    public Visibility PathsVisibility => Item.Paths.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
}

/// <summary>A section of the window: «Система», «Браузеры», «Приложения», «Прочее».</summary>
public sealed class SectionRow : Row
{
    public SectionRow(string name, string glyph, CleanCategory[] categories)
    {
        Name = name;
        Glyph = glyph;
        Categories = categories;
    }

    public string Name { get; }
    public string Glyph { get; }
    public CleanCategory[] Categories { get; }

    internal List<CategoryRow> Rows { get; set; } = [];

    private IEnumerable<ItemRow> Items => Rows.SelectMany(c => c.Groups).SelectMany(g => g.Items);

    public string SizeText => Sizes.Format(Items.Where(i => i.Item.Size > 0).Sum(i => i.Item.Size));
    public string GreenText
    {
        get
        {
            long green = Items.Where(i => i.Item.Color == CleanColor.Green && i.CanSelect && i.Item.Size > 0).Sum(i => i.Item.Size);
            return Rows.Count == 0 ? "ничего не найдено" : green > 0 ? $"можно удалить {Sizes.Format(green)}" : "можно удалить: ничего";
        }
    }

    private bool _selected;
    public bool IsSelected { get => _selected; set { _selected = value; Notify(); } }

    internal void Refresh()
    {
        Notify(nameof(SizeText));
        Notify(nameof(GreenText));
    }
}
