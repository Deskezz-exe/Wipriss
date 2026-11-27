using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Xml;

namespace Wipriss.Reader.Data;

/// <summary>
/// "Экспорт": the table as a new file of another kind. Values only (with the owner's unsaved changes): XLSX gets
/// every sheet with numbers as numbers, CSV gets the shown sheet in UTF-8 with a byte order mark (Excel then reads
/// Russian letters right). Formatting, formulas and charts are not carried over; for those, the same format is
/// saved as a copy instead.
/// </summary>
internal static class TableExport
{
    private const int MaxExcelRows = 1_048_576, MaxExcelColumns = 16_384;
    private const string MainNs = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

    public static void WriteCsv(Sheet sheet, Stream output, char delimiter, CancellationToken cancel)
    {
        var encoding = new UTF8Encoding(true);
        output.Write(encoding.GetPreamble());
        using var writer = new StreamWriter(output, new UTF8Encoding(false), 1 << 16, leaveOpen: true) { NewLine = "\r\n" };
        int last = CsvWriter.LastFilledRow(sheet), width = sheet.DataColumnCount;
        var line = new StringBuilder();
        int r = 0;
        foreach (var row in sheet.Rows(cancel))
        {
            if (r > last) break;
            line.Clear();
            for (int c = 0; c < width; c++)
            {
                if (c > 0) line.Append(delimiter);
                if (c < row.Length && row[c] is { } text) CsvWriter.AppendField(line, text, delimiter);
            }
            writer.WriteLine(line);
            r++;
        }
    }

    public static void WriteXlsx(IReadOnlyList<Sheet> sheets, Stream output, CancellationToken cancel)
    {
        foreach (var sheet in sheets)
        {
            if (CsvWriter.LastFilledRow(sheet) >= MaxExcelRows)
                throw new InvalidDataException($"В листе «{sheet.Name}» больше строк, чем помещается в Excel (1 048 576). Сохраните его в CSV.");
        }
        using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        var names = SheetNames(sheets);
        const string ct = "application/vnd.openxmlformats-officedocument.spreadsheetml";
        var types = new StringBuilder("""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/>""");
        types.Append($"""<Override PartName="/xl/workbook.xml" ContentType="{ct}.sheet.main+xml"/><Override PartName="/xl/styles.xml" ContentType="{ct}.styles+xml"/>""");
        var book = new StringBuilder($"""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><workbook xmlns="{MainNs}" xmlns:r="{XlsxParts.RelationshipsNs}"><sheets>""");
        var rels = new StringBuilder("""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">""");
        for (int i = 0; i < sheets.Count; i++)
        {
            int n = i + 1;
            types.Append($"""<Override PartName="/xl/worksheets/sheet{n}.xml" ContentType="{ct}.worksheet+xml"/>""");
            book.Append($"""<sheet name="{Escape(names[i])}" sheetId="{n}" r:id="rId{n}"/>""");
            rels.Append($"""<Relationship Id="rId{n}" Type="{XlsxParts.RelationshipsNs}/worksheet" Target="worksheets/sheet{n}.xml"/>""");
        }
        types.Append("</Types>");
        book.Append("</sheets></workbook>");
        rels.Append($"""<Relationship Id="rId{sheets.Count + 1}" Type="{XlsxParts.RelationshipsNs}/styles" Target="styles.xml"/></Relationships>""");

        Text(zip, "[Content_Types].xml", types.ToString());
        Text(zip, "_rels/.rels", """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/></Relationships>""");
        Text(zip, "xl/workbook.xml", book.ToString());
        Text(zip, "xl/_rels/workbook.xml.rels", rels.ToString());
        Text(zip, "xl/styles.xml", $"""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><styleSheet xmlns="{MainNs}"><fonts count="1"><font><sz val="11"/><name val="Calibri"/></font></fonts><fills count="2"><fill><patternFill patternType="none"/></fill><fill><patternFill patternType="gray125"/></fill></fills><borders count="1"><border><left/><right/><top/><bottom/><diagonal/></border></borders><cellStyleXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0"/></cellStyleXfs><cellXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0" xfId="0"/></cellXfs></styleSheet>""");
        for (int i = 0; i < sheets.Count; i++)
        {
            var entry = zip.CreateEntry($"xl/worksheets/sheet{i + 1}.xml", CompressionLevel.Optimal);
            using var writer = XmlWriter.Create(entry.Open(), new XmlWriterSettings { Encoding = new UTF8Encoding(false), CloseOutput = true });
            WriteSheet(sheets[i], writer, cancel);
        }
    }

    private static void WriteSheet(Sheet sheet, XmlWriter writer, CancellationToken cancel)
    {
        int last = CsvWriter.LastFilledRow(sheet);
        int width = Math.Min(sheet.DataColumnCount, MaxExcelColumns);
        writer.WriteStartDocument(true);
        writer.WriteStartElement("worksheet", MainNs);
        writer.WriteStartElement("sheetData", MainNs);
        int r = 0;
        foreach (var row in sheet.Rows(cancel))
        {
            if (r > last) break;
            bool open = false;
            for (int c = 0; c < Math.Min(width, row.Length); c++)
            {
                if (row[c] is not { } text) continue;
                if (!open)
                {
                    writer.WriteStartElement("row", MainNs);
                    writer.WriteAttributeString("r", (r + 1).ToString(CultureInfo.InvariantCulture));
                    open = true;
                }
                writer.WriteStartElement("c", MainNs);
                writer.WriteAttributeString("r", CellRef.Format(r, c));
                if (Number(sheet, r, c, text) is { } number)
                {
                    writer.WriteElementString("v", MainNs, number.ToString("R", CultureInfo.InvariantCulture));
                }
                else
                {
                    writer.WriteAttributeString("t", "inlineStr");
                    writer.WriteStartElement("is", MainNs);
                    writer.WriteStartElement("t", MainNs);
                    string clean = Clean(text);
                    if (clean.Length > 0 && (char.IsWhiteSpace(clean[0]) || char.IsWhiteSpace(clean[^1]) || clean.Contains('\n')))
                        writer.WriteAttributeString("xml", "space", null, "preserve");
                    writer.WriteString(clean);
                    writer.WriteEndElement();
                    writer.WriteEndElement();
                }
                writer.WriteEndElement();
            }
            if (open) writer.WriteEndElement();
            r++;
        }
        writer.WriteEndElement();
        writer.WriteEndElement();
        writer.WriteEndDocument();
    }

    /// <summary>
    /// A cell that shows a number becomes a number. Codes with a leading zero and phone numbers stay text; so do
    /// dates (without their format they would turn into bare day counts).
    /// </summary>
    private static double? Number(Sheet sheet, int row, int column, string text)
    {
        string trimmed = text.Trim();
        if (trimmed.Length > 1 && trimmed[0] == '0' && char.IsAsciiDigit(trimmed[1])) return null;
        if (trimmed.StartsWith('+')) return null;
        if (!Sheet.TryParseLooseNumber(trimmed, out double parsed)) return null;
        if (!sheet.NumbersFromText && !sheet.IsEdited(row, column) && sheet.TryNumber(row, column, out double exact)) return exact;
        return parsed;
    }

    private static string Clean(string text)
    {
        text = text.Replace("\r\n", "\n").Replace('\r', '\n');
        if (text.Length > 32_767) text = text[..32_767];
        var builder = new StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            char ch = text[i];
            if (char.IsHighSurrogate(ch) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])) builder.Append(ch).Append(text[++i]);
            else if (XmlConvert.IsXmlChar(ch)) builder.Append(ch);
        }
        return builder.ToString();
    }

    /// <summary>Excel's rules for sheet names: up to 31 letters, none of []:*?/\, all different.</summary>
    private static List<string> SheetNames(IReadOnlyList<Sheet> sheets)
    {
        var names = new List<string>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var sheet in sheets)
        {
            var clean = new string(sheet.Name.Where(ch => ch is not ('[' or ']' or ':' or '*' or '?' or '/' or '\\') && XmlConvert.IsXmlChar(ch)).ToArray()).Trim('\'', ' ');
            if (clean.Length == 0) clean = "Лист";
            if (clean.Length > 31) clean = clean[..31];
            string name = clean;
            for (int n = 2; !used.Add(name); n++)
            {
                string suffix = $" ({n})";
                name = (clean.Length + suffix.Length > 31 ? clean[..(31 - suffix.Length)] : clean) + suffix;
            }
            names.Add(name);
        }
        return names;
    }

    private static string Escape(string text) => System.Security.SecurityElement.Escape(text) ?? "";

    private static void Text(ZipArchive zip, string name, string text)
    {
        using var stream = zip.CreateEntry(name, CompressionLevel.Optimal).Open();
        stream.Write(new UTF8Encoding(false).GetBytes(text));
    }
}
