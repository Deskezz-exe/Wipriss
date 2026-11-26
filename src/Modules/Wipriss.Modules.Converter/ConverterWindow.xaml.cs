using System.Collections.Specialized;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Wipriss.Converter;
using Wipriss.Core.Ui;
using Wipriss.Modules.Capture;

namespace Wipriss.Modules.Converter;

/// <summary>
/// "Изменить через Wipriss": previews of the pictures on the left, settings on the right. Settings are common to
/// all pictures; a picture can get its own, or be drawn on. Closing the window drops the previews and the drawn-on
/// copies (a running batch goes on in the background).
/// </summary>
public partial class ConverterWindow : Window
{
    private static readonly int[] PreviewSizes = [256, 512, 1024, 2048];

    private readonly ConverterModule _module;
    private readonly ConverterSession _session;
    private readonly CancellationTokenSource _closing = new();
    private readonly HashSet<FileRow> _loading = [];
    private PreviewGrid? _grid;
    private FileRow? _selected;
    private Window? _editor;
    private bool _ready;

    internal ConverterWindow(ConverterModule module)
    {
        InitializeComponent();
        _module = module;
        _session = module.Session;

        // A bit smaller than the screen on small laptops.
        var work = SystemParameters.WorkArea;
        Width = Math.Min(Width, work.Width * 0.92);
        Height = Math.Min(Height, work.Height * 0.92);

        Tiles.ItemsSource = _session.Items;
        _session.Items.CollectionChanged += Items_CollectionChanged;
        _session.RunningChanged += Session_RunningChanged;
        ReplaceToggle.IsChecked = _session.ReplaceOriginals;
        Viewer.PreviewMouseLeftButtonDown += Viewer_MouseDown;

        ShowSettings();
        _ready = true;
        UpdateList();
        UpdateButtons();
        UpdateReplaceHint();
        if (_session.IsRunning) Status.Text = "Конвертирую…";
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        NativeWindowStyle.DarkTitleBar(this);
        NativeWindowStyle.FrameColors(this, (Color)FindResource("HairlineColor"), (Color)FindResource("WindowColor"), (Color)FindResource("TextColor"));
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && Keyboard.FocusedElement is not TextBox)
        {
            if (_selected is not null) Select(null);
            else Close();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Delete && _selected is { } row && Keyboard.FocusedElement is not TextBox)
        {
            RemoveRow(row);
            e.Handled = true;
            return;
        }
        base.OnPreviewKeyDown(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _closing.Cancel();
        _editor?.Close();
        _session.Items.CollectionChanged -= Items_CollectionChanged;
        _session.RunningChanged -= Session_RunningChanged;
        foreach (var row in _session.Items)
        {
            row.Thumb = null;
            row.ThumbPixels = 0;
            row.IsSelected = false;
        }
        // A batch still running finishes in the background; its list stays until the window is opened again.
        if (!_session.IsRunning) _session.Clear();
        _module.WindowClosed();
        base.OnClosed(e);
    }

    /// <summary>Pictures from Explorer, the island or a drop: added and shown.</summary>
    internal void AddFiles(IEnumerable<string> paths)
    {
        int added = _session.Add(paths.Where(_module.CanOpen));
        if (added == 0 && _session.Items.Count >= 500) Status.Text = "Больше 500 картинок за раз нельзя.";
    }

    // ---- List and previews ----

    private void Items_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_selected is not null && !_session.Items.Contains(_selected)) Select(null);
        UpdateList();
        LoadPreviews();
    }

    private void UpdateList()
    {
        int count = _session.Items.Count;
        EmptyBox.Visibility = count == 0 ? Visibility.Visible : Visibility.Collapsed;
        Viewer.Visibility = count == 0 ? Visibility.Collapsed : Visibility.Visible;
        CountText.Text = count == 0 ? "Нет фото" : Core.Text.RuText.Count(count, "фото", "фото", "фото");
        RunButton.Content = _session.IsRunning ? "Остановить" : count > 1 ? $"Применить ко всем ({count})" : "Применить";
    }

    private void Grid_Loaded(object sender, RoutedEventArgs e)
    {
        if (_grid is not null) _grid.TileChanged -= LoadPreviews;
        _grid = (PreviewGrid)sender;
        _grid.TileChanged += LoadPreviews;
        LoadPreviews();
    }

    /// <summary>The preview size the tiles need now, in pixels.</summary>
    private int WantedPreview()
    {
        double side = _grid is { Tile: { Width: > 0 } tile } ? Math.Max(tile.Width, tile.Height - PreviewGrid.Label) : 300;
        double pixels = side * VisualTreeHelper.GetDpi(this).DpiScaleX;
        foreach (int size in PreviewSizes)
            if (size >= pixels) return size;
        return PreviewSizes[^1];
    }

    /// <summary>Loads missing or too blurry previews, two at a time, off the UI thread.</summary>
    private async void LoadPreviews()
    {
        if (_closing.IsCancellationRequested) return;
        int wanted = WantedPreview();
        var rows = _session.Items.Where(r => !_loading.Contains(r) && !r.NoPreview && r.ThumbPixels < wanted).ToList();
        if (rows.Count == 0) return;
        foreach (var row in rows) _loading.Add(row);

        var failed = new List<FileRow>();
        using var gate = new SemaphoreSlim(2);
        await Task.WhenAll(rows.Select(async row =>
        {
            await gate.WaitAsync();
            try
            {
                if (_closing.IsCancellationRequested) return;
                string source = row.EditedPath ?? row.Path;
                var image = await Task.Run(() => Previews.Load(source, wanted));
                if (image is null) failed.Add(row);
                else SetThumb(row, image, wanted);
            }
            finally { gate.Release(); }
        }));

        // What Windows can't read (HEIC, AVIF without codecs): one converter run for all of them.
        if (failed.Count > 0 && !_closing.IsCancellationRequested)
        {
            var made = await Previews.ViaConverterAsync(failed.Select(r => r.EditedPath ?? r.Path).ToList(), wanted, _closing.Token);
            for (int i = 0; i < failed.Count; i++)
            {
                var row = failed[i];
                if (made[i] is { } png)
                {
                    var image = await Task.Run(() => Previews.Load(png, wanted));
                    ConverterSession.TryDelete(png);
                    if (image is not null && !_closing.IsCancellationRequested) { SetThumb(row, image, wanted); continue; }
                }
                row.NoPreview = true;
            }
        }
        foreach (var row in rows) _loading.Remove(row);
    }

    private void SetThumb(FileRow row, BitmapSource image, int wanted)
    {
        if (_closing.IsCancellationRequested || !_session.Items.Contains(row)) return;
        row.Thumb = image;
        // A small picture can't get sharper: count it as loaded at any size.
        row.ThumbPixels = Math.Max(image.PixelWidth, image.PixelHeight) < wanted ? int.MaxValue : wanted;
    }

    // ---- Selection ----

    private void Tile_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: FileRow row }) return;
        e.Handled = true;
        if (e.ClickCount == 2)
        {
            Select(row);
            Draw(row);
            return;
        }
        Select(_selected == row ? null : row);
    }

    /// <summary>A click on the empty space between tiles drops the selection.</summary>
    private void Viewer_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && FindRow(source) is null && source is not System.Windows.Controls.Primitives.ScrollBar
            && FindAncestor<System.Windows.Controls.Primitives.ScrollBar>(source) is null)
            Select(null);
    }

    private static FileRow? FindRow(DependencyObject source)
    {
        for (DependencyObject? at = source; at is not null; at = at is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(at) : LogicalTreeHelper.GetParent(at))
            if (at is FrameworkElement { DataContext: FileRow row }) return row;
        return null;
    }

    private static T? FindAncestor<T>(DependencyObject source) where T : DependencyObject
    {
        for (DependencyObject? at = source; at is not null; at = at is Visual ? VisualTreeHelper.GetParent(at) : LogicalTreeHelper.GetParent(at))
            if (at is T found) return found;
        return null;
    }

    private void Select(FileRow? row)
    {
        if (_selected is not null) _selected.IsSelected = false;
        _selected = row;
        if (row is not null) row.IsSelected = true;
        ShowSettings();
    }

    /// <summary>The settings the controls show and change: the selected picture's own, or the common ones.</summary>
    private PictureSettings Current => _selected?.Own ?? _session.Common;

    private void ShowSettings()
    {
        bool was = _ready;
        _ready = false;
        var row = _selected;
        ScopeBox.Visibility = row is null ? Visibility.Collapsed : Visibility.Visible;
        if (row is not null)
        {
            SelectedName.Text = row.Name;
            ScopeAll.IsChecked = !row.HasOwn;
            ScopeOwn.IsChecked = row.HasOwn;
            UndrawButton.Visibility = row.IsEdited ? Visibility.Visible : Visibility.Collapsed;
            RevealButton.Visibility = row.CanReveal ? Visibility.Visible : Visibility.Collapsed;
            SelectedResult.Text = row.Result;
            SelectedResult.Foreground = (Brush)FindResource(row.IsFailed ? "DangerBrush" : row.IsDone ? "OkBrush" : "SubtleTextBrush");
        }
        SettingsTitle.Text = row?.Own is not null ? "Только для этого фото" : "Для всех фото";

        var s = Current;
        Check(FormatGroup, s.Format.ToString());
        Check(ResizeGroup, s.Resize.ToString());
        WidthBox.Text = s.Width?.ToString(CultureInfo.InvariantCulture) ?? "";
        HeightBox.Text = s.Height?.ToString(CultureInfo.InvariantCulture) ?? "";
        PercentInput.Text = s.Percent.ToString(CultureInfo.InvariantCulture);
        LimitBox.Text = s.LimitMb.ToString(CultureInfo.CurrentCulture);
        LimitToggle.IsChecked = s.LimitWeight;
        LimitRow.Visibility = s.LimitWeight ? Visibility.Visible : Visibility.Collapsed;
        BackgroundToggle.IsChecked = s.RemoveBackground;
        UpdateResizeBoxes();
        _ready = was;
    }

    private void Scope_Checked(object sender, RoutedEventArgs e)
    {
        if (!_ready || _selected is not { } row) return;
        row.Own = ScopeOwn.IsChecked == true ? row.Own ?? _session.Common.Clone() : null;
        Redo(row);
        ShowSettings();
    }

    /// <summary>
    /// A setting changed: a picture with its own settings shows them on its tile, and the pictures these settings
    /// apply to wait for "Применить" again.
    /// </summary>
    private void SettingsChanged()
    {
        if (_selected?.Own is not null)
        {
            _selected.OwnChanged();
            Redo(_selected);
            return;
        }
        foreach (var row in _session.Items.Where(r => r.Own is null)) Redo(row);
    }

    private static void Redo(FileRow row)
    {
        if (row.IsDone) row.SetWaiting();
    }

    private static void Check(Panel group, string tag)
    {
        foreach (var radio in group.Children.OfType<RadioButton>())
            radio.IsChecked = (string)radio.Tag == tag;
    }

    // ---- Settings ----

    private void UpdateResizeBoxes()
    {
        var resize = Current.Resize;
        PixelsBox.Visibility = resize == ResizeKind.Pixels ? Visibility.Visible : Visibility.Collapsed;
        PercentBox.Visibility = resize == ResizeKind.Percent ? Visibility.Visible : Visibility.Collapsed;
        ResizeHint.Visibility = PixelsBox.Visibility;
    }

    private static bool KeepsTransparency(TargetFormat format) => format is not (TargetFormat.Jpg or TargetFormat.Bmp);

    private void Format_Checked(object sender, RoutedEventArgs e)
    {
        if (!_ready || sender is not RadioButton { Tag: string tag } || !Enum.TryParse<TargetFormat>(tag, out var format)) return;
        var s = Current;
        s.Format = format;
        // JPG and BMP have no transparency, so a removed background would turn white again.
        if (s.RemoveBackground && !KeepsTransparency(format))
        {
            s.RemoveBackground = false;
            _ready = false;
            BackgroundToggle.IsChecked = false;
            _ready = true;
            Status.Text = "JPG и BMP не умеют прозрачность: фон не убирается.";
        }
        SettingsChanged();
    }

    private void Resize_Checked(object sender, RoutedEventArgs e)
    {
        if (!_ready || sender is not RadioButton { Tag: string tag } || !Enum.TryParse<ResizeKind>(tag, out var mode)) return;
        Current.Resize = mode;
        UpdateResizeBoxes();
        SettingsChanged();
    }

    private void Limit_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        Current.LimitWeight = LimitToggle.IsChecked == true;
        LimitRow.Visibility = Current.LimitWeight ? Visibility.Visible : Visibility.Collapsed;
        SettingsChanged();
    }

    private void Background_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        var s = Current;
        s.RemoveBackground = BackgroundToggle.IsChecked == true;
        if (s.RemoveBackground && !KeepsTransparency(s.Format))
        {
            s.Format = TargetFormat.Png;
            _ready = false;
            Check(FormatGroup, nameof(TargetFormat.Png));
            _ready = true;
            Status.Text = "Без фона нужна прозрачность: выбран PNG.";
        }
        SettingsChanged();
    }

    private void Number_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_ready) return;
        var s = Current;
        s.Width = PositiveInt(WidthBox.Text);
        s.Height = PositiveInt(HeightBox.Text);
        if (PositiveInt(PercentInput.Text) is { } percent) s.Percent = percent;
        s.LimitMb = ParseMb(LimitBox.Text) ?? 0;
        SettingsChanged();
    }

    private static int? PositiveInt(string text) =>
        int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int n) && n > 0 ? n : null;

    private static double? ParseMb(string text) =>
        double.TryParse(text.Trim().Replace(',', '.'), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double mb)
        && mb > 0 ? mb : null;

    private void Digits_PreviewTextInput(object sender, TextCompositionEventArgs e) => e.Handled = !e.Text.All(char.IsAsciiDigit);

    private void Decimal_PreviewTextInput(object sender, TextCompositionEventArgs e) =>
        e.Handled = !e.Text.All(c => char.IsAsciiDigit(c) || c is ',' or '.');

    private void Replace_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        _session.ReplaceOriginals = ReplaceToggle.IsChecked == true;
        UpdateReplaceHint();
    }

    private void UpdateReplaceHint()
    {
        bool on = _session.ReplaceOriginals;
        ReplaceHint.Text = on
            ? "Включено: оригиналы уйдут в Корзину, результат займёт их имя. Вернуть оригинал можно из Корзины."
            : "Выключено: результат сохранится рядом под новым именем, оригинал не меняется.";
        ReplaceHint.Foreground = (Brush)FindResource(on ? "WarnBrush" : "SubtleTextBrush");
    }

    // ---- Run ----

    private void Session_RunningChanged()
    {
        UpdateButtons();
        UpdateList();
    }

    private void UpdateButtons() => ClearButton.IsEnabled = !_session.IsRunning;

    private async void Run_Click(object sender, RoutedEventArgs e)
    {
        if (_session.IsRunning)
        {
            _session.Cancel();
            return;
        }
        if (_session.Problem(_session.Items) is { } problem)
        {
            Status.Text = problem;
            return;
        }
        Status.Text = "Конвертирую…";
        string? result = await _session.RunAsync();
        if (IsLoaded) Status.Text = result ?? "";
        if (_selected is not null) ShowSettings();
        _module.TrimMemory();
    }

    // ---- Adding and removing ----

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Картинки для Wipriss",
            Multiselect = true,
            Filter = "Картинки|*.jpg;*.jpeg;*.jfif;*.jpe;*.png;*.webp;*.avif;*.heic;*.heif;*.bmp;*.dib;*.ico;*.tif;*.tiff;*.gif|Все файлы|*.*",
        };
        if (dialog.ShowDialog(this) == true) AddFiles(dialog.FileNames);
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        Select(null);
        _session.Clear();
        Status.Text = "";
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: FileRow row }) RemoveRow(row);
        e.Handled = true;
    }

    private void RemoveRow(FileRow row)
    {
        if (row == _selected) Select(null);
        row.Thumb = null;
        _session.Remove(row);
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = DroppedFiles(e).Any(_module.CanOpen) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        AddFiles(DroppedFiles(e));
        e.Handled = true;
    }

    private static string[] DroppedFiles(DragEventArgs e) =>
        e.Data.GetDataPresent(DataFormats.FileDrop) && e.Data.GetData(DataFormats.FileDrop) is string[] files ? files : [];

    private void Reveal_Click(object sender, RoutedEventArgs e)
    {
        if (_selected?.Output is not { } path) return;
        if (!File.Exists(path)) { Status.Text = "Файла уже нет на месте."; return; }
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
    }

    // ---- Drawing ----

    private void Draw_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is { } row) Draw(row);
    }

    private void Undraw_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is not { } row || row.IsWorking) return;
        ConverterSession.DeleteEdited(row);
        if (row.IsDone) row.SetWaiting();
        row.ThumbPixels = 0;
        LoadPreviews();
        ShowSettings();
    }

    /// <summary>Opens the screenshot editor on the picture (with its earlier drawing, if any).</summary>
    private async void Draw(FileRow row)
    {
        if (row.IsWorking) return;
        if (_editor is { IsLoaded: true } open)
        {
            open.Activate();
            return;
        }
        Status.Text = "Открываю фото…";
        string source = row.EditedPath ?? row.Path;
        var image = await Task.Run(() => Previews.Load(source, 0));
        if (image is null)
        {
            // Windows can't read it: the converter makes a full-size PNG to draw on.
            var made = await Previews.ViaConverterAsync([source], 0, _closing.Token);
            if (made[0] is { } png)
            {
                image = await Task.Run(() => Previews.Load(png, 0));
                ConverterSession.TryDelete(png);
            }
        }
        if (_closing.IsCancellationRequested) return;
        if (image is null)
        {
            Status.Text = "Это фото не открывается для рисования.";
            return;
        }
        Status.Text = "";
        _editor = PictureEditor.Open(image, this, drawn => SaveDrawing(row, drawn));
        _editor.Closed += (_, _) =>
        {
            _editor = null;
            _module.TrimMemory();
        };
    }

    private void SaveDrawing(FileRow row, BitmapSource drawn)
    {
        if (!_session.Items.Contains(row)) return;
        try
        {
            Directory.CreateDirectory(ConverterSession.TempFolder);
            string path = Path.Combine(ConverterSession.TempFolder, Guid.NewGuid().ToString("N") + ".png");
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(drawn));
            using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
                encoder.Save(file);
            ConverterSession.DeleteEdited(row);
            row.EditedPath = path;
            if (row.IsDone || row.IsFailed) row.SetWaiting();

            int wanted = WantedPreview();
            double scale = Math.Min(1, (double)wanted / Math.Max(drawn.PixelWidth, drawn.PixelHeight));
            BitmapSource thumb = scale < 1 ? new TransformedBitmap(drawn, new ScaleTransform(scale, scale)) : drawn;
            thumb.Freeze();
            SetThumb(row, thumb, wanted);
            if (row == _selected) ShowSettings();
            Status.Text = "Рисунок сохранится в новом файле после «Применить».";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status.Text = "Не получилось сохранить рисунок во временную папку.";
        }
    }
}
