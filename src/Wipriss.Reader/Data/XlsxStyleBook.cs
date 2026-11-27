using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Wipriss.Reader.Data;

/// <summary>
/// The styles part of a workbook being saved. A cell that got a new look gets a new cell style: a copy of its old
/// one with only the changed parts replaced, written after all the existing ones. Existing styles, fonts and fills
/// keep their numbers, so nothing else in the file has to change.
/// </summary>
internal sealed class XlsxStyleBook
{
    private static readonly string[] FontOrder =
        ["b", "i", "strike", "condense", "extend", "outline", "shadow", "u", "vertAlign", "sz", "color", "name", "family", "charset", "scheme"];

    private static readonly string[] SectionOrder =
        ["numFmts", "fonts", "fills", "borders", "cellStyleXfs", "cellXfs", "cellStyles", "dxfs", "tableStyles", "colors", "extLst"];

    private readonly XDocument _doc;
    private readonly XNamespace _ns;
    private readonly uint[] _theme;
    private readonly List<XElement> _fonts, _fills, _xfs;
    private readonly XElement _fontsSection, _fillsSection, _xfsSection;
    private readonly Dictionary<string, int> _fontIds = [], _fillIds = [], _xfIds = [];
    private readonly Dictionary<(int, CellFormat), int> _cache = [];
    private readonly List<byte> _kinds;

    private XlsxStyleBook(XDocument doc, uint[] theme, byte[] kinds, bool created)
    {
        _doc = doc;
        _ns = doc.Root!.Name.Namespace;
        _theme = theme;
        Created = created;
        _fontsSection = Section("fonts");
        _fillsSection = Section("fills");
        if (!_fillsSection.Elements(_ns + "fill").Any())
        {
            // Excel wants these two first fills.
            _fillsSection.Add(new XElement(_ns + "fill", new XElement(_ns + "patternFill", new XAttribute("patternType", "none"))),
                new XElement(_ns + "fill", new XElement(_ns + "patternFill", new XAttribute("patternType", "gray125"))));
        }
        _xfsSection = Section("cellXfs");
        if (!_fontsSection.Elements(_ns + "font").Any())
            _fontsSection.Add(new XElement(_ns + "font", new XElement(_ns + "sz", new XAttribute("val", "11")), new XElement(_ns + "name", new XAttribute("val", "Calibri"))));
        if (!_xfsSection.Elements(_ns + "xf").Any())
            _xfsSection.Add(new XElement(_ns + "xf", new XAttribute("numFmtId", 0), new XAttribute("fontId", 0), new XAttribute("fillId", 0), new XAttribute("borderId", 0)));
        _fonts = [.. _fontsSection.Elements(_ns + "font")];
        _fills = [.. _fillsSection.Elements(_ns + "fill")];
        _xfs = [.. _xfsSection.Elements(_ns + "xf")];
        DefaultSize = _fonts.Count > 0 && Child(_fonts[0], "sz") is { } sz
            && double.TryParse((string?)sz.Attribute("val"), NumberStyles.Float, CultureInfo.InvariantCulture, out double size) ? size : 11;
        _kinds = [.. kinds];
        while (_kinds.Count < _xfs.Count) _kinds.Add(0);
    }

    /// <summary>The workbook had no styles part: it has to be added to the package.</summary>
    public bool Created { get; }

    public bool Changed { get; private set; }

    public double DefaultSize { get; }

    /// <summary>For each cell style: 1 = date or time, 2 = text, 0 = other (new styles copy their old one's).</summary>
    public byte Kind(int xf) => (uint)xf < (uint)_kinds.Count ? _kinds[xf] : (byte)0;

    public static XlsxStyleBook Load(XlsxParts parts)
    {
        var theme = XlsxStyles.ThemeColors(parts);
        var kinds = parts.StyleKinds();
        if (parts.StylesPath is { } path && parts.LoadXml(path) is { Root: not null } doc) return new XlsxStyleBook(doc, theme, kinds, created: false);
        var ns = parts.Ns;
        var created = new XDocument(new XDeclaration("1.0", "UTF-8", "yes"), new XElement(ns + "styleSheet"));
        return new XlsxStyleBook(created, theme, kinds, created: true);
    }

    /// <summary>What an existing cell style looks like to the viewer.</summary>
    public CellFormat Look(int xf)
    {
        if ((uint)xf >= (uint)_xfs.Count) xf = 0;
        var element = _xfs[xf];
        int fontId = (int?)element.Attribute("fontId") ?? 0, fillId = (int?)element.Attribute("fillId") ?? 0;
        return XlsxStyles.Parse(element, (uint)fontId < (uint)_fonts.Count ? _fonts[fontId] : null,
            (uint)fillId < (uint)_fills.Count ? _fills[fillId] : null, DefaultSize, _theme);
    }

    /// <summary>The cell style to use instead of <paramref name="xf"/> so the cell looks like <paramref name="look"/>.</summary>
    public int Style(int xf, CellFormat look)
    {
        if ((uint)xf >= (uint)_xfs.Count) xf = 0;
        if (_cache.TryGetValue((xf, look), out int known)) return known;
        var old = Look(xf);
        if (old == look) return _cache[(xf, look)] = xf;

        var element = new XElement(_xfs[xf]);
        if (old.Size != look.Size || old.Bold != look.Bold || old.Italic != look.Italic || old.Underline != look.Underline || old.Color != look.Color)
        {
            int fontId = (int?)element.Attribute("fontId") ?? 0;
            var font = (uint)fontId < (uint)_fonts.Count ? new XElement(_fonts[fontId]) : new XElement(_ns + "font");
            if (old.Size != look.Size) SetChild(font, "sz", [new XAttribute("val", Format(look.Size > 0 ? look.Size : DefaultSize))]);
            if (old.Bold != look.Bold) SetChild(font, "b", look.Bold ? [] : null);
            if (old.Italic != look.Italic) SetChild(font, "i", look.Italic ? [] : null);
            if (old.Underline != look.Underline) SetChild(font, "u", look.Underline ? [] : null);
            if (old.Color != look.Color) SetChild(font, "color", look.Color is { } color ? [new XAttribute("rgb", Hex(color))] : null);
            Sort(font, FontOrder);
            element.SetAttributeValue("fontId", Add(font, _fonts, _fontsSection, _fontIds));
            element.SetAttributeValue("applyFont", 1);
        }
        if (old.Fill != look.Fill)
        {
            int fillId = 0;
            if (look.Fill is { } fill)
            {
                var pattern = new XElement(_ns + "patternFill", new XAttribute("patternType", "solid"),
                    new XElement(_ns + "fgColor", new XAttribute("rgb", Hex(fill))), new XElement(_ns + "bgColor", new XAttribute("indexed", 64)));
                fillId = Add(new XElement(_ns + "fill", pattern), _fills, _fillsSection, _fillIds);
            }
            element.SetAttributeValue("fillId", fillId);
            element.SetAttributeValue("applyFill", 1);
        }
        if (old.Horizontal != look.Horizontal || old.Vertical != look.Vertical || old.Mode != look.Mode)
        {
            var alignment = element.Element(_ns + "alignment");
            if (alignment is null)
            {
                alignment = new XElement(_ns + "alignment");
                element.AddFirst(alignment);
            }
            if (old.Horizontal != look.Horizontal)
                alignment.SetAttributeValue("horizontal", look.Horizontal switch { HAlign.Left => "left", HAlign.Center => "center", HAlign.Right => "right", _ => null });
            if (old.Vertical != look.Vertical)
                alignment.SetAttributeValue("vertical", look.Vertical switch { VAlign.Top => "top", VAlign.Center => "center", _ => null });
            if (old.Mode != look.Mode)
            {
                alignment.SetAttributeValue("wrapText", look.Mode == TextMode.Wrap ? "1" : null);
                alignment.SetAttributeValue(XlsxStyles.OverflowMark, look.Mode == TextMode.Overflow ? "0" : null);
            }
            if (!alignment.HasAttributes) alignment.Remove();
            element.SetAttributeValue("applyAlignment", 1);
        }
        int index = Add(element, _xfs, _xfsSection, _xfIds, out bool added);
        if (added) _kinds.Add(_kinds[xf]);
        return _cache[(xf, look)] = index;
    }

    /// <summary>Writes the changed part into the package (adding it, with its link, when the workbook had none).</summary>
    public void Save(ZipArchive package, XlsxParts parts)
    {
        if (!Changed) return;
        string path = parts.StylesPath ?? "xl/styles.xml";
        var entry = package.GetEntry(path);
        if (entry is null)
        {
            entry = package.CreateEntry(path, CompressionLevel.Optimal);
            if (Created) Link(package, parts, path);
        }
        using var stream = entry.Open();
        stream.SetLength(0);
        using var writer = XmlWriter.Create(stream, new XmlWriterSettings { Encoding = new UTF8Encoding(false), NewLineHandling = NewLineHandling.None });
        _doc.Save(writer);
    }

    private static void Link(ZipArchive package, XlsxParts parts, string path)
    {
        Edit(package, XlsxParts.RelsPath(parts.WorkbookPath), root =>
        {
            var ns = root.Name.Namespace;
            int n = 1;
            var ids = root.Elements().Select(e => (string?)e.Attribute("Id")).ToHashSet();
            while (ids.Contains("rId" + n)) n++;
            string folder = parts.WorkbookPath.LastIndexOf('/') is var i and > 0 ? parts.WorkbookPath[..(i + 1)] : "";
            string target = path.StartsWith(folder, StringComparison.Ordinal) ? path[folder.Length..] : "/" + path;
            root.Add(new XElement(ns + "Relationship", new XAttribute("Id", "rId" + n),
                new XAttribute("Type", XlsxParts.RelationshipsNs + "/styles"), new XAttribute("Target", target)));
        });
        Edit(package, "[Content_Types].xml", root =>
        {
            var ns = root.Name.Namespace;
            root.Add(new XElement(ns + "Override", new XAttribute("PartName", "/" + path),
                new XAttribute("ContentType", "application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml")));
        });
    }

    private static void Edit(ZipArchive package, string path, Action<XElement> change)
    {
        if (package.GetEntry(path) is not { } entry) return;
        XDocument doc;
        using (var reader = XmlReader.Create(entry.Open(), XlsxParts.ReaderSettings))
            doc = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
        if (doc.Root is null) return;
        change(doc.Root);
        using var stream = entry.Open();
        stream.SetLength(0);
        using var writer = XmlWriter.Create(stream, new XmlWriterSettings { Encoding = new UTF8Encoding(false), NewLineHandling = NewLineHandling.None });
        doc.Save(writer);
    }

    private int Add(XElement element, List<XElement> list, XElement section, Dictionary<string, int> known) =>
        Add(element, list, section, known, out _);

    /// <summary>Adds an element to the end of its list (or finds the same one added before) and returns its number.</summary>
    private int Add(XElement element, List<XElement> list, XElement section, Dictionary<string, int> known, out bool added)
    {
        string key = element.ToString(SaveOptions.DisableFormatting);
        added = false;
        if (known.TryGetValue(key, out int index)) return index;
        section.Add(element);
        list.Add(element);
        section.SetAttributeValue("count", list.Count);
        Changed = true;
        added = true;
        return known[key] = list.Count - 1;
    }

    private XElement Section(string name)
    {
        var root = _doc.Root!;
        if (root.Element(_ns + name) is { } found) return found;
        var section = new XElement(_ns + name, new XAttribute("count", 0));
        int order = Array.IndexOf(SectionOrder, name);
        var before = root.Elements().LastOrDefault(e => Array.IndexOf(SectionOrder, e.Name.LocalName) is var i && i >= 0 && i < order);
        if (before is not null) before.AddAfterSelf(section);
        else root.AddFirst(section);
        Changed = true;
        return section;
    }

    private static XElement? Child(XElement parent, string name) => parent.Elements().FirstOrDefault(e => e.Name.LocalName == name);

    /// <summary>Replaces the child (null content: removes it).</summary>
    private void SetChild(XElement parent, string name, object[]? content)
    {
        parent.Elements().Where(e => e.Name.LocalName == name).Remove();
        if (content is not null) parent.Add(new XElement(_ns + name, content));
    }

    private static void Sort(XElement parent, string[] order)
    {
        var children = parent.Elements().ToList();
        var sorted = children.OrderBy(e => Array.IndexOf(order, e.Name.LocalName) is var i && i >= 0 ? i : order.Length).ToList();
        if (children.SequenceEqual(sorted)) return;
        foreach (var child in children) child.Remove();
        parent.Add(sorted);
    }

    private static string Hex(uint argb) => (argb | 0xFF000000).ToString("X8", CultureInfo.InvariantCulture);

    private static string Format(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);
}
