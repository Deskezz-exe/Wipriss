using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using Wipriss.Reader.Data;

namespace Wipriss.Tests;

/// <summary>Cell formatting of XLSX tables: reading it, changing it with Ctrl+Z, saving it without harm.</summary>
[Collection("Table files")]
public class TableFormatTests : IDisposable
{
    private static readonly XNamespace Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "wipriss-format-tests-" + Guid.NewGuid().ToString("N"));

    public TableFormatTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); }
        catch (IOException) { }
    }

    private string PathOf(string name) => Path.Combine(_folder, name);

    [Fact]
    public void ReadsFontsFillsAlignmentAndRowHeights()
    {
        string path = PathOf("f.xlsx");
        WriteXlsx(path);
        using var book = Workbook.Load(path, null, null, CancellationToken.None);
        var sheet = book.Sheets[0];
        Assert.Equal(10, sheet.DefaultFontSize);
        var title = sheet.Format(0, 0);
        Assert.Equal(new CellFormat(Size: 14, Bold: true, Color: 0xFFC00000, Horizontal: HAlign.Center), title);
        var note = sheet.Format(1, 1);
        Assert.Equal(TextMode.Wrap, note.Mode);
        Assert.Equal(VAlign.Top, note.Vertical);
        Assert.Equal(0xFFFFFF00u, note.Fill);
        Assert.True(note.Italic && note.Underline);
        Assert.True(sheet.RowMayWrap(1));
        Assert.Equal((30.0, true), sheet.RowHeight(0));
        Assert.Equal((15.0, false), sheet.RowHeight(1));
        Assert.Equal(CellFormat.Plain, sheet.Format(1, 0));
        // A theme color with a tint: dark 1 is black, lighter by half is grey.
        Assert.Equal(0xFF808080u, sheet.Format(2, 0).Color);
    }

    [Fact]
    public void FormatChangesAndMergesGoBackWithCtrlZ()
    {
        string path = PathOf("u.xlsx");
        WriteXlsx(path);
        using var book = Workbook.Load(path, null, null, CancellationToken.None);
        var sheet = (EditedSheet)book.Sheets[0];
        Assert.Equal(2, sheet.SetFormat([(0, 0), (1, 0)], f => f with { Bold = !f.Bold }));
        Assert.False(sheet.Format(0, 0).Bold);
        Assert.True(sheet.Format(1, 0).Bold);
        Assert.Equal(2, sheet.ChangeCount);

        // Merging clears the other cells' text, in the same step.
        Assert.Equal("заметка", sheet.Text(1, 1));
        Assert.Equal(1, sheet.TextsLostByMerge(new CellRange(1, 0, 1, 1)));
        Assert.Equal(new CellRange(1, 0, 1, 1), sheet.Merge(new CellRange(1, 0, 1, 1)));
        Assert.Null(sheet.Text(1, 1));
        Assert.Contains(new CellRange(1, 0, 1, 1), sheet.Merges);
        // A merge touching another one takes it in.
        Assert.Equal(new CellRange(1, 0, 2, 1), sheet.Merge(new CellRange(1, 1, 2, 1)));
        Assert.DoesNotContain(new CellRange(1, 0, 1, 1), sheet.Merges);

        sheet.Undo();
        Assert.Contains(new CellRange(1, 0, 1, 1), sheet.Merges);
        sheet.Undo();
        Assert.Equal("заметка", sheet.Text(1, 1));
        Assert.Empty(sheet.Merges);
        sheet.Undo();
        Assert.True(sheet.Format(0, 0).Bold);
        Assert.Equal(0, sheet.ChangeCount);
        sheet.Redo();
        Assert.False(sheet.Format(0, 0).Bold);

        // Toggling back to the file's look is no change.
        sheet.SetFormat([(0, 0), (1, 0)], f => f with { Bold = !f.Bold });
        Assert.Equal(0, sheet.ChangeCount);
    }

    [Fact]
    public void SavingAddsStylesAfterTheOldOnesAndWritesMerges()
    {
        string path = PathOf("s.xlsx");
        WriteXlsx(path);
        var stylesBefore = XDocument.Parse(EntryText(path, "xl/styles.xml"));
        var oldXfs = stylesBefore.Descendants(Main + "cellXfs").Single().Elements().Select(e => e.ToString()).ToList();
        string saved = PathOf("saved.xlsx");
        using (var book = Workbook.Load(path, null, null, CancellationToken.None))
        {
            var sheet = (EditedSheet)book.Sheets[0];
            sheet.SetFormat([(0, 0)], f => f with { Bold = false, Size = 0 });                 // A1: back to the usual font
            sheet.SetFormat([(1, 0)], f => f with { Mode = TextMode.Wrap, Fill = 0xFF00B0F0 }); // A2: wraps, row 2 fits again
            sheet.SetFormat([(4, 3)], f => f with { Horizontal = HAlign.Right, Mode = TextMode.Overflow }); // D5: an empty cell
            sheet.Merge(new CellRange(2, 1, 2, 2));                                                 // B3:C3
            TableSaver.Save(book, saved, CultureInfo.InvariantCulture, CancellationToken.None);
        }

        var styles = XDocument.Parse(EntryText(saved, "xl/styles.xml"));
        var xfs = styles.Descendants(Main + "cellXfs").Single();
        // The old styles keep their numbers and look; new ones follow.
        Assert.Equal(oldXfs, xfs.Elements().Take(oldXfs.Count).Select(e => e.ToString()));
        Assert.Equal(xfs.Elements().Count().ToString(), (string?)xfs.Attribute("count"));

        var xml = XDocument.Parse(EntryText(saved, "xl/worksheets/sheet1.xml"));
        XElement Cell(string r) => xml.Descendants(Main + "c").Single(c => (string?)c.Attribute("r") == r);
        Assert.True(int.Parse((string)Cell("A1").Attribute("s")!) >= oldXfs.Count);
        Assert.True(int.Parse((string)Cell("D5").Attribute("s")!) >= oldXfs.Count);
        // Row 2's height came from Excel, so it is dropped; row 1's was set by hand and stays.
        var rows = xml.Descendants(Main + "row").ToDictionary(r => (string)r.Attribute("r")!);
        Assert.Null(rows["2"].Attribute("ht"));
        Assert.Equal("30", (string?)rows["1"].Attribute("ht"));
        // Merged cells go before the page margins, as the format wants.
        var names = xml.Root!.Elements().Select(e => e.Name.LocalName).ToList();
        Assert.Equal(names.IndexOf("pageMargins") - 1, names.IndexOf("mergeCells"));
        Assert.Equal("B3:C3", (string?)xml.Descendants(Main + "mergeCell").Single().Attribute("ref"));

        using var again = Workbook.Load(saved, null, null, CancellationToken.None);
        var back = again.Sheets[0];
        Assert.Equal(new CellFormat(Color: 0xFFC00000, Horizontal: HAlign.Center), back.Format(0, 0));
        Assert.Equal(TextMode.Wrap, back.Format(1, 0).Mode);
        Assert.Equal(0xFF00B0F0u, back.Format(1, 0).Fill);
        Assert.Equal(new CellFormat(Horizontal: HAlign.Right, Mode: TextMode.Overflow), back.Format(4, 3));
        Assert.Equal([new CellRange(2, 1, 2, 2)], back.Merges);
        Assert.Equal("Отчёт", back.Text(0, 0));

        // Splitting every merge removes the list from the sheet.
        string split = PathOf("split.xlsx");
        using (var book = Workbook.Load(saved, null, null, CancellationToken.None))
        {
            var sheet = (EditedSheet)book.Sheets[0];
            Assert.True(sheet.Unmerge(new CellRange(2, 2, 2, 2)));
            TableSaver.Save(book, split, CultureInfo.InvariantCulture, CancellationToken.None);
        }
        Assert.DoesNotContain("mergeCell", EntryText(split, "xl/worksheets/sheet1.xml"));
    }

    // ---- Test file ----

    private static string EntryText(string path, string name)
    {
        using var zip = ZipFile.OpenRead(path);
        using var reader = new StreamReader(zip.GetEntry(name)!.Open(), Encoding.UTF8);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// A workbook with styled cells: A1 a big red bold centered title (row 1 set by hand to 30 pt), B2 a wrapped
    /// italic underlined note on yellow, A3 grey from a theme color, and page margins after the cells.
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
                <Override PartName="/xl/styles.xml" ContentType="{ct}.styles+xml"/>
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
                <sheets><sheet name="Лист1" sheetId="1" r:id="rId1"/></sheets>
                </workbook>
                """,
            ["xl/_rels/workbook.xml.rels"] = """
                <?xml version="1.0" encoding="UTF-8"?>
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/>
                <Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/>
                </Relationships>
                """,
            ["xl/styles.xml"] = """
                <?xml version="1.0" encoding="UTF-8"?>
                <styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
                <fonts count="4">
                <font><sz val="10"/><name val="Arial"/></font>
                <font><b/><sz val="14"/><color rgb="FFC00000"/><name val="Arial"/></font>
                <font><i/><u/><sz val="10"/><name val="Arial"/></font>
                <font><sz val="10"/><color theme="1" tint="0.5"/><name val="Arial"/></font>
                </fonts>
                <fills count="3"><fill><patternFill patternType="none"/></fill><fill><patternFill patternType="gray125"/></fill>
                <fill><patternFill patternType="solid"><fgColor rgb="FFFFFF00"/><bgColor indexed="64"/></patternFill></fill></fills>
                <borders count="1"><border/></borders>
                <cellStyleXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0"/></cellStyleXfs>
                <cellXfs count="4">
                <xf numFmtId="0" fontId="0" fillId="0" borderId="0" xfId="0"/>
                <xf numFmtId="0" fontId="1" fillId="0" borderId="0" xfId="0" applyFont="1" applyAlignment="1"><alignment horizontal="center"/></xf>
                <xf numFmtId="0" fontId="2" fillId="2" borderId="0" xfId="0" applyFont="1" applyFill="1" applyAlignment="1"><alignment vertical="top" wrapText="1"/></xf>
                <xf numFmtId="0" fontId="3" fillId="0" borderId="0" xfId="0" applyFont="1"/>
                </cellXfs>
                <cellStyles count="1"><cellStyle name="Normal" xfId="0" builtinId="0"/></cellStyles>
                </styleSheet>
                """,
            ["xl/worksheets/sheet1.xml"] = """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
                <dimension ref="A1:B3"/>
                <sheetFormatPr defaultRowHeight="12.75"/>
                <sheetData>
                <row r="1" ht="30" customHeight="1"><c r="A1" s="1" t="inlineStr"><is><t>Отчёт</t></is></c></row>
                <row r="2" ht="15"><c r="A2" t="inlineStr"><is><t>текст</t></is></c><c r="B2" s="2" t="inlineStr"><is><t>заметка</t></is></c></row>
                <row r="3"><c r="A3" s="3"><v>5</v></c></row>
                </sheetData>
                <pageMargins left="0.7" right="0.7" top="0.75" bottom="0.75" header="0.3" footer="0.3"/>
                </worksheet>
                """,
        };
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, text) in parts)
        {
            using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
            writer.Write(text.Trim());
        }
    }
}
