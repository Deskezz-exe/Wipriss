using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using Wipriss.Reader.Data;
using Wipriss.Reader.View;

namespace Wipriss.Tests;

public class TablesTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "wipriss-tables-tests-" + Guid.NewGuid().ToString("N"));
    private readonly CultureInfo _culture = CultureInfo.CurrentCulture;

    public TablesTests()
    {
        Directory.CreateDirectory(_folder);
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        CultureInfo.CurrentCulture = new CultureInfo("ru-RU");
    }

    public void Dispose()
    {
        CultureInfo.CurrentCulture = _culture;
        try { Directory.Delete(_folder, recursive: true); }
        catch (IOException) { }
    }

    private string WriteFile(string name, byte[] bytes)
    {
        string path = Path.Combine(_folder, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static void Cells(string?[] expected, IEnumerable<string?> actual) => Assert.Equal<string?>(expected.AsEnumerable(), actual);

    // ---- CSV rows ----

    [Fact]
    public void ParseRow_QuotesDelimitersAndStrayQuotes()
    {
        Cells(["a", "b;c", "say \"hi\""], CsvFormat.ParseRow("a;\"b;c\";\"say \"\"hi\"\"\"", ';'));
        // A quote in the middle of an unquoted field is just a character.
        Cells(["Ноутбук 15\"", "2"], CsvFormat.ParseRow("Ноутбук 15\";2", ';'));
        // Empty fields stay in place, trailing ones are dropped.
        Cells(["a", null, "c"], CsvFormat.ParseRow("a,,c,,", ','));
        Assert.Empty(CsvFormat.ParseRow("", ','));
    }

    [Fact]
    public void ReadRows_LineBreaksInsideQuotesAndAllLineEnds()
    {
        string text = "h1;h2\r\n1;\"две\r\nстроки\"\n2;x\r3;y";
        var rows = CsvFormat.ReadRows(new StringReader(text), ';').ToList();
        Assert.Equal(4, rows.Count);
        Assert.Equal("две\r\nстроки", rows[1][1]);
        Cells(["3", "y"], rows[3]);
    }

    // ---- Guessing the encoding and the delimiter ----

    [Fact]
    public void DetectEncoding_BomUtf8AndRussianCodePages()
    {
        const string russian = "Город;Количество;Комментарий\nМосква;10;отгрузка прошла нормально\nКазань;3;ожидаем поставщика\n";
        Assert.Equal((65001, 3), Info(CsvFormat.DetectEncoding([0xEF, 0xBB, 0xBF, (byte)'a'])));
        Assert.Equal(1200, CsvFormat.DetectEncoding([0xFF, 0xFE, (byte)'a', 0]).Encoding.CodePage);
        Assert.Equal(65001, CsvFormat.DetectEncoding(Encoding.UTF8.GetBytes(russian)).Encoding.CodePage);
        Assert.Equal(1251, CsvFormat.DetectEncoding(Encoding.GetEncoding(1251).GetBytes(russian)).Encoding.CodePage);
        Assert.Equal(866, CsvFormat.DetectEncoding(Encoding.GetEncoding(866).GetBytes(russian)).Encoding.CodePage);
        Assert.Equal(20866, CsvFormat.DetectEncoding(Encoding.GetEncoding(20866).GetBytes(russian)).Encoding.CodePage);
        Assert.Equal(1252, CsvFormat.DetectEncoding(Encoding.GetEncoding(1252).GetBytes("Straße;Größe;café crème\n")).Encoding.CodePage);
        // A UTF-8 letter cut in half at the end of the sample is still UTF-8.
        var cut = Encoding.UTF8.GetBytes("Москва;Казань");
        Assert.Equal(65001, CsvFormat.DetectEncoding(cut.AsSpan(0, cut.Length - 1)).Encoding.CodePage);

        static (int, int) Info((Encoding Encoding, int Bom) found) => (found.Encoding.CodePage, found.Bom);
    }

    [Fact]
    public void DetectDelimiter_PicksTheConsistentOne()
    {
        // Russian Excel: semicolons, decimal commas.
        Assert.Equal(';', CsvFormat.DetectDelimiter("Товар;Цена;Кол-во\nЧайник;1 234,50;2\nКружка;99,90;10\nЛампа;450,00;1\n", ".csv"));
        Assert.Equal(',', CsvFormat.DetectDelimiter("name,age,city\nAnna,31,Berlin\n\"Smith, John\",28,\"New York\"\n", ".csv"));
        Assert.Equal('\t', CsvFormat.DetectDelimiter("a\tb\tc\n1\t2\t3\n4\t5\t6\n", ".txt"));
        Assert.Equal('|', CsvFormat.DetectDelimiter("a|b\n1|2\n3|4\n", ".csv"));
        Assert.Equal('\t', CsvFormat.DetectDelimiter("one column\nonly\n", ".tsv"));
        Assert.Equal((';', 7), CsvFormat.SepLine("sep=;\r\na;b"));
        Assert.Equal(((char?)null, 0), CsvFormat.SepLine("separator;b"));
    }

    // ---- CSV files ----

    [Fact]
    public void CsvFile_1251_IndexedRowsMatchStreamedRows()
    {
        string text = "sep=;\r\nНомер;Город;Комментарий\r\n1;Москва;\"две\r\nстроки; и точка с запятой\"\r\n2;Казань;\r\n\r\n3;Пермь;конец";
        string path = WriteFile("продажи.csv", Encoding.GetEncoding(1251).GetBytes(text));
        using var book = Workbook.Load(path, null, null, CancellationToken.None);

        Assert.Equal(TableFormat.Csv, book.Format);
        Assert.Equal(1251, book.Csv!.Encoding.CodePage);
        Assert.Equal(';', book.Csv.Delimiter);
        var sheet = Assert.IsType<CsvSheet>(Assert.IsType<EditedSheet>(Assert.Single(book.Sheets)).Inner);
        Assert.Equal(5, sheet.RowCount); // header, 1, 2, empty line, 3
        Assert.Equal(3, sheet.ColumnCount);
        Assert.Equal("Город", sheet.Text(0, 1));
        Assert.Equal("две\r\nстроки; и точка с запятой", sheet.Text(1, 2));
        Assert.Null(sheet.Text(3, 0));
        Assert.Equal("Пермь", sheet.Text(4, 1));
        Assert.True(sheet.TryNumber(4, 0, out double three) && three == 3);
        Assert.False(sheet.TryNumber(4, 1, out _));

        var streamed = sheet.Rows(CancellationToken.None).ToList();
        Assert.Equal(sheet.RowCount, streamed.Count);
        for (int r = 0; r < sheet.RowCount; r++)
            for (int c = 0; c < sheet.ColumnCount; c++)
                Assert.Equal(sheet.Text(r, c), c < streamed[r].Length ? streamed[r][c] : null);
    }

    [Fact]
    public void CsvFile_UnclosedQuoteIsReadAsPlainText()
    {
        string path = WriteFile("broken.csv", Encoding.UTF8.GetBytes("a,b\n\"oops,1\n2,3\n4,5\n"));
        using var book = Workbook.Load(path, null, null, CancellationToken.None);
        var sheet = Assert.IsType<CsvSheet>(Assert.IsType<EditedSheet>(book.Sheets[0]).Inner);
        Assert.True(sheet.QuotesIgnored);
        Assert.Equal(4, sheet.RowCount);
        Assert.Equal("4", sheet.Text(3, 0));
    }

    [Fact]
    public void CsvFile_ChosenSettingsWin_AndUtf16IsReadWhole()
    {
        string path = WriteFile("data.csv", Encoding.UTF8.GetBytes("a;b,c\n1;2,3\n"));
        using (var book = Workbook.Load(path, new CsvSettings(new UTF8Encoding(false), ','), null, CancellationToken.None))
            Assert.Equal("a;b", book.Sheets[0].Text(0, 0));

        var utf16 = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("Имя\tВозраст\r\nАнна\t31\r\n")).ToArray();
        string unicode = WriteFile("unicode.txt", utf16);
        using var text = Workbook.Load(unicode, null, null, CancellationToken.None);
        Assert.Equal('\t', text.Csv!.Delimiter);
        Assert.Equal("Анна", text.Sheets[0].Text(1, 0));
        Assert.True(text.Sheets[0].TryNumber(1, 1, out double age) && age == 31);
    }

    // ---- Numbers ----

    [Theory]
    [InlineData("1234", 1234)]
    [InlineData("-1 234,5", -1234.5)]
    [InlineData("1,234.5", 1234.5)]
    [InlineData("1.234,5", 1234.5)]
    [InlineData("3,5 ₽", 3.5)]
    [InlineData("12%", 0.12)]
    [InlineData("1 000", 1000)]
    [InlineData("1e3", 1000)]
    [InlineData("−7", -7)]
    public void LooseNumbers(string text, double expected)
    {
        Assert.True(Sheet.TryParseLooseNumber(text, out double value), text);
        Assert.Equal(expected, value, 9);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("10.02.2026")]
    [InlineData("1 2 3a")]
    [InlineData("+7 (999) 123-45-67")]
    [InlineData("-")]
    public void NotNumbers(string text) => Assert.False(Sheet.TryParseLooseNumber(text, out _), text);

    [Fact]
    public void ColumnNames() =>
        Assert.Equal(["A", "Z", "AA", "AZ", "BA", "XFD"], new[] { 0, 25, 26, 51, 52, 16383 }.Select(Sheet.ColumnName));

    // ---- OpenDocument ----

    [Fact]
    public void Ods_RepeatsSpansSpacesAndComments()
    {
        const string content = """
            <?xml version="1.0" encoding="UTF-8"?>
            <office:document-content xmlns:office="urn:oasis:names:tc:opendocument:xmlns:office:1.0"
                xmlns:table="urn:oasis:names:tc:opendocument:xmlns:table:1.0" xmlns:text="urn:oasis:names:tc:opendocument:xmlns:text:1.0">
             <office:body><office:spreadsheet>
              <table:table table:name="Отчёт">
               <table:table-row>
                <table:table-cell table:number-columns-spanned="3" office:value-type="string"><text:p>Итоги<text:s text:c="2"/>года</text:p></table:table-cell>
                <table:covered-table-cell table:number-columns-repeated="2"/>
               </table:table-row>
               <table:table-row table:number-rows-repeated="2"><table:table-cell table:number-columns-repeated="1024"/></table:table-row>
               <table:table-row>
                <table:table-cell office:value-type="float" office:value="1234.5"><text:p>1 234,50</text:p></table:table-cell>
                <table:table-cell table:number-columns-repeated="2" office:value-type="string"><text:p>да</text:p></table:table-cell>
                <table:table-cell office:value-type="string"><office:annotation><text:p>заметка</text:p></office:annotation><text:p>первая</text:p><text:p>вторая</text:p></table:table-cell>
               </table:table-row>
               <table:table-row table:number-rows-repeated="1048000"><table:table-cell table:number-columns-repeated="16384"/></table:table-row>
              </table:table>
             </office:spreadsheet></office:body>
            </office:document-content>
            """;
        var sheets = OdsLoader.Read(new MemoryStream(Encoding.UTF8.GetBytes(content)), null, CancellationToken.None);
        var sheet = Assert.Single(sheets);
        Assert.Equal("Отчёт", sheet.Name);
        Assert.Equal(4, sheet.RowCount); // the trailing empty rows are dropped
        Assert.Equal(4, sheet.ColumnCount);
        Assert.Equal("Итоги  года", sheet.Text(0, 0)); // text:s c="2" is two spaces
        Assert.Equal(new CellRange(0, 0, 0, 2), Assert.Single(sheet.Merges));
        Assert.Null(sheet.Text(1, 0));
        Assert.Equal("1 234,50", sheet.Text(3, 0));
        Assert.True(sheet.TryNumber(3, 0, out double n) && n == 1234.5);
        Assert.Equal("да", sheet.Text(3, 1));
        Assert.Equal("да", sheet.Text(3, 2));
        Assert.Equal("первая\nвторая", sheet.Text(3, 3));
    }

    [Fact]
    public void Ods_RefusesDtd()
    {
        const string content = """<?xml version="1.0"?><!DOCTYPE x [<!ENTITY a "b">]><x>&a;</x>""";
        Assert.Throws<System.Xml.XmlException>(() => OdsLoader.Read(new MemoryStream(Encoding.UTF8.GetBytes(content)), null, CancellationToken.None));
    }

    // ---- Excel ----

    [Fact]
    public void Xlsx_ValuesFormatsDatesAndMerges()
    {
        string path = Path.Combine(_folder, "отчёт.xlsx");
        WriteXlsx(path);
        using var book = Workbook.Load(path, null, null, CancellationToken.None);
        Assert.Equal(TableFormat.Excel, book.Format);
        Assert.Equal(2, book.Sheets.Count);
        var sheet = book.Sheets[0];
        Assert.Equal("Продажи", sheet.Name);
        Assert.False(sheet.Hidden);
        Assert.True(book.Sheets[1].Hidden);

        Assert.Equal("Товар", sheet.Text(0, 0));
        Assert.Equal("Цена", sheet.Text(0, 1));
        Assert.Equal("Чайник", sheet.Text(1, 0));
        Assert.Equal("1 234,50", sheet.Text(1, 1)?.Replace(' ', ' '));
        Assert.True(sheet.TryNumber(1, 1, out double price) && price == 1234.5);
        Assert.Equal("01.03.2025", sheet.Text(1, 2));
        Assert.True(sheet.TryNumber(1, 2, out double date) && DateTime.FromOADate(date) == new DateTime(2025, 3, 1));
        Assert.Equal("ИСТИНА", sheet.Text(1, 3));
        Assert.Equal("#DIV/0!", sheet.Text(2, 1));
        Assert.Equal("Итого", sheet.Text(3, 0));
        Assert.Equal(new CellRange(3, 0, 3, 2), Assert.Single(sheet.Merges));
        Assert.Equal(4, sheet.RowCount);
        Assert.Equal(4, sheet.ColumnCount);
    }

    /// <summary>The smallest XLSX Excel accepts: two sheets (one hidden), shared strings, styles, a merge.</summary>
    private static void WriteXlsx(string path)
    {
        var parts = new Dictionary<string, string>
        {
            ["[Content_Types].xml"] = """
                <?xml version="1.0" encoding="UTF-8"?>
                <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
                <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
                <Default Extension="xml" ContentType="application/xml"/>
                <Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/>
                <Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>
                <Override PartName="/xl/worksheets/sheet2.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>
                <Override PartName="/xl/sharedStrings.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sharedStrings+xml"/>
                <Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/>
                </Types>
                """,
            ["_rels/.rels"] = """
                <?xml version="1.0" encoding="UTF-8"?>
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/>
                </Relationships>
                """,
            ["xl/workbook.xml"] = """
                <?xml version="1.0" encoding="UTF-8"?>
                <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
                <sheets><sheet name="Продажи" sheetId="1" r:id="rId1"/><sheet name="Служебный" sheetId="2" state="hidden" r:id="rId2"/></sheets>
                </workbook>
                """,
            ["xl/_rels/workbook.xml.rels"] = """
                <?xml version="1.0" encoding="UTF-8"?>
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/>
                <Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet2.xml"/>
                <Relationship Id="rId3" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/sharedStrings" Target="sharedStrings.xml"/>
                <Relationship Id="rId4" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/>
                </Relationships>
                """,
            ["xl/sharedStrings.xml"] = """
                <?xml version="1.0" encoding="UTF-8"?>
                <sst xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
                <si><t>Товар</t></si><si><t>Цена</t></si><si><t>Чайник</t></si><si><t>Итого</t></si>
                </sst>
                """,
            ["xl/styles.xml"] = """
                <?xml version="1.0" encoding="UTF-8"?>
                <styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
                <fonts count="1"><font/></fonts><fills count="1"><fill/></fills><borders count="1"><border/></borders>
                <cellStyleXfs count="1"><xf/></cellStyleXfs>
                <cellXfs count="3"><xf numFmtId="0"/><xf numFmtId="4" applyNumberFormat="1"/><xf numFmtId="14" applyNumberFormat="1"/></cellXfs>
                </styleSheet>
                """,
            ["xl/worksheets/sheet1.xml"] = """
                <?xml version="1.0" encoding="UTF-8"?>
                <worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
                <sheetData>
                <row r="1"><c r="A1" t="s"><v>0</v></c><c r="B1" t="s"><v>1</v></c></row>
                <row r="2"><c r="A2" t="s"><v>2</v></c><c r="B2" s="1"><v>1234.5</v></c><c r="C2" s="2"><v>45717</v></c><c r="D2" t="b"><v>1</v></c></row>
                <row r="3"><c r="B3" t="e"><f>1/0</f><v>#DIV/0!</v></c></row>
                <row r="4"><c r="A4" t="s"><v>3</v></c></row>
                <row r="9"><c r="E9" s="1"/></row>
                </sheetData>
                <mergeCells count="1"><mergeCell ref="A4:C4"/></mergeCells>
                </worksheet>
                """,
            ["xl/worksheets/sheet2.xml"] = """
                <?xml version="1.0" encoding="UTF-8"?>
                <worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData><row r="1"><c r="A1"><v>1</v></c></row></sheetData></worksheet>
                """,
        };
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, xml) in parts)
        {
            using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
            writer.Write(xml);
        }
    }

    // ---- The view: header guess, sorting, searching ----

    private static MemorySheet Sample()
    {
        var sheet = new MemorySheet("Лист");
        sheet.AddRow(["Город", "Сумма"], null);
        sheet.AddRow(["Самара", "10"], [double.NaN, 10]);
        sheet.AddRow(["казань", "9"], [double.NaN, 9]);
        sheet.AddRow(["Москва", null], null);
        sheet.AddRow(["Пермь", "нет"], null);
        sheet.AddRow(["Москва 2", "100"], [double.NaN, 100]);
        sheet.AddRow(["Москва 10", "-1"], [double.NaN, -1]);
        return sheet;
    }

    [Fact]
    public void Header_GuessedFromTextAboveNumbers()
    {
        Assert.Equal(1, TableView.GuessPinnedRows(Sample()));
        var numbers = new MemorySheet("n");
        numbers.AddRow(["1", "2"], [1, 2]);
        numbers.AddRow(["3", "4"], [3, 4]);
        numbers.AddRow(["5", "6"], [5, 6]);
        Assert.Equal(0, TableView.GuessPinnedRows(numbers));

        // A report: a title, an empty row, then the header. All three stay on top.
        var report = new MemorySheet("r");
        report.AddRow(["Отчёт за год"], null);
        report.AddRow([], null);
        report.AddRow(["Город", "Сумма"], null);
        report.AddRow(["Самара", "10"], [double.NaN, 10]);
        report.AddRow(["Казань", "9"], [double.NaN, 9]);
        Assert.Equal(3, TableView.GuessPinnedRows(report));
        var view = new TableView(report, 3);
        Assert.Equal(2, view.RowCount);
        Assert.Equal(0, view.SheetRow(-3));
        Assert.Equal(2, view.SheetRow(-1));
        Assert.Equal(3, view.SheetRow(0));
        Assert.Equal(-2, view.ViewRow(1));
    }

    [Fact]
    public void Sort_NumbersThenTextThenEmpty_HeaderStaysPinned()
    {
        var view = new TableView(Sample(), 1);
        var order = view.ComputeOrder(1, descending: false, onlyMatches: false, null, CancellationToken.None);
        view.Apply(1, false, false, order);
        Assert.Equal(["-1", "9", "10", "100", "нет", null], Enumerable.Range(0, view.RowCount).Select(r => view.Sheet.Text(view.SheetRow(r), 1)));
        Assert.Equal(0, view.SheetRow(-1));

        // Down: numbers reversed, empty cells still last.
        order = view.ComputeOrder(1, descending: true, onlyMatches: false, null, CancellationToken.None);
        view.Apply(1, true, false, order);
        Assert.Equal(["100", "10", "9", "-1", "нет", null], Enumerable.Range(0, view.RowCount).Select(r => view.Sheet.Text(view.SheetRow(r), 1)));

        // Text: case does not matter, numbers inside text in their natural order.
        order = view.ComputeOrder(0, descending: false, onlyMatches: false, null, CancellationToken.None);
        view.Apply(0, false, false, order);
        Assert.Equal(["казань", "Москва", "Москва 2", "Москва 10", "Пермь", "Самара"],
            Enumerable.Range(0, view.RowCount).Select(r => view.Sheet.Text(view.SheetRow(r), 0)));
        Assert.False(view.KeepsFileOrder);
    }

    [Fact]
    public void Search_FindsAnyCase_AndFiltersRows()
    {
        var sheet = Sample();
        var (matches, count) = TableView.Find(sheet, "москв", 100, CancellationToken.None);
        Assert.Equal(3, count);
        Assert.Equal([3, 5, 6], matches.Keys.Order());

        var view = new TableView(sheet, 1);
        view.SetMatches(matches, count);
        Assert.True(view.IsMatch(5, 0));
        Assert.False(view.IsMatch(5, 1));
        var order = view.ComputeOrder(-1, false, onlyMatches: true, matches, CancellationToken.None);
        view.Apply(-1, false, true, order);
        Assert.Equal(3, view.RowCount);
        Assert.Equal(5, view.SheetRow(1));
        Assert.Equal(int.MinValue, view.ViewRow(1));

        // The limit stops the search early.
        Assert.Equal(1, TableView.Find(sheet, "а", 1, CancellationToken.None).Count);
    }
}
