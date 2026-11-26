using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using Wipriss.Converter;
using Wipriss.Core.Modules;

namespace Wipriss.Modules.Converter;

public partial class ConverterPanel : UserControl
{
    private readonly ConverterModule _module;
    private readonly IPanelContext _panel;
    private readonly ConverterSession _session;
    private readonly bool _ready;

    internal ConverterPanel(ConverterModule module, IPanelContext panel)
    {
        InitializeComponent();
        _module = module;
        _panel = panel;
        _session = module.Session;

        Check(FormatGroup, _session.Format.ToString());
        Check(ResizeGroup, _session.Resize.ToString());
        WidthBox.Text = _session.Width?.ToString(CultureInfo.InvariantCulture) ?? "";
        HeightBox.Text = _session.Height?.ToString(CultureInfo.InvariantCulture) ?? "";
        PercentInput.Text = _session.Percent.ToString(CultureInfo.InvariantCulture);
        LimitBox.Text = _session.LimitMb.ToString(CultureInfo.CurrentCulture);
        LimitToggle.IsChecked = _session.LimitWeight;
        _ready = true;
        UpdateResizeBoxes();
        LimitBox.IsEnabled = _session.LimitWeight;

        List.ItemsSource = _session.Items;
        _session.Items.CollectionChanged += Items_CollectionChanged;
        _session.RunningChanged += UpdateButtons;
        Unloaded += (_, _) =>
        {
            _session.Items.CollectionChanged -= Items_CollectionChanged;
            _session.RunningChanged -= UpdateButtons;
        };
        UpdateEmpty();
        UpdateButtons();
        // Opened again while a batch is still running in the background.
        if (_session.IsRunning) Status.Text = "Конвертирую…";
    }

    private static void Check(Panel group, string tag)
    {
        foreach (var radio in group.Children.OfType<RadioButton>())
            if ((string)radio.Tag == tag) radio.IsChecked = true;
    }

    private void Items_CollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) => UpdateEmpty();

    private void UpdateEmpty() => EmptyText.Visibility = _session.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void UpdateButtons()
    {
        bool running = _session.IsRunning;
        RunButton.Content = running ? "Остановить" : "Конвертировать";
        ClearButton.IsEnabled = !running;
    }

    private void UpdateResizeBoxes()
    {
        PixelsBox.Visibility = _session.Resize == ResizeKind.Pixels ? Visibility.Visible : Visibility.Collapsed;
        PercentBox.Visibility = _session.Resize == ResizeKind.Percent ? Visibility.Visible : Visibility.Collapsed;
        ResizeHint.Visibility = PixelsBox.Visibility;
    }

    private void Format_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string tag } && Enum.TryParse<TargetFormat>(tag, out var format))
            _session.Format = format;
    }

    private void Resize_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Tag: string tag } || !Enum.TryParse<ResizeKind>(tag, out var mode)) return;
        _session.Resize = mode;
        if (_ready) UpdateResizeBoxes();
    }

    private void Limit_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        _session.LimitWeight = LimitToggle.IsChecked == true;
        LimitBox.IsEnabled = _session.LimitWeight;
    }

    private void Number_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_ready) return;
        _session.Width = PositiveInt(WidthBox.Text);
        _session.Height = PositiveInt(HeightBox.Text);
        if (PositiveInt(PercentInput.Text) is { } percent) _session.Percent = percent;
        if (ParseMb(LimitBox.Text) is { } mb) _session.LimitMb = mb;
    }

    private static int? PositiveInt(string text) =>
        int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int n) && n > 0 ? n : null;

    private static double? ParseMb(string text) =>
        double.TryParse(text.Trim().Replace(',', '.'), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double mb)
        && mb > 0 ? mb : null;

    private void Digits_PreviewTextInput(object sender, TextCompositionEventArgs e) => e.Handled = !e.Text.All(char.IsAsciiDigit);

    private void Decimal_PreviewTextInput(object sender, TextCompositionEventArgs e) =>
        e.Handled = !e.Text.All(c => char.IsAsciiDigit(c) || c is ',' or '.');

    /// <summary>Checks the numbers before a run. Null: all good.</summary>
    private string? Problem()
    {
        if (_session.Resize == ResizeKind.Pixels)
        {
            if (_session.Width is null && _session.Height is null) return "Укажите ширину или высоту.";
            if (_session.Width > 32_000 || _session.Height > 32_000) return "Больше 32 000 пикселей нельзя.";
        }
        if (_session.Resize == ResizeKind.Percent)
        {
            if (PositiveInt(PercentInput.Text) is not { } p || p > 1000) return "Проценты: от 1 до 1000.";
        }
        if (_session.LimitWeight)
        {
            if (ParseMb(LimitBox.Text) is not { } mb || mb < 0.01 || mb > 1000) return "Вес: от 0,01 до 1000 МБ.";
        }
        return null;
    }

    private async void Run_Click(object sender, RoutedEventArgs e)
    {
        if (_session.IsRunning)
        {
            _session.Cancel();
            return;
        }
        if (Problem() is { } problem)
        {
            Status.Text = problem;
            return;
        }
        Status.Text = "Конвертирую…";
        string? result = await _session.RunAsync();
        if (IsLoaded) Status.Text = result ?? "";
        _module.TrimMemory();
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        _session.Clear();
        Status.Text = "";
    }

    private void Pick_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Картинки для конвертера",
            Multiselect = true,
            Filter = "Картинки|*.jpg;*.jpeg;*.jfif;*.png;*.webp;*.avif;*.heic;*.heif;*.bmp;*.ico;*.tif;*.tiff;*.gif|Все файлы|*.*",
        };
        bool? ok;
        using (_panel.KeepOpen())
            ok = dialog.ShowDialog(Window.GetWindow(this));
        if (ok == true) _session.Add(dialog.FileNames.Where(_module.CanOpen));
    }

    private void Panel_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = DroppedFiles(e).Any(_module.CanOpen) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Panel_Drop(object sender, DragEventArgs e)
    {
        _session.Add(DroppedFiles(e).Where(_module.CanOpen));
        e.Handled = true;
    }

    private static string[] DroppedFiles(DragEventArgs e) =>
        e.Data.GetDataPresent(DataFormats.FileDrop) && e.Data.GetData(DataFormats.FileDrop) is string[] files ? files : [];

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: FileRow row }) _session.Remove(row);
    }

    private void Reveal_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: FileRow { Output: { } path } }) return;
        if (!File.Exists(path)) { Status.Text = "Файла уже нет на месте."; return; }
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
    }
}
