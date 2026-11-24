using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Wipriss.Core.Modules;

namespace Wipriss.Modules.ClipboardHistory;

public partial class ClipboardPanel : UserControl
{
    private const string TrashGlyph = "";
    private const string ConfirmGlyph = "";

    private readonly ClipboardModule _module;
    private readonly IPanelContext _panel;
    private readonly Dictionary<Guid, ClipView> _views = [];
    private string _filter = "all";

    internal ClipboardPanel(ClipboardModule module, IPanelContext panel)
    {
        InitializeComponent();
        _module = module;
        _panel = panel;

        Loaded += (_, _) => { _module.Changed += Refresh; Refresh(); };
        Unloaded += (_, _) => { _module.Changed -= Refresh; _views.Clear(); };
    }

    private void Refresh()
    {
        var shown = _module.Items
            .Where(e => _filter == "all" || (_filter == "text" ? e.Kind == ClipKind.Text : e.Kind == ClipKind.Image))
            .Select(ViewFor)
            .ToList();
        List.ItemsSource = shown;

        EmptyText.Text = _module.Paused ? "История на паузе.\nНовые копирования не сохраняются."
            : _module.Items.Count == 0 ? "Пока пусто.\nСкопируйте текст или картинку."
            : "Здесь ничего нет для этого фильтра.";
        EmptyText.Visibility = shown.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        PauseButton.Content = _module.Paused ? "" : "";
        PauseButton.ToolTip = _module.Paused ? "Продолжить запись истории" : "Пауза: не сохранять новые копирования";
    }

    private ClipView ViewFor(ClipEntry entry)
    {
        if (!_views.TryGetValue(entry.Id, out var view))
        {
            view = new ClipView(entry);
            _views[entry.Id] = view;
            if (entry.Kind == ClipKind.Image) LoadThumb(view);
        }

        view.UpdateMeta();
        return view;
    }

    private void LoadThumb(ClipView view)
    {
        Task.Run<BitmapImage?>(() =>
        {
            var bytes = _module.ReadContent(view.Entry);
            if (bytes is null) return null;
            var image = new BitmapImage();
            image.BeginInit();
            image.StreamSource = new MemoryStream(bytes);
            image.DecodePixelWidth = Math.Min(view.Entry.Width > 0 ? view.Entry.Width : 640, 640); // small copy for the list
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.EndInit();
            image.Freeze();
            return image;
        }).ContinueWith(task =>
        {
            if (task.IsCompletedSuccessfully && task.Result is not null) view.Thumb = task.Result;
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void Filter_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Tag: string tag }) return;
        _filter = tag;
        if (IsLoaded) Refresh();
    }

    private void Item_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ClipView view }) return;
        if (_module.CopyBack(view.Entry)) _panel.Close(); // focus goes back to your app, ready for Ctrl+V
        else Hint.Text = "Не получилось скопировать: буфер занят другой программой. Попробуйте ещё раз.";
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ClipView view }) _module.Delete(view.Entry);
    }

    private void Pause_Click(object sender, RoutedEventArgs e) => _module.Paused = !_module.Paused;

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        if (_module.Items.Count == 0) return;
        if ((string)ClearButton.Content == TrashGlyph)
        {
            ClearButton.Content = ConfirmGlyph;
            ClearButton.Foreground = (Brush)FindResource("DangerBrush");
            ClearButton.ToolTip = "Нажмите ещё раз, чтобы очистить всю историю";
            return;
        }

        _module.Clear();
        Clear_MouseLeave(ClearButton, null);
    }

    private void Clear_MouseLeave(object sender, MouseEventArgs? e)
    {
        ClearButton.Content = TrashGlyph;
        ClearButton.ClearValue(ForegroundProperty);
        ClearButton.ToolTip = "Очистить всё";
    }
}

/// <summary>One row of the history list.</summary>
public sealed class ClipView(ClipEntry entry) : INotifyPropertyChanged
{
    private ImageSource? _thumb;
    private string _meta = "";

    public ClipEntry Entry { get; } = entry;

    public string Preview => Entry.Preview;

    public Visibility TextVisibility => Entry.Kind == ClipKind.Text ? Visibility.Visible : Visibility.Collapsed;

    public Visibility ImageVisibility => Entry.Kind == ClipKind.Image ? Visibility.Visible : Visibility.Collapsed;

    public ImageSource? Thumb
    {
        get => _thumb;
        set { _thumb = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Thumb))); }
    }

    public string Meta
    {
        get => _meta;
        private set { _meta = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Meta))); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public void UpdateMeta()
    {
        var when = Entry.Copied.Date == DateTime.Today ? Entry.Copied.ToString("HH:mm")
            : Entry.Copied.Date == DateTime.Today.AddDays(-1) ? "вчера, " + Entry.Copied.ToString("HH:mm")
            : Entry.Copied.ToString("dd.MM, HH:mm");
        Meta = Entry.Kind == ClipKind.Image ? $"{when} · картинка {Entry.Preview}" : when;
    }
}
