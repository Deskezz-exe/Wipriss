using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Wipriss.Core.Ui;

/// <summary>
/// The look of the open island's home screen, shared by the modules' cards: flat graphite cards with a hairline,
/// round buttons with Segoe Fluent glyphs, two text brightnesses. Animations touch only opacity and transforms
/// and last at most 300 ms.
/// </summary>
public static class HomeLook
{
    public const double CardRadius = 18;

    public static Brush CardFill => Res("TileBrush");
    public static Brush CardEdge => Res("DividerBrush");
    public static Brush Hover => Res("TileHoverBrush");
    public static Brush Raised => Res("SocketFillBrush");
    public static FontFamily IconFont => (FontFamily)Application.Current.FindResource("IconFont");
    public static FontFamily DisplayFont => (FontFamily)Application.Current.FindResource("DisplayFont");

    /// <summary>A card frame. <paramref name="padding"/> defaults to 16×14.</summary>
    public static Border Card(UIElement? child = null, Thickness? padding = null) => new()
    {
        Background = CardFill,
        BorderBrush = CardEdge,
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(CardRadius),
        Padding = padding ?? new Thickness(16, 14, 16, 14),
        Child = child,
    };

    public static TextBlock Text(string text, double size = 13, Brush? brush = null, bool bold = false) => new()
    {
        Text = text,
        FontSize = size,
        Foreground = brush ?? PillLook.Text,
        FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
        TextTrimming = TextTrimming.CharacterEllipsis,
        VerticalAlignment = VerticalAlignment.Center,
    };

    public static TextBlock Glyph(string glyph, double size = 14, Brush? brush = null) => new()
    {
        Text = glyph,
        FontFamily = IconFont,
        FontSize = size,
        Foreground = brush ?? PillLook.Text,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
    };

    /// <summary>
    /// A round (or rounded) button: a glyph or any content on a fill that lightens under the mouse and dips a little
    /// when pressed. Disabled buttons fade.
    /// </summary>
    public static Border Button(UIElement content, double width, double height, Action click, string? tip = null,
        Brush? fill = null, Brush? hover = null, double? radius = null, bool enabled = true)
    {
        var normal = fill ?? Brushes.Transparent;
        var over = hover ?? Hover;
        var scale = new ScaleTransform(1, 1);
        var b = new Border
        {
            Width = width,
            Height = height,
            CornerRadius = new CornerRadius(radius ?? Math.Min(width, height) / 2),
            Background = normal,
            Child = content,
            ToolTip = tip,
            Cursor = enabled ? Cursors.Hand : Cursors.Arrow,
            Opacity = enabled ? 1 : 0.35,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = scale,
            Focusable = false,
        };
        if (!enabled) return b;
        b.MouseEnter += (_, _) => b.Background = over;
        b.MouseLeave += (_, _) => { b.Background = normal; Press(scale, 1); };
        b.MouseLeftButtonDown += (_, e) => { Press(scale, 0.92); e.Handled = true; };
        b.MouseLeftButtonUp += (_, e) =>
        {
            Press(scale, 1);
            e.Handled = true;
            click();
        };
        return b;
    }

    private static void Press(ScaleTransform scale, double to)
    {
        var a = new DoubleAnimation(to, TimeSpan.FromMilliseconds(110)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, a);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, a);
    }

    /// <summary>A thin rounded bar: a track and a fill scaled by <paramref name="value"/> (0..1). Scaling, not resizing, keeps it off the layout.</summary>
    public static Grid Bar(double height, Brush fill, out ScaleTransform value)
    {
        value = new ScaleTransform(0, 1);
        var grid = new Grid { Height = height, VerticalAlignment = VerticalAlignment.Center };
        grid.Children.Add(new Border { CornerRadius = new CornerRadius(height / 2), Background = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)) });
        grid.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(height / 2),
            Background = fill,
            RenderTransformOrigin = new Point(0, 0.5),
            RenderTransform = value,
        });
        return grid;
    }

    /// <summary>Fades and slides an element in (≤ 300 ms, opacity and transform only).</summary>
    public static void Enter(UIElement element, double fromY = 8, int delayMs = 0)
    {
        var shift = new TranslateTransform(0, fromY);
        element.RenderTransform = shift;
        element.Opacity = 0;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var begin = TimeSpan.FromMilliseconds(delayMs);
        element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(220)) { BeginTime = begin, EasingFunction = ease });
        shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(260)) { BeginTime = begin, EasingFunction = ease });
    }

    private static Brush Res(string key) => (Brush)Application.Current.FindResource(key);
}
