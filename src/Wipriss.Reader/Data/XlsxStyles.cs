using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;

namespace Wipriss.Reader.Data;

/// <summary>
/// Reads cell formatting from an XLSX: fonts (size, bold, italic, underline, color), solid fills, alignment and
/// wrapping from styles.xml, and for each sheet which style every cell uses and the row heights. Sheets are read
/// as a stream, like <see cref="XlsxParts.Locks"/>.
/// </summary>
internal static class XlsxStyles
{
    /// <summary>
    /// The mark Wipriss leaves for "show whole" (text over the empty cells next to it): an explicit
    /// shrinkToFit="0". It is Excel's default value, so Excel reads the file as usual and shows such text in full.
    /// </summary>
    public const string OverflowMark = "shrinkToFit";

    /// <summary>Formatting of the named sheets (null: all), by sheet name. A sheet missing from the file is left out.</summary>
    public static Dictionary<string, SheetFormats> Read(string path, IEnumerable<string>? sheetNames, CancellationToken cancel)
    {
        var result = new Dictionary<string, SheetFormats>(StringComparer.Ordinal);
        using var zip = new ZipArchive(Workbook.OpenShared(path), ZipArchiveMode.Read);
        var parts = new XlsxParts(zip);
        var (styles, defaultSize) = Styles(parts);
        foreach (var name in sheetNames ?? [.. parts.SheetPaths.Keys])
        {
            cancel.ThrowIfCancellationRequested();
            if (!parts.SheetPaths.TryGetValue(name, out var sheetPath) || parts.Entry(sheetPath) is not { } entry) continue;
            var formats = new SheetFormats(styles, defaultSize);
            ReadSheet(entry, formats, cancel);
            result[name] = formats;
        }
        return result;
    }

    /// <summary>Every cell style of the workbook (cellXfs), and the usual font size.</summary>
    public static (CellFormat[] Styles, double DefaultSize) Styles(XlsxParts parts)
    {
        if (parts.StylesPath is null || parts.LoadXml(parts.StylesPath) is not { Root: { } root }) return ([], 11);
        var theme = ThemeColors(parts);
        return Styles(root, theme);
    }

    public static (CellFormat[] Styles, double DefaultSize) Styles(XElement root, uint[] theme)
    {
        var ns = root.Name.Namespace;
        var fonts = root.Elements(ns + "fonts").Elements(ns + "font").ToList();
        var fills = root.Elements(ns + "fills").Elements(ns + "fill").ToList();
        double defaultSize = fonts.Count > 0 ? FontSize(fonts[0]) ?? 11 : 11;
        var xfs = root.Elements(ns + "cellXfs").Elements(ns + "xf").ToList();
        var styles = new CellFormat[xfs.Count];
        for (int i = 0; i < xfs.Count; i++)
        {
            var xf = xfs[i];
            int fontId = (int?)xf.Attribute("fontId") ?? 0, fillId = (int?)xf.Attribute("fillId") ?? 0;
            styles[i] = Parse(xf, (uint)fontId < (uint)fonts.Count ? fonts[fontId] : null,
                (uint)fillId < (uint)fills.Count ? fills[fillId] : null, defaultSize, theme);
        }
        return (styles, defaultSize);
    }

    /// <summary>One cell style as the viewer sees it.</summary>
    public static CellFormat Parse(XElement xf, XElement? font, XElement? fill, double defaultSize, uint[] theme)
    {
        var format = CellFormat.Plain;
        if (font is not null)
        {
            double size = FontSize(font) ?? defaultSize;
            format = format with
            {
                Size = Math.Abs(size - defaultSize) < 0.01 ? 0 : size,
                Bold = Flag(Child(font, "b")),
                Italic = Flag(Child(font, "i")),
                Underline = Child(font, "u") is { } u && (string?)u.Attribute("val") is not "none",
                Color = Color(Child(font, "color"), theme),
            };
        }
        if (fill is not null && Child(fill, "patternFill") is { } pattern && (string?)pattern.Attribute("patternType") == "solid")
            format = format with { Fill = Color(Child(pattern, "fgColor"), theme) ?? 0xFF000000 };
        if (Child(xf, "alignment") is { } alignment)
        {
            format = format with
            {
                Horizontal = (string?)alignment.Attribute("horizontal") switch
                {
                    "left" or "justify" or "distributed" or "fill" => HAlign.Left,
                    "center" or "centerContinuous" => HAlign.Center,
                    "right" => HAlign.Right,
                    _ => HAlign.General,
                },
                Vertical = (string?)alignment.Attribute("vertical") switch
                {
                    "top" => VAlign.Top,
                    "center" or "justify" or "distributed" => VAlign.Center,
                    _ => VAlign.Bottom,
                },
                Mode = IsTrue((string?)alignment.Attribute("wrapText")) ? TextMode.Wrap
                    : alignment.Attribute(OverflowMark) is { } mark && !IsTrue(mark.Value) ? TextMode.Overflow
                    : TextMode.Clip,
            };
        }
        return format;
    }

    private static XElement? Child(XElement parent, string name) => parent.Elements().FirstOrDefault(e => e.Name.LocalName == name);

    private static bool Flag(XElement? element) => element is not null && ((string?)element.Attribute("val") is null || IsTrue((string?)element.Attribute("val")));

    private static bool IsTrue(string? value) => value is "1" or "true";

    private static double? FontSize(XElement font) =>
        Child(font, "sz") is { } sz && double.TryParse((string?)sz.Attribute("val"), NumberStyles.Float, CultureInfo.InvariantCulture, out double size)
        && size is > 0 and < 500 ? size : null;

    // ---- Colors ----

    /// <summary>A color element (rgb, theme with tint, indexed) as 0xAARRGGBB, or null for automatic.</summary>
    public static uint? Color(XElement? color, uint[] theme)
    {
        if (color is null || IsTrue((string?)color.Attribute("auto"))) return null;
        uint? rgb = null;
        if ((string?)color.Attribute("rgb") is { } hex && uint.TryParse(hex.Length == 6 ? "FF" + hex : hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint argb))
            rgb = argb | 0xFF000000;
        else if ((int?)color.Attribute("theme") is { } t && (uint)t < (uint)theme.Length) rgb = theme[t];
        else if ((int?)color.Attribute("indexed") is { } i && (uint)i < (uint)Palette.Length) rgb = Palette[i];
        if (rgb is null) return null;
        if (double.TryParse((string?)color.Attribute("tint"), NumberStyles.Float, CultureInfo.InvariantCulture, out double tint) && tint != 0)
            rgb = Tint(rgb.Value, Math.Clamp(tint, -1, 1));
        return rgb;
    }

    /// <summary>Excel's tint: lighter or darker in HSL lightness.</summary>
    private static uint Tint(uint argb, double tint)
    {
        double r = (argb >> 16 & 0xFF) / 255.0, g = (argb >> 8 & 0xFF) / 255.0, b = (argb & 0xFF) / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        double l = (max + min) / 2, h = 0, s = 0;
        if (max != min)
        {
            double d = max - min;
            s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
            h = max == r ? (g - b) / d + (g < b ? 6 : 0) : max == g ? (b - r) / d + 2 : (r - g) / d + 4;
            h /= 6;
        }
        l = tint < 0 ? l * (1 + tint) : l * (1 - tint) + tint;
        double q = l < 0.5 ? l * (1 + s) : l + s - l * s, p = 2 * l - q;
        return 0xFF000000 | (Channel(p, q, h + 1.0 / 3) << 16) | (Channel(p, q, h) << 8) | Channel(p, q, h - 1.0 / 3);

        static uint Channel(double p, double q, double t)
        {
            if (t < 0) t += 1;
            if (t > 1) t -= 1;
            double v = t < 1.0 / 6 ? p + (q - p) * 6 * t : t < 0.5 ? q : t < 2.0 / 3 ? p + (q - p) * (2.0 / 3 - t) * 6 : p;
            return (uint)Math.Clamp(Math.Round(v * 255), 0, 255);
        }
    }

    /// <summary>The workbook theme's colors in Excel's order (light 1, dark 1, light 2, dark 2, accents, links).</summary>
    public static uint[] ThemeColors(XlsxParts parts)
    {
        uint[] colors = [0xFFFFFFFF, 0xFF000000, 0xFFE7E6E6, 0xFF44546A, 0xFF4472C4, 0xFFED7D31, 0xFFA5A5A5, 0xFFFFC000, 0xFF5B9BD5, 0xFF70AD47, 0xFF0563C1, 0xFF954F72];
        var themePath = parts.WorkbookRels.Values.FirstOrDefault(r => r.Type.EndsWith("/theme", StringComparison.Ordinal)).Target;
        if (themePath is null || parts.LoadXml(themePath) is not { Root: { } root }) return colors;
        var scheme = root.Descendants().FirstOrDefault(e => e.Name.LocalName == "clrScheme");
        if (scheme is null) return colors;
        string[] order = ["lt1", "dk1", "lt2", "dk2", "accent1", "accent2", "accent3", "accent4", "accent5", "accent6", "hlink", "folHlink"];
        for (int i = 0; i < order.Length; i++)
        {
            var slot = scheme.Elements().FirstOrDefault(e => e.Name.LocalName == order[i])?.Elements().FirstOrDefault();
            string? hex = slot?.Name.LocalName switch
            {
                "srgbClr" => (string?)slot.Attribute("val"),
                "sysClr" => (string?)slot.Attribute("lastClr"),
                _ => null,
            };
            if (hex is { Length: 6 } && uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint rgb)) colors[i] = 0xFF000000 | rgb;
        }
        return colors;
    }

    /// <summary>Excel's indexed colors (the old 64-color palette); 64 and above are automatic.</summary>
    private static readonly uint[] Palette =
    [
        0x000000, 0xFFFFFF, 0xFF0000, 0x00FF00, 0x0000FF, 0xFFFF00, 0xFF00FF, 0x00FFFF,
        0x000000, 0xFFFFFF, 0xFF0000, 0x00FF00, 0x0000FF, 0xFFFF00, 0xFF00FF, 0x00FFFF,
        0x800000, 0x008000, 0x000080, 0x808000, 0x800080, 0x008080, 0xC0C0C0, 0x808080,
        0x9999FF, 0x993366, 0xFFFFCC, 0xCCFFFF, 0x660066, 0xFF8080, 0x0066CC, 0xCCCCFF,
        0x000080, 0xFF00FF, 0xFFFF00, 0x00FFFF, 0x800080, 0x800000, 0x008080, 0x0000FF,
        0x00CCFF, 0xCCFFFF, 0xCCFFCC, 0xFFFF99, 0x99CCFF, 0xFF99CC, 0xCC99FF, 0xFFCC99,
        0x3366FF, 0x33CCCC, 0x99CC00, 0xFFCC00, 0xFF9900, 0xFF6600, 0x666699, 0x969696,
        0x003366, 0x339966, 0x003300, 0x333300, 0x993300, 0x993366, 0x333399, 0x333333,
    ];

    static XlsxStyles()
    {
        for (int i = 0; i < Palette.Length; i++) Palette[i] |= 0xFF000000;
    }

    // ---- Sheets ----

    private static void ReadSheet(ZipArchiveEntry entry, SheetFormats formats, CancellationToken cancel)
    {
        using var reader = XmlReader.Create(entry.Open(), XlsxParts.ReaderSettings);
        int row = -1, column = -1, rowStyle = 0;
        long count = 0;
        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element) continue;
            switch (reader.LocalName)
            {
                case "sheetFormatPr":
                    if (Number(reader.GetAttribute("defaultRowHeight")) is { } d) formats.DefaultRowHeight = d;
                    break;
                case "row":
                    row = CellRef.ParseRow(reader.GetAttribute("r")) ?? row + 1;
                    column = -1;
                    rowStyle = IsTrue(reader.GetAttribute("customFormat")) && int.TryParse(reader.GetAttribute("s"), out int rs) ? rs : 0;
                    if (Number(reader.GetAttribute("ht")) is { } ht)
                        formats.SetRowHeight(row, ht, IsTrue(reader.GetAttribute("customHeight")));
                    if ((++count & 0xFFF) == 0) cancel.ThrowIfCancellationRequested();
                    break;
                case "c":
                    column = CellRef.Parse(reader.GetAttribute("r")) is { } at ? at.Column : column + 1;
                    int style = int.TryParse(reader.GetAttribute("s"), NumberStyles.None, CultureInfo.InvariantCulture, out int s) ? s : rowStyle;
                    if (row >= 0 && column >= 0) formats.SetCell(row, column, style);
                    break;
            }
        }
    }

    private static double? Number(string? text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) && value is >= 0 and < 2000 ? value : null;
}
