using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Xml;

namespace Wipriss.Reader.Data;

/// <summary>
/// OpenDocument spreadsheets (.ods from LibreOffice and OpenOffice): content.xml inside the zip, read as a stream.
/// The shown text is the one the file saved (already formatted by the program that wrote it); formulas are not
/// recalculated, scripts and links are ignored. DTDs are refused, so the XML can't pull anything in.
/// </summary>
internal static class OdsLoader
{
    private const string TableNs = "urn:oasis:names:tc:opendocument:xmlns:table:1.0";
    private const string OfficeNs = "urn:oasis:names:tc:opendocument:xmlns:office:1.0";
    private const string TextNs = "urn:oasis:names:tc:opendocument:xmlns:text:1.0";

    private const int MaxColumns = 16_384;
    private const int MaxRows = 1_048_576;
    /// <summary>content.xml larger than this when unpacked is refused (a "zip bomb" protection).</summary>
    private const long MaxContentBytes = 1L << 30;

    public static Workbook Load(string path, IProgress<long>? progress, CancellationToken cancel)
    {
        using var stream = Workbook.OpenShared(path);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        var entry = zip.GetEntry("content.xml") ?? throw new InvalidDataException("В файле нет таблицы (content.xml).");
        if (entry.Length > MaxContentBytes) throw new InvalidDataException("Таблица внутри файла слишком большая.");
        using var content = entry.Open();
        var sheets = Read(content, progress, cancel);
        return new Workbook(path, TableFormat.OpenDocument, sheets);
    }

    internal static List<Sheet> Read(Stream content, IProgress<long>? progress, CancellationToken cancel)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
            CloseInput = false,
        };
        using var xml = XmlReader.Create(content, settings);
        var sheets = new List<Sheet>();
        long total = 0;

        MemorySheet? sheet = null;
        int pendingEmpty = 0;
        var cells = new List<string?>();
        var numbers = new List<double>();
        int column = 0;
        int rowRepeat = 1;
        bool rowHasContent = false;

        while (xml.Read())
        {
            if (xml.NodeType == XmlNodeType.Element && xml.NamespaceURI == TableNs)
            {
                switch (xml.LocalName)
                {
                    case "table":
                        sheet = new MemorySheet(xml.GetAttribute("name", TableNs) ?? $"Лист {sheets.Count + 1}");
                        sheets.Add(sheet);
                        pendingEmpty = 0;
                        break;

                    case "table-row" when sheet is not null:
                        cancel.ThrowIfCancellationRequested();
                        rowRepeat = Repeat(xml.GetAttribute("number-rows-repeated", TableNs), MaxRows);
                        cells.Clear();
                        numbers.Clear();
                        column = 0;
                        rowHasContent = false;
                        if (xml.IsEmptyElement) EndRow();
                        break;

                    case "table-cell" or "covered-table-cell" when sheet is not null:
                        ReadCell();
                        break;
                }
            }
            else if (xml.NodeType == XmlNodeType.EndElement && xml.NamespaceURI == TableNs)
            {
                if (xml.LocalName == "table-row" && sheet is not null) EndRow();
                else if (xml.LocalName == "table" && sheet is not null)
                {
                    sheet.TrimEnd();
                    sheet = null;
                }
            }
        }
        progress?.Report(total);
        return sheets;

        void ReadCell()
        {
            int repeat = Repeat(xml.GetAttribute("number-columns-repeated", TableNs), MaxColumns);
            int spanColumns = Repeat(xml.GetAttribute("number-columns-spanned", TableNs), MaxColumns);
            int spanRows = Repeat(xml.GetAttribute("number-rows-spanned", TableNs), MaxRows);
            string? type = xml.GetAttribute("value-type", OfficeNs);
            double number = double.NaN;
            string? raw = null;
            switch (type)
            {
                case "float" or "percentage" or "currency":
                    raw = xml.GetAttribute("value", OfficeNs);
                    if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out number)) number = double.NaN;
                    break;
                case "date":
                    raw = xml.GetAttribute("date-value", OfficeNs);
                    if (DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) number = date.ToOADate();
                    break;
                case "time":
                    raw = xml.GetAttribute("time-value", OfficeNs);
                    try { if (raw is not null) number = XmlConvert.ToTimeSpan(raw).TotalDays; }
                    catch (FormatException) { }
                    break;
                case "boolean":
                    raw = xml.GetAttribute("boolean-value", OfficeNs);
                    break;
            }

            string? text = xml.IsEmptyElement ? null : CellText();
            if (string.IsNullOrEmpty(text)) text = raw;

            int row = sheet!.RowCount + pendingEmpty;
            if ((spanColumns > 1 || spanRows > 1) && column < MaxColumns)
                sheet.AddMerge(new CellRange(row, column, row + spanRows - 1, Math.Min(column + spanColumns, MaxColumns) - 1));

            if (text is null)
            {
                column = (int)Math.Min((long)column + repeat, MaxColumns);
                return;
            }
            rowHasContent = true;
            for (int i = 0; i < repeat && column < MaxColumns; i++, column++)
            {
                while (cells.Count < column)
                {
                    cells.Add(null);
                    numbers.Add(double.NaN);
                }
                cells.Add(text);
                numbers.Add(number);
            }
        }

        // The text of the cell: its paragraphs on separate lines, with ODF's coded spaces, tabs and breaks.
        string CellText()
        {
            var text = new StringBuilder();
            int paragraphs = 0;
            using var cell = xml.ReadSubtree();
            cell.Read(); // the cell element itself
            bool more = cell.Read();
            while (more && !cell.EOF)
            {
                if (cell.NodeType == XmlNodeType.Element && cell.NamespaceURI == OfficeNs && cell.LocalName == "annotation")
                {
                    // A comment on the cell, not its value.
                    cell.Skip();
                    continue;
                }
                if (cell.NodeType == XmlNodeType.Element && cell.NamespaceURI == TextNs)
                {
                    switch (cell.LocalName)
                    {
                        case "p" or "h":
                            if (paragraphs++ > 0) text.Append('\n');
                            break;
                        case "s":
                            text.Append(' ', Repeat(cell.GetAttribute("c", TextNs), 1000));
                            break;
                        case "tab":
                            text.Append('\t');
                            break;
                        case "line-break":
                            text.Append('\n');
                            break;
                    }
                }
                else if (cell.NodeType is XmlNodeType.Text or XmlNodeType.SignificantWhitespace or XmlNodeType.Whitespace
                         && cell.Depth > 1)
                {
                    // Depth > 1: inside a paragraph, not the layout whitespace between paragraphs.
                    text.Append(cell.Value);
                }
                more = cell.Read();
            }
            return text.ToString();
        }

        void EndRow()
        {
            if (!rowHasContent)
            {
                pendingEmpty = (int)Math.Min((long)pendingEmpty + rowRepeat, MaxRows);
                return;
            }
            var s = sheet!;
            for (int i = 0; i < pendingEmpty && s.RowCount < MaxRows; i++) s.AddRow([], null);
            pendingEmpty = 0;
            var texts = cells.ToArray();
            var values = numbers.ToArray();
            bool anyNumber = Array.Exists(values, v => !double.IsNaN(v));
            for (int i = 0; i < rowRepeat && s.RowCount < MaxRows; i++)
            {
                s.AddRow(i == 0 ? texts : (string?[])texts.Clone(), anyNumber ? values : null);
                if ((++total & 0x3FF) == 0) progress?.Report(total);
            }
            rowHasContent = false;
        }
    }

    private static int Repeat(string? value, int max) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int n) && n > 0 ? Math.Min(n, max) : 1;
}
