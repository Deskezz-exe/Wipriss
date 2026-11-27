using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Wipriss.Cleaner;

namespace Wipriss.Modules.Cleaner;

/// <summary>One installed program in the list.</summary>
public sealed class ProgramRow(AppEntry app) : Row
{
    public AppEntry App { get; } = app;
    public string Name => App.Name;

    public string Details
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(App.Publisher)) parts.Add(App.Publisher!);
            if (!string.IsNullOrWhiteSpace(App.Version)) parts.Add(App.Version!);
            if (App.Installed is { } date) parts.Add($"установлена {date:dd.MM.yyyy}");
            if (App.Store) parts.Add("Microsoft Store");
            if (!App.HasUninstaller) parts.Add("нет деинсталлятора");
            return string.Join(" · ", parts);
        }
    }

    public string SizeText => App.Size > 0 ? Sizes.Format(App.Size) : "";

    private ImageSource? _icon;
    public ImageSource? Icon { get => _icon; set { _icon = value; Notify(); Notify(nameof(PlaceholderVisibility)); } }
    public Visibility PlaceholderVisibility => _icon is null ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>The icon from its file (safe on a background thread; the image is frozen).</summary>
    internal static ImageSource? LoadIcon(AppEntry app)
    {
        if (app.IconPath is not { } path || !File.Exists(path)) return null;
        try
        {
            if (path.EndsWith(".png", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase))
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.UriSource = new Uri(path);
                image.DecodePixelWidth = 32;
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.EndInit();
                image.Freeze();
                return image;
            }
            if (app.IconIndex != 0 && ExtractIconEx(path, app.IconIndex, out var large, IntPtr.Zero, 1) > 0 && large != IntPtr.Zero)
            {
                try
                {
                    var image = Imaging.CreateBitmapSourceFromHIcon(large, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    image.Freeze();
                    return image;
                }
                finally { DestroyIcon(large); }
            }
            return Wipriss.Core.Ui.FileIcons.For(path);
        }
        catch (Exception) { return null; } // a broken image is just no icon
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int ExtractIconEx(string file, int index, out IntPtr large, IntPtr small, int count);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);
}

/// <summary>«На дисках» / «В реестре и системе» above the tails.</summary>
public sealed class TailHeaderRow(string title, long size) : Row
{
    public string Title { get; } = title;
    public string SizeText { get; } = size > 0 ? Sizes.Format(size) : "";
}

/// <summary>One tail (a folder opens into its contents, each with its own tick).</summary>
public sealed class TailRow : Row
{
    public TailRow(TailItem item, TailRow? parent)
    {
        Item = item;
        Parent = parent;
        Children = item.Children.Select(c => new TailRow(c, this)).ToList();
    }

    public TailItem Item { get; }
    public TailRow? Parent { get; }
    public IReadOnlyList<TailRow> Children { get; }

    public string Title => Item.Title;
    public string Evidence => string.Join(" · ", Item.Evidence) + (Item.NeedsAdmin ? " · нужны права администратора" : "");
    public string SizeText => Item.Kind is TailKind.Folder or TailKind.File ? Sizes.Format(Item.Size) : "";
    public Brush Brush => ColorBrush(Item.Color);
    public Thickness Indent => new(Parent is null ? 0 : 30, 0, 0, Parent is null ? 4 : 2);
    public string Glyph => Item.Kind switch
    {
        TailKind.Folder => "",
        TailKind.File => "",
        TailKind.Service => "",
        TailKind.Task => "",
        _ => "", // a registry record
    };

    public Visibility ChevronVisibility => Children.Count > 0 ? Visibility.Visible : Visibility.Hidden;
    private bool _expanded;
    public bool Expanded { get => _expanded; set { _expanded = value; Notify(); Notify(nameof(Chevron)); } }
    public string Chevron => Expanded ? "" : "";

    private bool _checked;

    /// <summary>A folder with contents: true when all of it is chosen, null when a part.</summary>
    public bool? IsChecked
    {
        get
        {
            if (Children.Count == 0) return _checked;
            if (Children.All(c => c._checked)) return true;
            return Children.Any(c => c._checked) ? null : false;
        }
    }

    internal event Action? SelectionChanged;

    public void Toggle() => Set(IsChecked != true);

    internal void Set(bool value)
    {
        if (Children.Count == 0) _checked = value;
        else foreach (var child in Children) child._checked = value;
        Changed();
    }

    private void Changed()
    {
        Notify(nameof(IsChecked));
        foreach (var child in Children) child.Notify(nameof(IsChecked));
        Parent?.Notify(nameof(IsChecked));
        (Parent ?? this).SelectionChanged?.Invoke();
    }

    internal void ToggleChild()
    {
        _checked = !_checked;
        Changed();
    }

    /// <summary>The ids to delete: the whole folder when all of it is chosen, otherwise the chosen parts.</summary>
    internal IEnumerable<string> ChosenIds()
    {
        if (IsChecked == true) return [Item.Id];
        return Children.Where(c => c._checked).Select(c => c.Item.Id);
    }

    internal long ChosenSize => IsChecked == true ? Item.Size : Children.Where(c => c._checked).Sum(c => c.Item.Size);
    internal bool AnyChosen => IsChecked != false;
    internal bool ChosenNeedsAdmin => AnyChosen && Item.NeedsAdmin;

    /// <summary>Selecting by colour: green only, or everything.</summary>
    internal void SelectBy(Func<TailItem, bool> pick)
    {
        if (Children.Count == 0) _checked = pick(Item);
        else foreach (var child in Children) child._checked = pick(child.Item);
        Notify(nameof(IsChecked));
        foreach (var child in Children) child.Notify(nameof(IsChecked));
    }
}

/// <summary>A registry copy in the «вернуть» list.</summary>
public sealed class BackupRow(string folder, string program, DateTime when)
{
    public string Folder { get; } = folder;
    public string Program { get; } = program;
    public string WhenText { get; } = $"удалено {when:dd.MM.yyyy HH:mm}";
}
