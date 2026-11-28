using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Wipriss.Core.Ui;

namespace Wipriss.Modules.Media;

/// <summary>
/// The home-screen card. One source: a big card with the cover, title, artist, a progress bar with times and
/// previous / play-pause / next, on a background tinted by the cover. Two sources (a video and music): the card splits
/// into two short ones, top and bottom. The progress moves once a second, and only while the card is on screen.
/// </summary>
internal sealed class MediaCard : Grid
{
    private static readonly Brush Soft = Frozen(new SolidColorBrush(Color.FromArgb(0xB8, 0xFF, 0xFF, 0xFF)));
    private static readonly Brush Faint = Frozen(new SolidColorBrush(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF)));
    private static readonly Brush White = Frozen(new SolidColorBrush(Colors.White));
    private static readonly Brush WhiteHover = Frozen(new SolidColorBrush(Color.FromRgb(0xE6, 0xE6, 0xE8)));
    private static readonly Brush GlassHover = Frozen(new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF)));

    private readonly MediaModule _module;
    private readonly List<(NowPlaying Item, ScaleTransform Bar, TextBlock? Elapsed, TextBlock? Total)> _progress = [];
    private readonly DispatcherTimer _tick = new(DispatcherPriority.Render) { Interval = TimeSpan.FromSeconds(1) };

    public MediaCard(MediaModule module)
    {
        _module = module;
        var items = module.Items;
        if (items.Count == 0) return;

        if (items.Count == 1)
        {
            Children.Add(Big(items[0]));
        }
        else
        {
            RowDefinitions.Add(new RowDefinition());
            RowDefinitions.Add(new RowDefinition { Height = new GridLength(8) });
            RowDefinitions.Add(new RowDefinition());
            var top = Small(items[0]);
            var bottom = Small(items[1]);
            SetRow(bottom, 2);
            Children.Add(top);
            Children.Add(bottom);
            // One card becomes two: the halves slide out of the middle.
            if (module.ShownCount == 1)
            {
                HomeLook.Enter(top, fromY: 26);
                HomeLook.Enter(bottom, fromY: -26, delayMs: 40);
            }
        }
        module.ShownCount = items.Count;

        _tick.Tick += (_, _) => UpdateProgress();
        Loaded += (_, _) =>
        {
            _module.WatchTimeline(true);
            UpdateProgress();
            if (_progress.Any(p => p.Item.Playing && p.Item.Length > TimeSpan.Zero)) _tick.Start();
        };
        Unloaded += (_, _) =>
        {
            _tick.Stop();
            _module.WatchTimeline(false);
        };
    }

    // ---- One source: the big card ----

    private Border Big(NowPlaying now)
    {
        var card = Frame(now, new Thickness(18, 18, 18, 14));
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        card.Child = grid;

        var head = new DockPanel();
        var cover = Cover(now, 76, 14);
        cover.Margin = new Thickness(0, 0, 14, 0);
        DockPanel.SetDock(cover, Dock.Left);
        head.Children.Add(cover);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var title = HomeLook.Text(now.Title, 16, White, bold: true);
        title.TextWrapping = TextWrapping.Wrap;
        title.MaxHeight = 44;
        title.LineHeight = 21;
        text.Children.Add(title);
        if (now.Artist.Length > 0)
        {
            var artist = HomeLook.Text(now.Artist, 13, Soft);
            artist.Margin = new Thickness(0, 3, 0, 0);
            text.Children.Add(artist);
        }
        text.Children.Add(Source(now, new Thickness(0, 6, 0, 0)));
        head.Children.Add(text);
        grid.Children.Add(head);

        // Progress with times; hidden when the program does not say how long it is.
        var progress = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        Grid.SetRow(progress, 2);
        var bar = HomeLook.Bar(4, White, out var scale);
        progress.Children.Add(bar);
        var times = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
        var total = Time();
        DockPanel.SetDock(total, Dock.Right);
        times.Children.Add(total);
        var elapsed = Time();
        times.Children.Add(elapsed);
        progress.Children.Add(times);
        progress.Visibility = now.Length > TimeSpan.Zero ? Visibility.Visible : Visibility.Hidden;
        grid.Children.Add(progress);
        _progress.Add((now, scale, elapsed, total));

        var controls = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 8, 0, 0) };
        Grid.SetRow(controls, 3);
        controls.Children.Add(Side("", "Назад", now.CanPrevious, () => _module.Previous(now.Key), 40, 15));
        var play = Play(now, 50, 18);
        play.Margin = new Thickness(18, 0, 18, 0);
        controls.Children.Add(play);
        controls.Children.Add(Side("", "Дальше", now.CanNext, () => _module.Next(now.Key), 40, 15));
        grid.Children.Add(controls);
        return card;
    }

    // ---- Two sources: two short cards ----

    private Border Small(NowPlaying now)
    {
        var card = Frame(now, new Thickness(12, 10, 12, 10));
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        card.Child = grid;

        var row = new DockPanel { VerticalAlignment = VerticalAlignment.Center };
        var cover = Cover(now, 50, 11);
        cover.Margin = new Thickness(0, 0, 12, 0);
        DockPanel.SetDock(cover, Dock.Left);
        row.Children.Add(cover);

        var controls = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        controls.Children.Add(Side("", "Назад", now.CanPrevious, () => _module.Previous(now.Key), 30, 12));
        var play = Play(now, 36, 13);
        play.Margin = new Thickness(4, 0, 4, 0);
        controls.Children.Add(play);
        controls.Children.Add(Side("", "Дальше", now.CanNext, () => _module.Next(now.Key), 30, 12));
        DockPanel.SetDock(controls, Dock.Right);
        row.Children.Add(controls);

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(HomeLook.Text(now.Title, 13.5, White, bold: true));
        string under = now.Artist.Length > 0 && now.App.Length > 0 ? $"{now.Artist} · {now.App}" : now.Artist + now.App;
        if (under.Length > 0)
        {
            var sub = HomeLook.Text(under, 12, Soft);
            sub.Margin = new Thickness(0, 2, 0, 0);
            text.Children.Add(sub);
        }
        row.Children.Add(text);
        grid.Children.Add(row);

        var bar = HomeLook.Bar(3, White, out var scale);
        bar.Margin = new Thickness(0, 8, 0, 0);
        bar.Visibility = now.Length > TimeSpan.Zero ? Visibility.Visible : Visibility.Hidden;
        Grid.SetRow(bar, 1);
        grid.Children.Add(bar);
        _progress.Add((now, scale, null, null));
        return card;
    }

    // ---- Parts ----

    /// <summary>The card's frame: a gradient from the cover's colour into the island's graphite.</summary>
    private static Border Frame(NowPlaying now, Thickness padding)
    {
        var tint = now.Tint ?? Color.FromRgb(0x2A, 0x2A, 0x30);
        var deep = Mix(tint, Color.FromRgb(0x12, 0x12, 0x14), 0.72);
        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 1),
            GradientStops =
            {
                new GradientStop(Mix(tint, Color.FromRgb(0x12, 0x12, 0x14), 0.25), 0),
                new GradientStop(Mix(tint, Color.FromRgb(0x12, 0x12, 0x14), 0.5), 0.55),
                new GradientStop(deep, 1),
            },
        };
        brush.Freeze();
        return new Border
        {
            Background = brush,
            BorderBrush = Frozen(new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF))),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(HomeLook.CardRadius),
            Padding = padding,
            ClipToBounds = true,
        };
    }

    private static FrameworkElement Cover(NowPlaying now, double size, double radius)
    {
        var box = new Border
        {
            Width = size, Height = size, CornerRadius = new CornerRadius(radius),
            Background = Frozen(new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF))),
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (now.Cover is { } image)
            box.Child = new Rectangle { RadiusX = radius, RadiusY = radius, Fill = new ImageBrush(image) { Stretch = Stretch.UniformToFill } };
        else
            box.Child = new Viewbox { Width = size * 0.4, Height = size * 0.4, Child = new Path { Data = MediaModule.Note, Fill = Soft, Width = 24, Height = 24 } };
        return box;
    }

    private static FrameworkElement Source(NowPlaying now, Thickness margin)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = margin };
        var dot = new Ellipse { Width = 6, Height = 6, Fill = now.Playing ? PillLook.Green : Faint, Margin = new Thickness(0, 1, 6, 0), VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(dot);
        string state = now.Playing ? "Играет" : "На паузе";
        row.Children.Add(HomeLook.Text(now.App.Length > 0 ? $"{state} · {now.App}" : state, 11.5, Faint));
        return row;
    }

    private Border Play(NowPlaying now, double size, double glyph) =>
        HomeLook.Button(HomeLook.Glyph(now.Playing ? "" : "", glyph, Brushes.Black), size, size,
            () => _module.PlayPause(now.Key), now.Playing ? "Пауза" : "Играть", fill: White, hover: WhiteHover);

    private static Border Side(string glyph, string tip, bool enabled, Action click, double size, double glyphSize) =>
        HomeLook.Button(HomeLook.Glyph(glyph, glyphSize, White), size, size, click, tip, hover: GlassHover, enabled: enabled);

    private static TextBlock Time()
    {
        var t = HomeLook.Text("0:00", 11.5, Soft);
        Typography.SetNumeralAlignment(t, FontNumeralAlignment.Tabular);
        return t;
    }

    private void UpdateProgress()
    {
        var now = DateTime.Now;
        foreach (var (item, bar, elapsed, total) in _progress)
        {
            if (item.Length <= TimeSpan.Zero) continue;
            var p = item.PositionNow(now);
            bar.ScaleX = p.TotalSeconds / item.Length.TotalSeconds;
            if (elapsed is not null) elapsed.Text = Format(p);
            if (total is not null) total.Text = Format(item.Length);
        }
    }

    internal static string Format(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{(int)t.TotalMinutes}:{t.Seconds:00}";

    private static Color Mix(Color a, Color b, double toB) => Color.FromRgb(
        (byte)(a.R + (b.R - a.R) * toB), (byte)(a.G + (b.G - a.G) * toB), (byte)(a.B + (b.B - a.B) * toB));

    private static T Frozen<T>(T f) where T : Freezable
    {
        f.Freeze();
        return f;
    }
}
