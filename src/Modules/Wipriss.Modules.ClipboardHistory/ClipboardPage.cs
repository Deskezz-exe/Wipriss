using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using Wipriss.Core.Ui;

namespace Wipriss.Modules.ClipboardHistory;

/// <summary>
/// The clipboard inside the open island: one line per copy, pinned ones first with a blue mark. A click puts the
/// entry back on the clipboard without moving it; the pin keeps it; "Очистить" removes everything not pinned
/// (a second click confirms). Rows are rebuilt only when the history changes.
/// </summary>
internal sealed partial class ClipboardPage : Border
{
    private const double RowHeight = 44, RowGap = 6;

    private readonly ClipboardModule _module;
    private readonly StackPanel _rows = new();
    private readonly TextBlock _empty;
    private readonly TextBlock _clearText;
    private readonly Border _clear;
    private readonly TextBlock _pauseGlyph;
    private readonly Border _pause;
    private readonly Dictionary<Guid, ImageSource> _thumbs = [];
    private bool _confirmClear;

    public ClipboardPage(ClipboardModule module)
    {
        _module = module;
        Background = HomeLook.CardFill;
        BorderBrush = HomeLook.CardEdge;
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(HomeLook.CardRadius);
        Padding = new Thickness(16, 12, 12, 12);

        var root = new DockPanel();
        Child = root;

        // Head: the title on the left, pause and "Очистить" on the right.
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        DockPanel.SetDock(head, Dock.Top);
        root.Children.Add(head);

        _clearText = HomeLook.Text("Очистить", 12.5, PillLook.Subtle);
        _clearText.HorizontalAlignment = HorizontalAlignment.Center;
        _clear = HomeLook.Button(_clearText, 92, 28, OnClear, fill: HomeLook.Raised, radius: 10);
        _clear.MouseLeave += (_, _) => ResetClear();
        DockPanel.SetDock(_clear, Dock.Right);
        head.Children.Add(_clear);

        _pauseGlyph = HomeLook.Glyph("", 12, PillLook.Subtle);
        _pause = HomeLook.Button(_pauseGlyph, 28, 28, () => _module.Paused = !_module.Paused, radius: 10);
        _pause.Margin = new Thickness(0, 0, 6, 0);
        DockPanel.SetDock(_pause, Dock.Right);
        head.Children.Add(_pause);

        var icon = HomeLook.Glyph("", 13, PillLook.Blue);
        icon.Margin = new Thickness(0, 1, 9, 0);
        DockPanel.SetDock(icon, Dock.Left);
        head.Children.Add(icon);
        head.Children.Add(HomeLook.Text("Буфер обмена", 13.5, bold: true));

        // The list: always the same height, so the island keeps its size; more entries scroll inside.
        int visible = SystemParameters.WorkArea.Height >= 860 ? 6 : 4;
        var list = new Grid { Height = visible * RowHeight + (visible - 1) * RowGap };
        list.Children.Add(new ScrollViewer
        {
            Content = _rows,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Focusable = false,
        });
        _empty = HomeLook.Text("", 13, PillLook.Subtle);
        _empty.HorizontalAlignment = HorizontalAlignment.Center;
        _empty.TextAlignment = TextAlignment.Center;
        _empty.TextWrapping = TextWrapping.Wrap;
        list.Children.Add(_empty);
        root.Children.Add(list);

        Loaded += (_, _) => { _module.Changed += Fill; Fill(); };
        Unloaded += (_, _) => { _module.Changed -= Fill; _thumbs.Clear(); };
    }

    private void Fill()
    {
        _rows.Children.Clear();
        var items = _module.Ordered;
        for (int i = 0; i < items.Count; i++)
        {
            var row = Row(items[i]);
            if (i > 0) row.Margin = new Thickness(0, RowGap, 4, 0);
            _rows.Children.Add(row);
        }

        _empty.Text = _module.Paused ? "История на паузе.\nНовые копирования не сохраняются."
            : "Пока пусто.\nСкопируйте текст или картинку.";
        _empty.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        _pauseGlyph.Text = _module.Paused ? "" : "";
        _pauseGlyph.Foreground = _module.Paused ? PillLook.Amber : PillLook.Subtle;
        _pause.ToolTip = _module.Paused ? "Продолжить запись истории" : "Пауза: не сохранять новые копирования";

        bool any = items.Any(e => !e.Pinned);
        _clear.Opacity = any ? 1 : 0.4;
        _clear.ToolTip = any ? "Убрать всё, кроме закреплённых" : null;
        ResetClear();
    }

    /// <summary>One entry: the text (one line) or a small picture, the pin and the cross on the right.</summary>
    private Border Row(ClipEntry entry)
    {
        var row = new Border
        {
            Height = RowHeight,
            CornerRadius = new CornerRadius(12),
            Background = HomeLook.Raised,
            Cursor = Cursors.Hand,
            Margin = new Thickness(0, 0, 4, 0),
        };
        var grid = new Grid();
        row.Child = grid;
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        if (entry.Pinned)
            grid.Children.Add(new Border
            {
                Width = 3,
                Margin = new Thickness(0, 9, 0, 9),
                CornerRadius = new CornerRadius(1.5),
                Background = PillLook.Blue,
                HorizontalAlignment = HorizontalAlignment.Left,
            });

        var content = new DockPanel { Margin = new Thickness(14, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        grid.Children.Add(content);
        if (entry.Kind == ClipKind.Image)
        {
            var thumb = new Border
            {
                Width = 52,
                Height = 32,
                CornerRadius = new CornerRadius(6),
                Background = HomeLook.CardFill,
                Margin = new Thickness(0, 0, 12, 0),
            };
            DockPanel.SetDock(thumb, Dock.Left);
            content.Children.Add(thumb);
            ShowThumb(entry, thumb);
            content.Children.Add(HomeLook.Text($"Картинка · {entry.Preview}", 13, PillLook.Subtle));
        }
        else
        {
            content.Children.Add(HomeLook.Text(OneLine(entry.Preview), 13.5));
        }

        // Right: "Скопировано" for a moment after a click, the pin, the cross. Pin and cross show under the mouse;
        // a pinned entry always shows its pin.
        var tools = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(tools, 1);
        grid.Children.Add(tools);

        var done = new StackPanel { Orientation = Orientation.Horizontal, Opacity = 0, Margin = new Thickness(0, 0, 8, 0), IsHitTestVisible = false };
        var check = HomeLook.Glyph("", 11, PillLook.Green);
        check.Margin = new Thickness(0, 1, 6, 0);
        done.Children.Add(check);
        done.Children.Add(HomeLook.Text("Скопировано", 12, PillLook.Green));
        tools.Children.Add(done);

        var pinGlyph = HomeLook.Glyph("", 12, entry.Pinned ? PillLook.Blue : PillLook.Subtle);
        var pin = HomeLook.Button(pinGlyph, 28, 28, () => _module.SetPinned(entry, !entry.Pinned),
            entry.Pinned ? "Открепить" : "Закрепить: останется в списке и не сотрётся", radius: 9);
        pin.Opacity = entry.Pinned ? 1 : 0;
        tools.Children.Add(pin);

        var remove = HomeLook.Button(HomeLook.Glyph("", 10, PillLook.Subtle), 28, 28, () => _module.Delete(entry), "Убрать из списка", radius: 9);
        remove.Opacity = 0;
        tools.Children.Add(remove);

        row.ToolTip = entry.Kind == ClipKind.Text ? Tip(entry) : null;
        row.MouseEnter += (_, _) =>
        {
            row.Background = HomeLook.Hover;
            pin.Opacity = 1;
            remove.Opacity = 1;
        };
        row.MouseLeave += (_, _) =>
        {
            row.Background = HomeLook.Raised;
            pin.Opacity = entry.Pinned ? 1 : 0;
            remove.Opacity = 0;
        };
        row.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            bool ok = _module.CopyBack(entry);
            Flash(done, ok);
        };
        return row;
    }

    /// <summary>"Скопировано" fades in, stays a second and fades out (opacity only).</summary>
    private static void Flash(StackPanel done, bool ok)
    {
        if (done.Children[1] is TextBlock text) text.Text = ok ? "Скопировано" : "Буфер занят, ещё раз";
        foreach (var child in done.Children.OfType<TextBlock>()) child.Foreground = ok ? PillLook.Green : PillLook.Amber;
        var fade = new DoubleAnimationUsingKeyFrames();
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(120))));
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1200))));
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1450))));
        done.BeginAnimation(OpacityProperty, fade);
    }

    private static ToolTip Tip(ClipEntry entry) => new()
    {
        Content = new TextBlock { Text = entry.Preview, TextWrapping = TextWrapping.Wrap, MaxWidth = 460 },
    };

    private static string OneLine(string text) => Spaces().Replace(text, " ").Trim();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    private void ShowThumb(ClipEntry entry, Border frame)
    {
        if (_thumbs.TryGetValue(entry.Id, out var cached))
        {
            frame.Background = Fill(cached);
            return;
        }

        Task.Run<BitmapImage?>(() =>
        {
            var bytes = _module.ReadContent(entry);
            if (bytes is null) return null;
            var image = new BitmapImage();
            image.BeginInit();
            image.StreamSource = new MemoryStream(bytes);
            image.DecodePixelWidth = 120; // a tiny copy for the row
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.EndInit();
            image.Freeze();
            return image;
        }).ContinueWith(task =>
        {
            if (!task.IsCompletedSuccessfully || task.Result is null) return;
            _thumbs[entry.Id] = task.Result;
            frame.Background = Fill(task.Result);
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private static ImageBrush Fill(ImageSource image) => new(image) { Stretch = Stretch.UniformToFill };

    private void OnClear()
    {
        if (!_module.Items.Any(e => !e.Pinned)) return;
        if (!_confirmClear)
        {
            _confirmClear = true;
            _clearText.Text = "Точно?";
            _clearText.Foreground = PillLook.Red;
            return;
        }
        _module.Clear();
    }

    private void ResetClear()
    {
        _confirmClear = false;
        _clearText.Text = "Очистить";
        _clearText.Foreground = PillLook.Subtle;
    }
}
