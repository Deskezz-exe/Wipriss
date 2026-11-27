using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using Wipriss.Reader.Data;

namespace Wipriss.Tests;

/// <summary>Editing tables: the layer of changes, spare rows and columns, saving CSV and XLSX without harm.</summary>
[Collection("Table files")]
public class TableEditTests : IDisposable
{
    private static readonly XNamespace Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "wipriss-edit-tests-" + Guid.NewGuid().ToString("N"));
    private readonly CultureInfo _culture = CultureInfo.CurrentCulture;

    public TableEditTests()
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

    private string PathOf(string name) => Path.Combine(_folder, name);

    // ---- The layer of changes ----

    [Fact]
    public void SpareRowsAndColumnsAreShownOnlyWhileEditingAndGrowTheTable()
    {
        var inner = new MemorySheet("Лист");
        inner.AddRow(["a", "b"], null);
        inner.AddRow(["c", "d"], null);
        var sheet = new EditedSheet(inner);
        Assert.Equal((2, 2), (sheet.RowCount, sheet.ColumnCount));

        sheet.Editing = true;
        Assert.Equal((2, 2), (sheet.DataRowCount, sheet.DataColumnCount));
        Assert.True(sheet.RowCount >= 2 + 30 && sheet.ColumnCount >= 2 + 5);

        // Spaces only is empty: the table does not grow.
        sheet.Set([(10, 0, "   ")], out _);
        Assert.Equal(0, sheet.ChangeCount);
        Assert.Equal(2, sheet.DataRowCount);

        sheet.Set([(40, 6, "новое")], out _);
        Assert.Equal((41, 7), (sheet.DataRowCount, sheet.DataColumnCount));
        Assert.True(sheet.RowCount > 41);
        Assert.Equal("новое", sheet.Text(40, 6));
        Assert.True(sheet.IsEdited(40, 6));

        sheet.Undo();
        Assert.Equal((2, 2), (sheet.DataRowCount, sheet.DataColumnCount));
        Assert.Null(sheet.Text(40, 6));
        sheet.Redo();
        Assert.Equal("новое", sheet.Text(40, 6));

        // Typing the file's own text back is no change.
        sheet.Set([(0, 0, "x")], out _);
        sheet.Set([(0, 0, "a")], out _);
        Assert.False(sheet.IsEdited(0, 0));

        sheet.Editing = false;
        Assert.Equal(sheet.DataRowCount, sheet.RowCount);
    }

    [Fact]
    public void LockedCellsAreSkipped()
    {
        var inner = new MemorySheet("Лист");
        inner.AddRow(["a"], null);
        var sheet = new EditedSheet(inner) { Locks = new() { [(0, 0)] = "нельзя" } };
        int changed = sheet.Set([(0, 0, "b"), (0, 1, "c")], out int locked);
        Assert.Equal((1, 1), (changed, locked));
        Assert.Equal("a", sheet.Text(0, 0));
    }

    [Fact]
    public void RowsCarryTheChanges()
    {
        var inner = new MemorySheet("Лист");
        inner.AddRow(["a", "b"], null);
        var sheet = new EditedSheet(inner) { Editing = true };
        sheet.Set([(0, 1, "B"), (2, 0, "new")], out _);
        var rows = sheet.Rows(CancellationToken.None).ToList();
        Assert.Equal(sheet.RowCount, rows.Count);
        Assert.Equal("B", rows[0][1]);
        Assert.Equal("new", rows[2][0]);
    }

    // ---- CSV ----

    [Fact]
    public void CsvKeepsUntouchedRowsByteForByte()
    {
        var encoding = Encoding.GetEncoding(1251);
        // Odd but valid quoting and spacing in untouched rows must survive as is.
        string original = "sep=;\r\nИмя;Цена\r\n\"Чайник\" ; 100\r\nЛампа;\"2,5\"\r\nСтул;300\r\n\r\n\r\n";
        string path = PathOf("t.csv");
        File.WriteAllBytes(path, encoding.GetBytes(original));

        using var book = Workbook.Load(path, null, null, CancellationToken.None);
        var sheet = Assert.IsType<EditedSheet>(book.Sheets[0]);
        sheet.Editing = true;
        Assert.Equal("Лампа", sheet.Text(2, 0));
        sheet.Set([(2, 1, "3;5"), (4, 0, "Стол \"Дуб\""), (4, 1, "1 500"), (9, 0, null)], out _);

        using var output = new MemoryStream();
        CsvWriter.Write(sheet, book.Csv!, book.CsvDataStart, path, output, CancellationToken.None);
        string saved = encoding.GetString(output.ToArray());
        Assert.Equal("sep=;\r\nИмя;Цена\r\n\"Чайник\" ; 100\r\nЛампа;\"3;5\"\r\nСтул;300\r\n\"Стол \"\"Дуб\"\"\";1 500\r\n", saved);
    }

    [Fact]
    public void CsvClearedLastRowsAreNotWritten()
    {
        string path = PathOf("u.csv");
        File.WriteAllBytes(path, [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("a,b\nc,d\ne,f")]);
        using var book = Workbook.Load(path, null, null, CancellationToken.None);
        var sheet = (EditedSheet)book.Sheets[0];
        sheet.Set([(2, 0, null), (2, 1, null), (0, 2, "x")], out _);
        using var output = new MemoryStream();
        CsvWriter.Write(sheet, book.Csv!, book.CsvDataStart, path, output, CancellationToken.None);
        byte[] bytes = output.ToArray();
        Assert.Equal([0xEF, 0xBB, 0xBF], bytes[..3]);
        Assert.Equal("a,b,x\nc,d\n", Encoding.UTF8.GetString(bytes[3..]));
    }

    [Fact]
    public void CsvLetterOutsideTheEncodingStopsTheSave()
    {
        string path = PathOf("v.csv");
        File.WriteAllBytes(path, Encoding.GetEncoding(1251).GetBytes("Имя;Цена\r\nа;1\r\n"));
        using var book = Workbook.Load(path, null, null, CancellationToken.None);
        var sheet = (EditedSheet)book.Sheets[0];
        sheet.Set([(1, 0, "日本")], out _);
        Assert.Throws<EncoderFallbackException>(() =>
            CsvWriter.Write(sheet, book.Csv!, book.CsvDataStart, path, new MemoryStream(), CancellationToken.None));
    }

    [Fact]
    public void SavingACopyLeavesTheOriginal()
    {
        string path = PathOf("w.csv");
        byte[] bytes = Encoding.UTF8.GetBytes("a;b\r\n1;2\r\n");
        File.WriteAllBytes(path, bytes);
        string copy = PathOf("w (копия).csv");
        using (var book = Workbook.Load(path, null, null, CancellationToken.None))
        {
            ((EditedSheet)book.Sheets[0]).Set([(1, 1, "3")], out _);
            TableSaver.Save(book, copy, CultureInfo.CurrentCulture, CancellationToken.None);
        }
        Assert.Equal(bytes, File.ReadAllBytes(path));
        Assert.Equal("a;b\r\n1;3\r\n", File.ReadAllText(copy));
        Assert.False(File.GetAttributes(copy).HasFlag(FileAttributes.Hidden));
        Assert.Empty(Directory.GetFiles(_folder, "*.wipriss-tmp"));
    }

    // ---- XLSX ----

    [Fact]
    public void XlsxChangesOnlyTheSheetAndKeepsStyles()
    {
        string path = PathOf("t.xlsx");
        WriteXlsx(path);
        byte[] chartBefore = EntryBytes(path, "xl/charts/chart1.xml");
        string otherBefore = EntryText(path, "xl/worksheets/sheet2.xml");

        string saved = PathOf("saved.xlsx");
        using (var book = Workbook.Load(path, null, null, CancellationToken.None))
        {
            var sheet = (EditedSheet)book.Sheets[0];
            Assert.Equal("Чайник", sheet.Text(1, 0));
            sheet.Set([
                (1, 0, "Самовар"),      // shared string → text in the cell
                (1, 1, "2 000,5"),      // a styled number stays a number with its style
                (1, 2, "15.03.2026"),   // a date cell gets a date
                (2, 4, "=СУММ(B2;B3)"), // a formula, in Russian
                (9, 5, "новая строка"), // past the end: a new row
                (3, 0, null),           // cleared, style kept
            ], out _);
            TableSaver.Save(book, saved, CultureInfo.CurrentCulture, CancellationToken.None);
        }

        var xml = XDocument.Parse(EntryText(saved, "xl/worksheets/sheet1.xml"));
        XElement Cell(string r) => xml.Descendants(Main + "c").Single(c => (string?)c.Attribute("r") == r);
        Assert.Equal("inlineStr", (string?)Cell("A2").Attribute("t"));
        Assert.Equal("Самовар", Cell("A2").Element(Main + "is")!.Element(Main + "t")!.Value);
        Assert.Equal("1", (string?)Cell("B2").Attribute("s"));
        Assert.Equal("2000.5", Cell("B2").Element(Main + "v")!.Value);
        Assert.Null(Cell("B2").Attribute("t"));
        Assert.Equal(new DateTime(2026, 3, 15).ToOADate().ToString(CultureInfo.InvariantCulture), Cell("C2").Element(Main + "v")!.Value);
        Assert.Equal("SUM(B2,B3)", Cell("E3").Element(Main + "f")!.Value);
        Assert.Equal("новая строка", Cell("F10").Value);
        Assert.Equal("1", (string?)Cell("A4").Attribute("s"));
        Assert.False(Cell("A4").HasElements);
        // Cells stay in column order, rows in row order.
        var row3 = xml.Descendants(Main + "row").Single(r => (string?)r.Attribute("r") == "3");
        Assert.Equal(["B3", "E3"], row3.Elements(Main + "c").Select(c => (string)c.Attribute("r")!));
        Assert.Equal(["1", "2", "3", "4", "5", "6", "7", "8", "9", "10"], xml.Descendants(Main + "row").Select(r => (string)r.Attribute("r")!));
        Assert.Equal("A1:F10", (string?)xml.Descendants(Main + "dimension").Single().Attribute("ref"));

        // The rest of the file is untouched; the calculation chain is gone and Excel recalculates on open.
        Assert.Equal(chartBefore, EntryBytes(saved, "xl/charts/chart1.xml"));
        Assert.Equal(otherBefore, EntryText(saved, "xl/worksheets/sheet2.xml"));
        Assert.Null(Entry(saved, "xl/calcChain.xml"));
        Assert.DoesNotContain("calcChain", EntryText(saved, "xl/_rels/workbook.xml.rels"));
        Assert.DoesNotContain("calcChain", EntryText(saved, "[Content_Types].xml"));
        var workbook = XDocument.Parse(EntryText(saved, "xl/workbook.xml"));
        Assert.Equal("1", (string?)workbook.Descendants(Main + "calcPr").Single().Attribute("fullCalcOnLoad"));

        // And Wipriss reads back what was typed.
        using var again = Workbook.Load(saved, null, null, CancellationToken.None);
        var back = again.Sheets[0];
        Assert.Equal("Самовар", back.Text(1, 0));
        Assert.True(back.TryNumber(1, 1, out double number) && number == 2000.5);
        Assert.Equal("новая строка", back.Text(9, 5));
        Assert.Null(back.Text(3, 0));
    }

    [Fact]
    public void XlsxLocksArrayFormulasSharedFormulasAndTableHeaders()
    {
        string path = PathOf("locks.xlsx");
        WriteXlsx(path);
        using var zip = ZipFile.OpenRead(path);
        var locks = new XlsxParts(zip).Locks("xl/worksheets/sheet1.xml");
        Assert.Contains((4, 0), locks.Keys);  // array formula A5:B6
        Assert.Contains((5, 1), locks.Keys);
        Assert.Contains((4, 2), locks.Keys);  // first cell of a shared formula C5
        Assert.DoesNotContain((5, 2), locks.Keys); // a cell that only copies it may change
        Assert.Contains((6, 3), locks.Keys);  // header of the smart table D7:E8
        Assert.Contains((6, 4), locks.Keys);
        Assert.DoesNotContain((7, 3), locks.Keys);

        // Even without the list (the file changed since), saving refuses those cells and writes nothing.
        using var book = Workbook.Load(path, null, null, CancellationToken.None);
        var sheet = (EditedSheet)book.Sheets[0];
        sheet.Set([(4, 2, "1")], out _);
        string target = PathOf("never.xlsx");
        Assert.Throws<LockedCellsException>(() =>
            TableSaver.Save(book, target, CultureInfo.CurrentCulture, CancellationToken.None));
        Assert.False(File.Exists(target));
        Assert.Empty(Directory.GetFiles(_folder, "*.wipriss-tmp"));
    }

    [Fact]
    public void XlsxRowsAndColumnsMatchTheFileWhenDataStartsFarFromA1()
    {
        string path = PathOf("far.xlsx");
        WriteXlsx(path);
        string saved = PathOf("far-saved.xlsx");
        using (var book = Workbook.Load(path, null, null, CancellationToken.None))
        {
            var sheet = (EditedSheet)book.Sheets[1];
            Assert.Equal("далеко", sheet.Text(2, 2)); // C3
            sheet.Set([(2, 2, "рядом"), (3, 1, "B4")], out _);
            TableSaver.Save(book, saved, CultureInfo.CurrentCulture, CancellationToken.None);
        }
        var xml = XDocument.Parse(EntryText(saved, "xl/worksheets/sheet2.xml"));
        var cells = xml.Descendants(Main + "c").ToDictionary(c => (string)c.Attribute("r")!, c => c.Value);
        Assert.Equal("рядом", cells["C3"]);
        Assert.Equal("B4", cells["B4"]);
    }

    [Theory]
    [InlineData("=СУММ(A1;B1)*1,5", "SUM(A1,B1)*1.5")]
    [InlineData("=ЕСЛИ(A1>0;\"да; нет\";'Лист 2'!B1)", "IF(A1>0,\"да; нет\",'Лист 2'!B1)")]
    [InlineData("=A1+МОЯФУНКЦИЯ(1)", "A1+МОЯФУНКЦИЯ(1)")]
    public void RussianFormulasBecomeFileFormulas(string typed, string stored)
    {
        Assert.Equal(new CellValue(CellKind.Formula, stored), CellValue.Classify(typed, 0, CultureInfo.CurrentCulture));
    }

    [Theory]
    [InlineData("12,5", 0, 1, "12.5")]
    [InlineData("0123", 0, 0, "0123")]
    [InlineData("+7 999 123-45-67", 0, 0, "+7 999 123-45-67")]
    [InlineData("'1-2", 0, 0, "1-2")]
    [InlineData("15", 2, 0, "15")]
    [InlineData("ИСТИНА", 0, 2, "1")]
    [InlineData("10.02.2026", 0, 0, "10.02.2026")]
    [InlineData("12:30", 1, 1, "0.5208333333333334")]
    public void TypedTextBecomesTheRightKindOfCell(string typed, byte style, int kind, string stored)
    {
        Assert.Equal(new CellValue((CellKind)kind, stored), CellValue.Classify(typed, style, CultureInfo.CurrentCulture));
    }

    [Theory]
    [InlineData("in-place.csv")]
    [InlineData("in-place.xlsx")]
    public void SavingWritesIntoTheSameFileAndLeavesNothingBehind(string name)
    {
        string path = PathOf(name);
        if (name.EndsWith(".csv")) File.WriteAllBytes(path, Encoding.UTF8.GetBytes("a;b\r\n1;2\r\n"));
        else WriteXlsx(path);
        var created = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        File.SetCreationTimeUtc(path, created);
        string work = Path.Combine(Path.GetTempPath(), "Wipriss");
        var before = Directory.Exists(work) ? Directory.GetDirectories(work) : [];

        // The window keeps the file open while it shows it (a CSV is read from disk): saving must work anyway.
        using (var book = Workbook.Load(path, null, null, CancellationToken.None))
        {
            ((EditedSheet)book.Sheets[0]).Set([(1, 1, "3")], out _);
            TableSaver.Save(book, path, CultureInfo.CurrentCulture, CancellationToken.None);
        }

        Assert.Equal([path], Directory.GetFiles(_folder));
        Assert.Equal(created, File.GetCreationTimeUtc(path));
        var after = Directory.Exists(work) ? Directory.GetDirectories(work) : [];
        Assert.Equal(before.Order(), after.Order());
        using var again = Workbook.Load(path, null, null, CancellationToken.None);
        Assert.True(again.Sheets[0].TryNumber(1, 1, out double value) && value == 3);
    }

    // ---- Export ----

    [Fact]
    public void CsvExportsToAWorkbookWithNumbersAndChanges()
    {
        string path = PathOf("e.csv");
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes("Имя;Цена;Код\r\nЧайник;1 200,5;007\r\n"));
        string xlsx = PathOf("e.xlsx");
        using (var book = Workbook.Load(path, null, null, CancellationToken.None))
        {
            var sheet = (EditedSheet)book.Sheets[0];
            sheet.Editing = true;
            sheet.Set([(2, 0, "Лампа"), (2, 1, "350")], out _);
            TableSaver.SaveWith(xlsx, temp =>
            {
                using var output = File.Create(temp);
                TableExport.WriteXlsx(book.Sheets, output, CancellationToken.None);
            }, CancellationToken.None);
        }
        using var back = Workbook.Load(xlsx, null, null, CancellationToken.None);
        var s = back.Sheets[0];
        Assert.Equal((3, 3), (s.DataRowCount, s.DataColumnCount));
        Assert.True(s.TryNumber(1, 1, out double price) && price == 1200.5);
        Assert.Equal("007", s.Text(1, 2));
        Assert.Equal("Лампа", s.Text(2, 0));
        Assert.Empty(Directory.GetFiles(_folder, "*.wipriss-tmp"));
    }

    [Fact]
    public void WorkbookExportsShownSheetToCsv()
    {
        string path = PathOf("e2.xlsx");
        WriteXlsx(path);
        using var book = Workbook.Load(path, null, null, CancellationToken.None);
        using var output = new MemoryStream();
        TableExport.WriteCsv(book.Sheets[1], output, ';', CancellationToken.None);
        byte[] bytes = output.ToArray();
        Assert.Equal([0xEF, 0xBB, 0xBF], bytes[..3]);
        Assert.Equal(";;;\r\n;;;\r\n;;далеко;\r\n;;;7\r\n", Encoding.UTF8.GetString(bytes[3..]));
    }

    // ---- Test files ----

    private static ZipArchiveEntry? Entry(string path, string name)
    {
        using var zip = ZipFile.OpenRead(path);
        return zip.GetEntry(name) is { } entry ? entry : null;
    }

    private static byte[] EntryBytes(string path, string name)
    {
        using var zip = ZipFile.OpenRead(path);
        using var stream = zip.GetEntry(name)!.Open();
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    private static string EntryText(string path, string name) => Encoding.UTF8.GetString(EntryBytes(path, name));

    /// <summary>
    /// A workbook with what an edit must not break: shared strings, styles (number, date), a formula with a
    /// calculation chain, an array formula, a shared formula, a smart table, a chart part, and a second sheet whose
    /// data starts at C3.
    /// </summary>
    private static void WriteXlsx(string path)
    {
        const string ct = "application/vnd.openxmlformats-officedocument.spreadsheetml";
        var parts = new Dictionary<string, string>
        {
            ["[Content_Types].xml"] = $"""
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
                <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
                <Default Extension="xml" ContentType="application/xml"/>
                <Override PartName="/xl/workbook.xml" ContentType="{ct}.sheet.main+xml"/>
                <Override PartName="/xl/worksheets/sheet1.xml" ContentType="{ct}.worksheet+xml"/>
                <Override PartName="/xl/worksheets/sheet2.xml" ContentType="{ct}.worksheet+xml"/>
                <Override PartName="/xl/sharedStrings.xml" ContentType="{ct}.sharedStrings+xml"/>
                <Override PartName="/xl/styles.xml" ContentType="{ct}.styles+xml"/>
                <Override PartName="/xl/calcChain.xml" ContentType="{ct}.calcChain+xml"/>
                <Override PartName="/xl/tables/table1.xml" ContentType="{ct}.table+xml"/>
                <Override PartName="/xl/charts/chart1.xml" ContentType="application/vnd.openxmlformats-officedocument.drawingml.chart+xml"/>
                </Types>
                """,
            ["_rels/.rels"] = """
                <?xml version="1.0" encoding="UTF-8"?>
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/>
                </Relationships>
                """,
            ["xl/workbook.xml"] = """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
                <sheets><sheet name="Продажи" sheetId="1" r:id="rId1"/><sheet name="Далеко" sheetId="2" r:id="rId2"/></sheets>
                <definedNames><definedName name="x">Продажи!$A$1</definedName></definedNames>
                </workbook>
                """,
            ["xl/_rels/workbook.xml.rels"] = """
                <?xml version="1.0" encoding="UTF-8"?>
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/>
                <Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="/xl/worksheets/sheet2.xml"/>
                <Relationship Id="rId3" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/sharedStrings" Target="sharedStrings.xml"/>
                <Relationship Id="rId4" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/>
                <Relationship Id="rId5" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/calcChain" Target="calcChain.xml"/>
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
            ["xl/calcChain.xml"] = """
                <?xml version="1.0" encoding="UTF-8"?>
                <calcChain xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><c r="B3" i="1"/></calcChain>
                """,
            ["xl/worksheets/sheet1.xml"] = """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships" xmlns:x14ac="http://schemas.microsoft.com/office/spreadsheetml/2009/9/ac" xmlns:mc="http://schemas.openxmlformats.org/markup-compatibility/2006" mc:Ignorable="x14ac">
                <dimension ref="A1:E9"/>
                <sheetData>
                <row r="1" spans="1:2" x14ac:dyDescent="0.25"><c r="A1" t="s"><v>0</v></c><c r="B1" t="s"><v>1</v></c></row>
                <row r="2" spans="1:3"><c r="A2" t="s"><v>2</v></c><c r="B2" s="1"><v>1234.5</v></c><c r="C2" s="2"><v>45717</v></c></row>
                <row r="3"><c r="B3"><f>B2*2</f><v>2469</v></c></row>
                <row r="4"><c r="A4" s="1" t="s"><v>3</v></c></row>
                <row r="5"><c r="A5"><f t="array" ref="A5:B6">TRANSPOSE(A1:A2)</f><v>1</v></c><c r="C5"><f t="shared" ref="C5:C6" si="0">A1*2</f><v>2</v></c></row>
                <row r="6"><c r="C6"><f t="shared" si="0"/><v>2</v></c></row>
                <row r="7"><c r="D7" t="inlineStr"><is><t>Имя</t></is></c><c r="E7" t="inlineStr"><is><t>Сумма</t></is></c></row>
                <row r="8"><c r="D8" t="inlineStr"><is><t>Аня</t></is></c><c r="E8"><v>5</v></c></row>
                <row r="9"><c r="E9" s="1"/></row>
                </sheetData>
                <tableParts count="1"><tablePart r:id="rId1"/></tableParts>
                </worksheet>
                """,
            ["xl/worksheets/_rels/sheet1.xml.rels"] = """
                <?xml version="1.0" encoding="UTF-8"?>
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/table" Target="../tables/table1.xml"/>
                </Relationships>
                """,
            ["xl/tables/table1.xml"] = """
                <?xml version="1.0" encoding="UTF-8"?>
                <table xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" id="1" name="Т1" displayName="Т1" ref="D7:E8">
                <tableColumns count="2"><tableColumn id="1" name="Имя"/><tableColumn id="2" name="Сумма"/></tableColumns></table>
                """,
            ["xl/charts/chart1.xml"] = """
                <?xml version="1.0" encoding="UTF-8"?>
                <c:chartSpace xmlns:c="http://schemas.openxmlformats.org/drawingml/2006/chart"><c:chart/></c:chartSpace>
                """,
            ["xl/worksheets/sheet2.xml"] = """
                <?xml version="1.0" encoding="UTF-8"?>
                <worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><dimension ref="C3:D4"/><sheetData><row r="3"><c r="C3" t="inlineStr"><is><t>далеко</t></is></c></row><row r="4"><c r="D4"><v>7</v></c></row></sheetData></worksheet>
                """,
        };
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, xml) in parts)
        {
            using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
            writer.Write(xml);
        }
    }
}
