using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Wipriss.Reader.Data;

/// <summary>Some changed cells can't be saved: the file does not let change them safely.</summary>
internal sealed class LockedCellsException(string message) : Exception(message);

/// <summary>
/// Saves changed cells into an XLSX/XLSM by editing the sheet's XML inside the package. Everything else in the file
/// (charts, pivot tables, pictures, macros, comments, other sheets) is copied untouched. A changed cell keeps
/// its style; text is stored right in the cell, numbers as numbers, "=…" as a formula. Excel recalculates the
/// formulas when it opens the file next time (the stored calculation order is dropped for that). A cell with a new
/// look gets a new style added after the existing ones (<see cref="XlsxStyleBook"/>); merged areas are rewritten.
/// </summary>
internal static class XlsxWriter
{
    /// <param name="output">A copy of the source to change in place (the caller makes it).</param>
    public static void Write(string source, string output, IEnumerable<EditedSheet> sheets, CultureInfo culture, CancellationToken cancel)
    {
        var changed = sheets.Where(s => s.ChangeCount > 0).ToList();
        if (changed.Count == 0) return;

        using var original = new ZipArchive(Workbook.OpenShared(source), ZipArchiveMode.Read);
        var parts = new XlsxParts(original);
        var book = XlsxStyleBook.Load(parts);

        var plan = new List<(string Path, SortedDictionary<int, SortedDictionary<int, CellEdit>> Rows, IReadOnlyList<CellRange>? Merges)>();
        foreach (var sheet in changed)
        {
            if (!parts.SheetPaths.TryGetValue(sheet.Name, out var path) || parts.Entry(path) is null)
                throw new InvalidDataException($"В файле не нашёлся лист «{sheet.Name}».");
            // The file may have changed since it was opened: check the locks again on what is there now.
            var locks = parts.Locks(path);
            var bad = sheet.Edits.Keys.Where(locks.ContainsKey).Take(6).Select(k => CellRef.Format(k.Row, k.Column)).ToList();
            if (bad.Count > 0)
                throw new LockedCellsException($"Эти ячейки можно изменить только в Excel: {string.Join(", ", bad)}. Верните их (Ctrl+Z) и сохраните снова.");
            var rows = new SortedDictionary<int, SortedDictionary<int, CellEdit>>();
            foreach (var ((row, column), text) in sheet.Edits)
            {
                if (!rows.TryGetValue(row, out var cells)) rows[row] = cells = [];
                cells[column] = new CellEdit(true, text, null);
            }
            foreach (var ((row, column), format) in sheet.FormatEdits)
            {
                if (!rows.TryGetValue(row, out var cells)) rows[row] = cells = [];
                cells[column] = cells.TryGetValue(column, out var edit) ? edit with { Format = format } : new CellEdit(false, null, format);
            }
            plan.Add((path, rows, sheet.MergesChanged ? sheet.Merges : null));
        }

        using var package = new ZipArchive(new FileStream(output, FileMode.Open, FileAccess.ReadWrite, FileShare.None), ZipArchiveMode.Update);
        foreach (var (path, rows, merges) in plan)
        {
            cancel.ThrowIfCancellationRequested();
            var from = parts.Entry(path)!;
            var target = package.GetEntry(from.FullName)!;
            var time = target.LastWriteTime;
            target.Delete();
            var entry = package.CreateEntry(from.FullName, CompressionLevel.Optimal);
            entry.LastWriteTime = time;
            using var input = XmlReader.Create(from.Open(), XlsxParts.ReaderSettings);
            using var writer = XmlWriter.Create(entry.Open(), new XmlWriterSettings
            {
                Encoding = new UTF8Encoding(false),
                NewLineHandling = NewLineHandling.None,
                CloseOutput = true,
            });
            new SheetRewriter(input, writer, rows, merges, book, culture, cancel).Run();
        }
        book.Save(package, parts);
        RecalculateOnOpen(package, parts);
    }

    /// <summary>Drops the stored calculation order and asks Excel to recalculate everything on open.</summary>
    private static void RecalculateOnOpen(ZipArchive package, XlsxParts parts)
    {
        if (parts.CalcChainPath is { } chain)
        {
            package.GetEntry(chain)?.Delete();
            Rewrite(package, XlsxParts.RelsPath(parts.WorkbookPath), doc =>
                doc.Root?.Elements().Where(e => e.Name.LocalName == "Relationship"
                    && ((string?)e.Attribute("Type"))?.EndsWith("/calcChain", StringComparison.Ordinal) == true).Remove());
            Rewrite(package, "[Content_Types].xml", doc =>
                doc.Root?.Elements().Where(e => e.Name.LocalName == "Override"
                    && string.Equals(((string?)e.Attribute("PartName"))?.TrimStart('/'), chain, StringComparison.OrdinalIgnoreCase)).Remove());
        }
        Rewrite(package, parts.WorkbookPath, doc =>
        {
            if (doc.Root is not { } root) return;
            var ns = root.Name.Namespace;
            var calc = root.Element(ns + "calcPr");
            if (calc is null)
            {
                calc = new XElement(ns + "calcPr");
                // calcPr comes right after these in a workbook, before everything else that may follow.
                var before = root.Elements().LastOrDefault(e => e.Name.LocalName is "sheets" or "functionGroups" or "externalReferences" or "definedNames");
                if (before is not null) before.AddAfterSelf(calc);
                else root.Add(calc);
            }
            calc.SetAttributeValue("fullCalcOnLoad", "1");
        });
    }

    private static void Rewrite(ZipArchive package, string path, Action<XDocument> change)
    {
        if (package.GetEntry(path) is not { } entry) return;
        XDocument doc;
        using (var reader = XmlReader.Create(entry.Open(), XlsxParts.ReaderSettings))
            doc = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
        change(doc);
        using var stream = entry.Open();
        stream.SetLength(0);
        using var writer = XmlWriter.Create(stream, new XmlWriterSettings { Encoding = new UTF8Encoding(false), NewLineHandling = NewLineHandling.None });
        doc.Save(writer);
    }

    /// <summary>Copies a sheet's XML node by node; rows with changes are read whole, changed and written back.</summary>
    private sealed class SheetRewriter(XmlReader reader, XmlWriter writer, SortedDictionary<int, SortedDictionary<int, CellEdit>> edits,
        IReadOnlyList<CellRange>? merges, XlsxStyleBook styles, CultureInfo culture, CancellationToken cancel)
    {
        /// <summary>Parts of a sheet that come after the merged cells; new merges go before the first of them.</summary>
        private static readonly HashSet<string> AfterMerges =
        [
            "phoneticPr", "conditionalFormatting", "dataValidations", "hyperlinks", "printOptions", "pageMargins", "pageSetup",
            "headerFooter", "rowBreaks", "colBreaks", "customProperties", "cellWatches", "ignoredErrors", "smartTags", "drawing",
            "legacyDrawing", "legacyDrawingHF", "drawingHF", "picture", "oleObjects", "controls", "webPublishItems", "tableParts", "extLst",
        ];

        private XNamespace _ns = XNamespace.None;
        private bool _mergesWritten;

        public void Run()
        {
            reader.Read();
            while (!reader.EOF)
            {
                if (reader.NodeType == XmlNodeType.Element && reader.Depth == 0) _ns = reader.NamespaceURI;
                if (reader.NodeType == XmlNodeType.Element && reader.Depth == 1 && reader.NamespaceURI == _ns.NamespaceName)
                {
                    if (reader.LocalName == "sheetData")
                    {
                        SheetData();
                        continue;
                    }
                    if (reader.LocalName == "dimension")
                    {
                        var dimension = (XElement)XNode.ReadFrom(reader);
                        Grow(dimension);
                        dimension.WriteTo(writer);
                        continue;
                    }
                    if (merges is not null && reader.LocalName == "mergeCells")
                    {
                        WriteMerges();
                        reader.Skip();
                        continue;
                    }
                    if (merges is not null && AfterMerges.Contains(reader.LocalName)) WriteMerges();
                }
                if (merges is not null && reader.NodeType == XmlNodeType.EndElement && reader.Depth == 0) WriteMerges();
                CopyNode();
                reader.Read();
            }
        }

        private void WriteMerges()
        {
            if (_mergesWritten || merges is null) return;
            _mergesWritten = true;
            if (merges.Count == 0) return;
            writer.WriteStartElement("mergeCells", _ns.NamespaceName);
            writer.WriteAttributeString("count", merges.Count.ToString(CultureInfo.InvariantCulture));
            foreach (var m in merges)
            {
                writer.WriteStartElement("mergeCell", _ns.NamespaceName);
                writer.WriteAttributeString("ref", $"{CellRef.Format(m.FromRow, m.FromColumn)}:{CellRef.Format(m.ToRow, m.ToColumn)}");
                writer.WriteEndElement();
            }
            writer.WriteEndElement();
        }

        private void SheetData()
        {
            int depth = reader.Depth;
            bool empty = reader.IsEmptyElement;
            CopyStart(endIfEmpty: false);
            var pending = new Queue<int>(edits.Keys);
            if (!empty)
            {
                reader.Read();
                int lastRow = -1;
                while (!(reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth) && !reader.EOF)
                {
                    if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "row")
                    {
                        cancel.ThrowIfCancellationRequested();
                        var row = (XElement)XNode.ReadFrom(reader);
                        int index = CellRef.ParseRow((string?)row.Attribute("r")) ?? lastRow + 1;
                        lastRow = index;
                        while (pending.Count > 0 && pending.Peek() < index) NewRow(pending.Dequeue()).WriteTo(writer);
                        if (pending.Count > 0 && pending.Peek() == index)
                        {
                            pending.Dequeue();
                            row.SetAttributeValue("r", index + 1);
                            Change(row, edits[index]);
                        }
                        row.WriteTo(writer);
                        continue;
                    }
                    CopyNode();
                    reader.Read();
                }
                reader.Read(); // past </sheetData>
            }
            else reader.Read();
            while (pending.Count > 0) NewRow(pending.Dequeue()).WriteTo(writer);
            writer.WriteEndElement();
        }

        private XElement NewRow(int index)
        {
            var row = new XElement(_ns + "row", new XAttribute("r", index + 1));
            Change(row, edits[index]);
            return row;
        }

        private void Change(XElement row, SortedDictionary<int, CellEdit> cells)
        {
            var ns = _ns;
            int rowIndex = CellRef.ParseRow((string?)row.Attribute("r")) ?? 0;
            var byColumn = new SortedDictionary<int, XElement>();
            int last = -1;
            foreach (var cell in row.Elements(ns + "c"))
            {
                int column = CellRef.Parse((string?)cell.Attribute("r"))?.Column ?? last + 1;
                last = column;
                byColumn[column] = cell;
                // Cells may leave out their address (it is then the next column): new cells around need them.
                if (cell.Attribute("r") is null) cell.SetAttributeValue("r", CellRef.Format(rowIndex, column));
            }
            bool added = false, wrapChanged = false;
            foreach (var (column, edit) in cells)
            {
                if (byColumn.TryGetValue(column, out var cell))
                {
                    wrapChanged |= Restyle(cell, edit);
                    if (edit.HasText) SetValue(cell, edit.Text);
                    continue;
                }
                if (edit.Text is null && edit.Format is null) continue;
                cell = new XElement(ns + "c", new XAttribute("r", CellRef.Format(rowIndex, column)));
                wrapChanged |= Restyle(cell, edit);
                if (edit.HasText) SetValue(cell, edit.Text);
                // Cells go in column order.
                var next = byColumn.FirstOrDefault(p => p.Key > column).Value;
                if (next is not null) next.AddBeforeSelf(cell);
                else if (byColumn.Count > 0) byColumn.Values.Last().AddAfterSelf(cell);
                else row.AddFirst(cell);
                byColumn[column] = cell;
                added = true;
            }
            // "spans" is only a hint of the row's width; a wrong one upsets Excel, none is fine.
            if (added) row.Attribute("spans")?.Remove();
            // Wrapping turned on or off: a height Excel worked out itself is dropped so it fits the row again.
            if (wrapChanged && (string?)row.Attribute("customHeight") is not ("1" or "true"))
            {
                row.Attribute("ht")?.Remove();
                row.Attribute("customHeight")?.Remove();
            }
        }

        /// <summary>Gives the cell the style of its new look. Returns true when its wrapping changed.</summary>
        private bool Restyle(XElement cell, CellEdit edit)
        {
            if (edit.Format is not { } look) return false;
            int old = (int?)cell.Attribute("s") ?? 0;
            int style = styles.Style(old, look);
            cell.SetAttributeValue("s", style == 0 ? null : style);
            return (styles.Look(old).Mode == TextMode.Wrap) != (look.Mode == TextMode.Wrap);
        }

        private void SetValue(XElement cell, string? text)
        {
            var ns = _ns;
            if (cell.Element(ns + "f") is { } formula && (string?)formula.Attribute("t") is "array" or "dataTable"
                || cell.Element(ns + "f") is { } shared && (string?)shared.Attribute("t") == "shared" && shared.Attribute("ref") is not null)
                throw new LockedCellsException($"Ячейку {(string?)cell.Attribute("r")} можно изменить только в Excel.");
            int? style = (int?)cell.Attribute("s");
            cell.Attribute("t")?.Remove();
            cell.Attribute("cm")?.Remove();
            cell.Attribute("vm")?.Remove();
            cell.Elements().Where(e => e.Name.LocalName is "f" or "v" or "is").Remove();
            if (text is null) return;

            byte kind = style is { } s ? styles.Kind(s) : (byte)0;
            var value = CellValue.Classify(text, kind, culture);
            XElement content = value.Kind switch
            {
                CellKind.Formula => new XElement(ns + "f", XmlSafe(value.Text)),
                CellKind.Number => new XElement(ns + "v", value.Text),
                CellKind.Bool => new XElement(ns + "v", value.Text),
                _ => new XElement(ns + "is", Preserve(new XElement(ns + "t", XmlSafe(value.Text)))),
            };
            if (value.Kind == CellKind.Bool) cell.SetAttributeValue("t", "b");
            else if (value.Kind == CellKind.Text) cell.SetAttributeValue("t", "inlineStr");
            cell.AddFirst(content);
        }

        private static XElement Preserve(XElement t)
        {
            string value = t.Value;
            if (value.Length > 0 && (char.IsWhiteSpace(value[0]) || char.IsWhiteSpace(value[^1]) || value.Contains('\n')))
                t.SetAttributeValue(XNamespace.Xml + "space", "preserve");
            return t;
        }

        /// <summary>Line breaks as Excel keeps them, and no characters XML can't hold.</summary>
        private static string XmlSafe(string text)
        {
            text = text.Replace("\r\n", "\n").Replace('\r', '\n');
            if (text.Length > 32_767) text = text[..32_767];
            var builder = new StringBuilder(text.Length);
            for (int i = 0; i < text.Length; i++)
            {
                char ch = text[i];
                if (char.IsHighSurrogate(ch) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                {
                    builder.Append(ch).Append(text[++i]);
                }
                else if (XmlConvert.IsXmlChar(ch)) builder.Append(ch);
            }
            return builder.ToString();
        }

        /// <summary>The used range in "dimension" grows to the new cells.</summary>
        private void Grow(XElement dimension)
        {
            int maxRow = -1, maxColumn = -1;
            foreach (var (row, cells) in edits)
            {
                foreach (var (column, edit) in cells)
                {
                    if (edit.Text is null && edit.Format is null) continue;
                    maxRow = Math.Max(maxRow, row);
                    maxColumn = Math.Max(maxColumn, column);
                }
            }
            if (maxRow < 0) return;
            var area = CellRef.ParseRange((string?)dimension.Attribute("ref")) ?? new CellRange(0, 0, 0, 0);
            var grown = new CellRange(area.FromRow, area.FromColumn, Math.Max(area.ToRow, maxRow), Math.Max(area.ToColumn, maxColumn));
            if (grown == area) return;
            dimension.SetAttributeValue("ref", $"{CellRef.Format(grown.FromRow, grown.FromColumn)}:{CellRef.Format(grown.ToRow, grown.ToColumn)}");
        }

        private void CopyStart(bool endIfEmpty)
        {
            writer.WriteStartElement(reader.Prefix, reader.LocalName, reader.NamespaceURI);
            if (reader.MoveToFirstAttribute())
            {
                do writer.WriteAttributeString(reader.Prefix, reader.LocalName, reader.NamespaceURI, reader.Value);
                while (reader.MoveToNextAttribute());
                reader.MoveToElement();
            }
            if (endIfEmpty && reader.IsEmptyElement) writer.WriteEndElement();
        }

        private void CopyNode()
        {
            switch (reader.NodeType)
            {
                case XmlNodeType.Element:
                    CopyStart(endIfEmpty: true);
                    break;
                case XmlNodeType.EndElement:
                    writer.WriteFullEndElement();
                    break;
                case XmlNodeType.Text:
                    writer.WriteString(reader.Value);
                    break;
                case XmlNodeType.CDATA:
                    writer.WriteCData(reader.Value);
                    break;
                case XmlNodeType.Whitespace or XmlNodeType.SignificantWhitespace:
                    writer.WriteWhitespace(reader.Value);
                    break;
                case XmlNodeType.Comment:
                    writer.WriteComment(reader.Value);
                    break;
                case XmlNodeType.ProcessingInstruction:
                    writer.WriteProcessingInstruction(reader.Name, reader.Value);
                    break;
                case XmlNodeType.XmlDeclaration:
                    writer.WriteStartDocument(reader.Value.Contains("standalone=\"yes\"", StringComparison.Ordinal)
                                              || reader.Value.Contains("standalone='yes'", StringComparison.Ordinal));
                    break;
            }
        }
    }
}

/// <summary>What changes in one cell: its text (when <see cref="HasText"/>), its look (when not null), or both.</summary>
internal readonly record struct CellEdit(bool HasText, string? Text, CellFormat? Format);

internal enum CellKind { Text, Number, Bool, Formula }

/// <summary>What a typed text becomes in an Excel cell, close to what Excel itself does when you type it.</summary>
internal readonly record struct CellValue(CellKind Kind, string Text)
{
    /// <param name="styleKind">1 when the cell is formatted as a date or time, 2 when as text ("@").</param>
    public static CellValue Classify(string text, byte styleKind, CultureInfo culture)
    {
        if (text.Length > 1 && text[0] == '=' && styleKind != 2) return new(CellKind.Formula, FormulaText.ToFile(text[1..], culture));
        // An apostrophe in front keeps "007" or "1-2" as text, like in Excel.
        if (text.Length > 1 && text[0] == '\'') return new(CellKind.Text, text[1..]);
        if (styleKind == 2) return new(CellKind.Text, text);
        string trimmed = text.Trim();
        if (trimmed.Equals("ИСТИНА", StringComparison.OrdinalIgnoreCase) || trimmed.Equals("TRUE", StringComparison.OrdinalIgnoreCase))
            return new(CellKind.Bool, "1");
        if (trimmed.Equals("ЛОЖЬ", StringComparison.OrdinalIgnoreCase) || trimmed.Equals("FALSE", StringComparison.OrdinalIgnoreCase))
            return new(CellKind.Bool, "0");
        if (styleKind == 1)
        {
            bool timeOnly = trimmed.Contains(':') && trimmed.IndexOfAny(['.', '/', '-']) < 0 && trimmed.Count(char.IsWhiteSpace) == 0;
            if (timeOnly && TimeSpan.TryParse(trimmed, culture, out var time) && time >= TimeSpan.Zero && time < TimeSpan.FromDays(1))
                return Number(time.TotalDays);
            if (DateTime.TryParse(trimmed, culture, DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.NoCurrentDateDefault, out var date)
                && date.Year >= 1900)
                return Number(date.ToOADate());
        }
        // "0123" and "+7 999…" are codes and phone numbers: keep them as typed.
        if (trimmed.Length > 1 && trimmed[0] == '0' && char.IsAsciiDigit(trimmed[1])) return new(CellKind.Text, text);
        if (trimmed.StartsWith('+')) return new(CellKind.Text, text);
        if (Sheet.TryParseLooseNumber(trimmed, out double number)) return Number(number);
        return new(CellKind.Text, text);
    }

    private static CellValue Number(double value) => new(CellKind.Number, value.ToString("R", CultureInfo.InvariantCulture));
}

/// <summary>
/// Excel keeps formulas in English with commas between arguments; in Russian Excel they are typed as
/// "=СУММ(A1;B1)*1,5". The common Russian function names, ";" and the decimal comma are turned into the file's form.
/// </summary>
internal static class FormulaText
{
    private static readonly Dictionary<string, string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        ["СУММ"] = "SUM", ["СРЗНАЧ"] = "AVERAGE", ["МИН"] = "MIN", ["МАКС"] = "MAX", ["СЧЁТ"] = "COUNT", ["СЧЕТ"] = "COUNT",
        ["СЧЁТЗ"] = "COUNTA", ["СЧЕТЗ"] = "COUNTA", ["СЧЁТЕСЛИ"] = "COUNTIF", ["СЧЕТЕСЛИ"] = "COUNTIF", ["СУММЕСЛИ"] = "SUMIF",
        ["СУММЕСЛИМН"] = "SUMIFS", ["СЧЁТЕСЛИМН"] = "COUNTIFS", ["СЧЕТЕСЛИМН"] = "COUNTIFS", ["ЕСЛИ"] = "IF", ["И"] = "AND",
        ["ИЛИ"] = "OR", ["НЕ"] = "NOT", ["ОКРУГЛ"] = "ROUND", ["ОКРУГЛВВЕРХ"] = "ROUNDUP", ["ОКРУГЛВНИЗ"] = "ROUNDDOWN",
        ["ABS"] = "ABS", ["ВПР"] = "VLOOKUP", ["ГПР"] = "HLOOKUP", ["ИНДЕКС"] = "INDEX", ["ПОИСКПОЗ"] = "MATCH",
        ["СЦЕПИТЬ"] = "CONCATENATE", ["СЦЕП"] = "CONCAT", ["ЛЕВСИМВ"] = "LEFT", ["ПРАВСИМВ"] = "RIGHT", ["ПСТР"] = "MID",
        ["ДЛСТР"] = "LEN", ["СЕГОДНЯ"] = "TODAY", ["ТДАТА"] = "NOW", ["ГОД"] = "YEAR", ["МЕСЯЦ"] = "MONTH", ["ДЕНЬ"] = "DAY",
        ["ЕСЛИОШИБКА"] = "IFERROR", ["ПРОИЗВЕД"] = "PRODUCT", ["ТЕКСТ"] = "TEXT", ["ЗНАЧЕН"] = "VALUE", ["СТЕПЕНЬ"] = "POWER",
        ["КОРЕНЬ"] = "SQRT", ["ЦЕЛОЕ"] = "INT", ["ОСТАТ"] = "MOD", ["СЖПРОБЕЛЫ"] = "TRIM", ["ПРОПИСН"] = "UPPER", ["СТРОЧН"] = "LOWER",
    };

    public static string ToFile(string formula, CultureInfo culture)
    {
        bool commaDecimal = culture.NumberFormat.NumberDecimalSeparator == ",";
        var result = new StringBuilder(formula.Length);
        for (int i = 0; i < formula.Length;)
        {
            char ch = formula[i];
            if (ch is '"' or '\'')
            {
                // Text in quotes and quoted sheet names stay as they are.
                int end = i + 1;
                while (end < formula.Length)
                {
                    if (formula[end] == ch)
                    {
                        if (end + 1 < formula.Length && formula[end + 1] == ch) end += 2;
                        else break;
                    }
                    else end++;
                }
                end = Math.Min(end + 1, formula.Length);
                result.Append(formula, i, end - i);
                i = end;
                continue;
            }
            if (char.IsLetter(ch))
            {
                int end = i;
                while (end < formula.Length && (char.IsLetterOrDigit(formula[end]) || formula[end] is '.' or '_')) end++;
                string word = formula[i..end];
                result.Append(end < formula.Length && formula[end] == '(' && Names.TryGetValue(word, out var english) ? english : word);
                i = end;
                continue;
            }
            if (commaDecimal && ch == ';') result.Append(',');
            else if (commaDecimal && ch == ',' && i > 0 && char.IsAsciiDigit(formula[i - 1]) && i + 1 < formula.Length && char.IsAsciiDigit(formula[i + 1]))
                result.Append('.');
            else result.Append(ch);
            i++;
        }
        return result.ToString();
    }
}
