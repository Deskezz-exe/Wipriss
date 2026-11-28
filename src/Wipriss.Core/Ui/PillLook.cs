using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Wipriss.Core.Ui;

/// <summary>
/// The look shared by every block of the closed pill: two text brightnesses, four status colours, fonts and
/// line icons. Icons come from Fluent UI System Icons (Microsoft, MIT; see THIRD-PARTY-NOTICES.md), 16×16 box.
/// </summary>
public static class PillLook
{
    public const double IconSize = 14;
    public const double FontSize = 12;

    public static Brush Text { get; } = Resource("TextBrush", 0xEDEDEF);
    public static Brush Subtle { get; } = Resource("SubtleTextBrush", 0x9A9AA2);
    public static Brush Faint { get; } = Resource("FaintTextBrush", 0x5E5E66);

    /// <summary>Accent: something is going on.</summary>
    public static Brush Blue { get; } = Resource("InfoBrush", 0x6EA8F5);

    /// <summary>Good, charging.</summary>
    public static Brush Green { get; } = Resource("OkBrush", 0x7BC79A);

    /// <summary>Needs attention.</summary>
    public static Brush Amber { get; } = Resource("WarnBrush", 0xE2B05E);

    /// <summary>High load, battery under 20 %.</summary>
    public static Brush Red { get; } = Resource("DangerBrush", 0xE5806E);

    public static FontFamily Font { get; } = new("Segoe UI Variable Text, Segoe UI");
    public static FontFamily SmallFont { get; } = new("Segoe UI Variable Small, Segoe UI");

    // ---- Fluent UI System Icons, 16 px regular ----

    public static Geometry MusicNote { get; } = Icon("M12 5.20951V10.2675C11.7058 10.0974 11.3643 9.99998 11 9.99998C9.89543 9.99998 9 10.8954 9 12C9 13.1045 9.89543 14 11 14C12.1046 14 13 13.1045 13 12V1.92568C13 1.37245 12.4519 0.986213 11.9309 1.17228L5.53093 3.458C5.21254 3.57171 5 3.8733 5 4.21139V11.2675C4.70583 11.0974 4.36429 11 4 11C2.89543 11 2 11.8954 2 13C2 14.1045 2.89543 15 4 15C5.10457 15 6 14.1045 6 13V7.35237L12 5.20951ZM12 4.14765L6 6.2905V4.35233L12 2.20948V4.14765ZM11 11C11.5523 11 12 11.4477 12 12C12 12.5523 11.5523 13 11 13C10.4477 13 10 12.5523 10 12C10 11.4477 10.4477 11 11 11ZM4 12C4.55228 12 5 12.4477 5 13C5 13.5523 4.55228 14 4 14C3.44772 14 3 13.5523 3 13C3 12.4477 3.44772 12 4 12Z");

    public static Geometry Headphones { get; } = Icon("M8 1C11.866 1 15 4.13401 15 8V12.5C15 13.8807 13.8807 15 12.5 15H10.5C10.2239 15 10 14.7761 10 14.5V9.5C10 9.22386 10.2239 9 10.5 9H14V8C14 4.68629 11.3137 2 8 2C4.68629 2 2 4.68629 2 8V9H5.5C5.77614 9 6 9.22386 6 9.5V14.5C6 14.7761 5.77614 15 5.5 15H3.5C2.20566 15 1.14082 14.0164 1.0127 12.7559L1 12.5V8C1 4.13401 4.13401 1 8 1ZM2 12.5L2.00781 12.6533C2.08461 13.4097 2.72334 14 3.5 14H5V10H2V12.5ZM11 14H12.5C13.3284 14 14 13.3284 14 12.5V10H11V14Z");

    public static Geometry Keyboard { get; } = Icon("M3 10.5C3 10.2239 3.22386 10 3.5 10H12.5C12.7761 10 13 10.2239 13 10.5C13 10.7761 12.7761 11 12.5 11H3.5C3.22386 11 3 10.7761 3 10.5ZM3.25 7C3.66421 7 4 6.66421 4 6.25C4 5.83579 3.66421 5.5 3.25 5.5C2.83579 5.5 2.5 5.83579 2.5 6.25C2.5 6.66421 2.83579 7 3.25 7ZM10 6.25C10 6.66421 9.66421 7 9.25 7C8.83579 7 8.5 6.66421 8.5 6.25C8.5 5.83579 8.83579 5.5 9.25 5.5C9.66421 5.5 10 5.83579 10 6.25ZM6.25 7C6.66421 7 7 6.66421 7 6.25C7 5.83579 6.66421 5.5 6.25 5.5C5.83579 5.5 5.5 5.83579 5.5 6.25C5.5 6.66421 5.83579 7 6.25 7ZM13 6.25C13 6.66421 12.6642 7 12.25 7C11.8358 7 11.5 6.66421 11.5 6.25C11.5 5.83579 11.8358 5.5 12.25 5.5C12.6642 5.5 13 5.83579 13 6.25ZM5.25 9C5.66421 9 6 8.66421 6 8.25C6 7.83579 5.66421 7.5 5.25 7.5C4.83579 7.5 4.5 7.83579 4.5 8.25C4.5 8.66421 4.83579 9 5.25 9ZM9 8.25C9 8.66421 8.66421 9 8.25 9C7.83579 9 7.5 8.66421 7.5 8.25C7.5 7.83579 7.83579 7.5 8.25 7.5C8.66421 7.5 9 7.83579 9 8.25ZM11.25 9C11.6642 9 12 8.66421 12 8.25C12 7.83579 11.6642 7.5 11.25 7.5C10.8358 7.5 10.5 7.83579 10.5 8.25C10.5 8.66421 10.8358 9 11.25 9ZM1 4.75C1 3.7835 1.7835 3 2.75 3H13.25C14.2165 3 15 3.7835 15 4.75V11.25C15 12.2165 14.2165 13 13.25 13H2.75C1.7835 13 1 12.2165 1 11.25V4.75ZM2.75 4C2.33579 4 2 4.33579 2 4.75V11.25C2 11.6642 2.33579 12 2.75 12H13.25C13.6642 12 14 11.6642 14 11.25V4.75C14 4.33579 13.6642 4 13.25 4H2.75Z");

    public static Geometry Headset { get; } = Icon("M4 6C4 3.79086 5.79086 2 8 2C10.2091 2 12 3.79086 12 6H10C9.44772 6 9 6.44772 9 7V10C9 10.5523 9.44772 11 10 11H11C12.1046 11 13 10.1046 13 9V6C13 3.23858 10.7614 1 8 1C5.23858 1 3 3.23858 3 6V11C3 12.6569 4.34315 14 6 14H6.58535C6.79127 14.5826 7.34689 15 8 15C8.82843 15 9.5 14.3284 9.5 13.5C9.5 12.6716 8.82843 12 8 12C7.34689 12 6.79127 12.4174 6.58535 13H6C4.89543 13 4 12.1046 4 11H6C6.55228 11 7 10.5523 7 10V7C7 6.44772 6.55228 6 6 6H4ZM12 7V9C12 9.55228 11.5523 10 11 10H10V7H12ZM6 10H4V7H6V10ZM7.5 13.5C7.5 13.2239 7.72386 13 8 13C8.27614 13 8.5 13.2239 8.5 13.5C8.5 13.7761 8.27614 14 8 14C7.72386 14 7.5 13.7761 7.5 13.5Z");

    public static Geometry ArrowDown { get; } = Icon("M8.5 2.5C8.5 2.22386 8.27615 2 8 2C7.72386 2 7.5 2.22386 7.5 2.5V12.197L3.87165 8.16552C3.68692 7.96026 3.37078 7.94362 3.16552 8.12835C2.96027 8.31308 2.94363 8.62923 3.12836 8.83448L7.62836 13.8345C7.72318 13.9398 7.85826 14 8 14C8.14175 14 8.27683 13.9398 8.37165 13.8345L12.8717 8.83448C13.0564 8.62923 13.0397 8.31308 12.8345 8.12835C12.6292 7.94362 12.3131 7.96026 12.1284 8.16552L8.5 12.197V2.5Z");

    public static Geometry ArrowUp { get; } = Icon("M7.5 13.5C7.5 13.7761 7.72386 14 8 14C8.27614 14 8.5 13.7761 8.5 13.5V3.80298L12.1283 7.83448C12.3131 8.03974 12.6292 8.05638 12.8345 7.87165C13.0397 7.68692 13.0564 7.37077 12.8716 7.16552L8.37165 2.16552C8.27683 2.06016 8.14174 2 8 2C7.85826 2 7.72317 2.06016 7.62835 2.16552L3.12836 7.16552C2.94363 7.37077 2.96027 7.68692 3.16552 7.87165C3.37078 8.05638 3.68692 8.03974 3.87165 7.83448L7.5 3.80298V13.5Z");

    /// <summary>Our own mouse in the same line style (Fluent has none): a rounded body with a wheel line.</summary>
    public static Geometry Mouse { get; } = Icon("M8 1C10.7614 1 13 3.23858 13 6V10C13 12.7614 10.7614 15 8 15C5.23858 15 3 12.7614 3 10V6C3 3.23858 5.23858 1 8 1ZM8 2C5.79086 2 4 3.79086 4 6V10C4 12.2091 5.79086 14 8 14C10.2091 14 12 12.2091 12 10V6C12 3.79086 10.2091 2 8 2ZM8 4C8.27614 4 8.5 4.22386 8.5 4.5V6.5C8.5 6.77614 8.27614 7 8 7C7.72386 7 7.5 6.77614 7.5 6.5V4.5C7.5 4.22386 7.72386 4 8 4Z");

    /// <summary>A 16×16 icon as a ready element.</summary>
    public static FrameworkElement IconView(Geometry icon, Brush? brush = null, double size = IconSize) => new Viewbox
    {
        Width = size,
        Height = size,
        VerticalAlignment = VerticalAlignment.Center,
        Child = new Path { Data = icon, Fill = brush ?? Text, Width = 16, Height = 16 },
    };

    /// <summary>Text for the pill. Figures are tabular, so a changing number never shifts its neighbours.</summary>
    public static TextBlock Label(string text, Brush? brush = null, double size = FontSize)
    {
        var t = new TextBlock
        {
            Text = text,
            FontFamily = Font,
            FontSize = size,
            Foreground = brush ?? Text,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Typography.SetNumeralAlignment(t, FontNumeralAlignment.Tabular);
        return t;
    }

    /// <summary>The width a text takes in this font, for fixed-width figures.</summary>
    public static double TextWidth(string sample, double size = FontSize)
    {
        var t = Label(sample, size: size);
        t.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return Math.Ceiling(t.DesiredSize.Width);
    }

    private static Geometry Icon(string data)
    {
        var g = Geometry.Parse(data);
        g.Freeze();
        return g;
    }

    private static Brush Resource(string key, int rgb)
    {
        if (Application.Current?.TryFindResource(key) is Brush b) return b;
        var brush = new SolidColorBrush(Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
        brush.Freeze();
        return brush;
    }
}
