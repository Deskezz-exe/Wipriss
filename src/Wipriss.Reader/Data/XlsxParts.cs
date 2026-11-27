using System.IO;
using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;
using ExcelNumberFormat;

namespace Wipriss.Reader.Data;

/// <summary>
/// Finding the parts of an XLSX package: the workbook, its sheets by name, styles, the parts a sheet links to.
/// Only reads; XML is read with DTDs off and no outside resolving.
/// </summary>
internal sealed class XlsxParts
{
    public const string RelationshipsNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    private readonly ZipArchive _zip;

    public XlsxParts(ZipArchive zip)
    {
        _zip = zip;
        WorkbookPath = OfficeDocument() ?? "xl/workbook.xml";
        var workbook = LoadXml(WorkbookPath) ?? throw new InvalidDataException("В файле нет книги Excel.");
        Ns = workbook.Root!.Name.Namespace;
        WorkbookRels = Relationships(WorkbookPath);
        foreach (var sheet in workbook.Root.Elements(Ns + "sheets").Elements(Ns + "sheet"))
        {
            string? name = (string?)sheet.Attribute("name");
            string? id = sheet.Attributes().FirstOrDefault(a => a.Name.LocalName == "id")?.Value;
            if (name is null || id is null || !WorkbookRels.TryGetValue(id, out var rel)) continue;
            SheetPaths.TryAdd(name, rel.Target);
        }
        StylesPath = WorkbookRels.Values.FirstOrDefault(r => r.Type.EndsWith("/styles", StringComparison.Ordinal)).Target;
        CalcChainPath = WorkbookRels.Values.FirstOrDefault(r => r.Type.EndsWith("/calcChain", StringComparison.Ordinal)).Target;
    }

    /// <summary>The main namespace of the workbook (transitional or strict).</summary>
    public XNamespace Ns { get; }

    public string WorkbookPath { get; }

    public Dictionary<string, (string Type, string Target)> WorkbookRels { get; }

    public Dictionary<string, string> SheetPaths { get; } = new(StringComparer.Ordinal);

    public string? StylesPath { get; }

    public string? CalcChainPath { get; }

    public static XmlReaderSettings ReaderSettings => new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        IgnoreWhitespace = false,
        CloseInput = true,
    };

    public ZipArchiveEntry? Entry(string path) =>
        _zip.GetEntry(path) ?? _zip.Entries.FirstOrDefault(e => string.Equals(e.FullName, path, StringComparison.OrdinalIgnoreCase));

    public XDocument? LoadXml(string path)
    {
        if (Entry(path) is not { } entry) return null;
        using var reader = XmlReader.Create(entry.Open(), ReaderSettings);
        return XDocument.Load(reader, LoadOptions.PreserveWhitespace);
    }

    /// <summary>The relationships of a part: id → (type, full path of the target inside the package).</summary>
    public Dictionary<string, (string Type, string Target)> Relationships(string part)
    {
        var result = new Dictionary<string, (string, string)>(StringComparer.Ordinal);
        string folder = Folder(part);
        if (LoadXml(RelsPath(part)) is not { Root: { } root }) return result;
        foreach (var rel in root.Elements().Where(e => e.Name.LocalName == "Relationship"))
        {
            string? id = (string?)rel.Attribute("Id"), type = (string?)rel.Attribute("Type"), target = (string?)rel.Attribute("Target");
            if (id is null || type is null || target is null) continue;
            if (string.Equals((string?)rel.Attribute("TargetMode"), "External", StringComparison.OrdinalIgnoreCase)) continue;
            result[id] = (type, Resolve(folder, target));
        }
        return result;
    }

    public static string RelsPath(string part)
    {
        string folder = Folder(part);
        return (folder.Length > 0 ? folder + "/" : "") + "_rels/" + part[(part.LastIndexOf('/') + 1)..] + ".rels";
    }

    private static string Folder(string part) => part.LastIndexOf('/') is var i and > 0 ? part[..i] : "";

    /// <summary>A relationship target as a path inside the package ("../tables/t1.xml" from "xl/worksheets").</summary>
    public static string Resolve(string folder, string target)
    {
        target = Uri.UnescapeDataString(target.Replace('\\', '/'));
        var parts = new List<string>();
        if (!target.StartsWith('/') && folder.Length > 0) parts.AddRange(folder.Split('/'));
        foreach (var piece in target.TrimStart('/').Split('/'))
        {
            if (piece is "" or ".") continue;
            if (piece == "..")
            {
                if (parts.Count > 0) parts.RemoveAt(parts.Count - 1);
            }
            else parts.Add(piece);
        }
        return string.Join('/', parts);
    }

    private string? OfficeDocument()
    {
        if (LoadXml("_rels/.rels") is not { Root: { } root }) return null;
        foreach (var rel in root.Elements().Where(e => e.Name.LocalName == "Relationship"))
        {
            if (((string?)rel.Attribute("Type"))?.EndsWith("/officeDocument", StringComparison.Ordinal) == true
                && (string?)rel.Attribute("Target") is { } target)
                return Resolve("", target);
        }
        return null;
    }

    // ---- Cell styles: is a cell a date, or forced text ----

    /// <summary>For each cell style (the "s" of a cell): 1 = date or time, 2 = text ("@"), 0 = anything else.</summary>
    public byte[] StyleKinds()
    {
        if (StylesPath is null || LoadXml(StylesPath) is not { Root: { } root }) return [];
        var ns = root.Name.Namespace;
        var custom = new Dictionary<int, string>();
        foreach (var format in root.Elements(ns + "numFmts").Elements(ns + "numFmt"))
        {
            if ((int?)format.Attribute("numFmtId") is { } id && (string?)format.Attribute("formatCode") is { } code) custom[id] = code;
        }
        var xfs = root.Elements(ns + "cellXfs").Elements(ns + "xf").ToList();
        var kinds = new byte[xfs.Count];
        for (int i = 0; i < xfs.Count; i++)
        {
            int id = (int?)xfs[i].Attribute("numFmtId") ?? 0;
            kinds[i] = custom.TryGetValue(id, out var code) ? KindOf(code) : BuiltInKind(id);
        }
        return kinds;
    }

    private static byte BuiltInKind(int id) => id switch
    {
        49 => 2,
        (>= 14 and <= 22) or (>= 27 and <= 36) or (>= 45 and <= 47) or (>= 50 and <= 58) => 1,
        _ => 0,
    };

    private static byte KindOf(string code)
    {
        if (code.Trim() == "@") return 2;
        try
        {
            var format = new NumberFormat(code);
            return format.IsValid && format.IsDateTimeFormat ? (byte)1 : (byte)0;
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or InvalidOperationException)
        {
            return 0;
        }
    }

    // ---- Cells the editor must not change ----

    /// <summary>
    /// Cells of a sheet that can't be changed safely here: array formulas and data tables (one formula for a
    /// whole range), the first cell of a shared formula (the cells next to it copy it), the header of a smart
    /// table (Excel checks it against the table's column names).
    /// </summary>
    public Dictionary<(int Row, int Column), string> Locks(string sheetPath, int limit = 1_000_000)
    {
        var locks = new Dictionary<(int, int), string>();
        if (Entry(sheetPath) is not { } entry) return locks;
        var tableIds = new List<string>();
        using (var reader = XmlReader.Create(entry.Open(), ReaderSettings))
        {
            int row = -1, column = -1;
            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element) continue;
                switch (reader.LocalName)
                {
                    case "row":
                        row = CellRef.ParseRow(reader.GetAttribute("r")) ?? row + 1;
                        column = -1;
                        break;
                    case "c":
                        column = CellRef.Parse(reader.GetAttribute("r")) is { } at ? at.Column : column + 1;
                        break;
                    case "f":
                        string? type = reader.GetAttribute("t");
                        string? range = reader.GetAttribute("ref");
                        if (type is "array" or "dataTable" && CellRef.ParseRange(range) is { } area)
                            LockRange(area, type == "array" ? "Это часть формулы массива. Её можно изменить только в Excel." : "Это часть таблицы данных Excel. Её можно изменить только в Excel.");
                        else if (type == "shared" && range is not null && row >= 0 && column >= 0)
                            locks.TryAdd((row, column), "От формулы в этой ячейке зависят соседние. Её можно изменить только в Excel.");
                        break;
                    case "tablePart":
                        if ((reader.GetAttribute("id", RelationshipsNs) ?? FirstAttribute(reader, "id")) is { } id) tableIds.Add(id);
                        break;
                }
                if (locks.Count >= limit) break;
            }
        }
        if (tableIds.Count > 0)
        {
            var rels = Relationships(sheetPath);
            foreach (var id in tableIds)
            {
                if (!rels.TryGetValue(id, out var rel) || LoadXml(rel.Target) is not { Root: { } table }) continue;
                if (CellRef.ParseRange((string?)table.Attribute("ref")) is not { } area) continue;
                if (((int?)table.Attribute("headerRowCount") ?? 1) == 0) continue;
                LockRange(area with { ToRow = area.FromRow }, "Это заголовок умной таблицы Excel. Его можно изменить только в Excel.");
            }
        }
        return locks;

        void LockRange(CellRange area, string why)
        {
            for (int r = area.FromRow; r <= area.ToRow && locks.Count < limit; r++)
                for (int c = area.FromColumn; c <= area.ToColumn && locks.Count < limit; c++) locks.TryAdd((r, c), why);
        }
    }

    private static string? FirstAttribute(XmlReader reader, string localName)
    {
        string? value = null;
        if (reader.MoveToFirstAttribute())
        {
            do
            {
                if (reader.LocalName == localName)
                {
                    value = reader.Value;
                    break;
                }
            }
            while (reader.MoveToNextAttribute());
            reader.MoveToElement();
        }
        return value;
    }
}

/// <summary>Cell addresses like "B12" and ranges like "A1:D20", zero-based.</summary>
internal static class CellRef
{
    public static string Format(int row, int column) => Sheet.ColumnName(column) + (row + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);

    public static (int Row, int Column)? Parse(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var span = text.AsSpan().Trim().TrimStart('$');
        int column = 0, i = 0;
        while (i < span.Length && char.IsAsciiLetter(span[i]))
        {
            column = column * 26 + (char.ToUpperInvariant(span[i]) - 'A' + 1);
            if (column > 20_000) return null;
            i++;
        }
        if (i == 0) return null;
        if (i < span.Length && span[i] == '$') i++;
        if (!int.TryParse(span[i..], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int row) || row < 1)
            return null;
        return (row - 1, column - 1);
    }

    public static int? ParseRow(string? text) =>
        int.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int row) && row >= 1 ? row - 1 : null;

    public static CellRange? ParseRange(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        int colon = text.IndexOf(':');
        var from = Parse(colon < 0 ? text : text[..colon]);
        var to = colon < 0 ? from : Parse(text[(colon + 1)..]);
        if (from is not { } a || to is not { } b) return null;
        return new CellRange(Math.Min(a.Row, b.Row), Math.Min(a.Column, b.Column), Math.Max(a.Row, b.Row), Math.Max(a.Column, b.Column));
    }
}
