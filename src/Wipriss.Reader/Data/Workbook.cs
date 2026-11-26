using System.IO;
using System.Text;

namespace Wipriss.Reader.Data;

internal enum TableFormat { Excel, OpenDocument, Csv }

/// <summary>Settings of a text table: what the reader guessed, or what the owner picked.</summary>
internal sealed record CsvSettings(Encoding Encoding, char Delimiter);

/// <summary>An opened table file: its sheets, and for CSV how it was read.</summary>
internal sealed class Workbook : IDisposable
{
    public Workbook(string path, TableFormat format, IReadOnlyList<Sheet> sheets, CsvSettings? csv = null)
    {
        Path = path;
        Format = format;
        Sheets = sheets;
        Csv = csv;
    }

    public string Path { get; }

    public TableFormat Format { get; }

    public IReadOnlyList<Sheet> Sheets { get; }

    public CsvSettings? Csv { get; }

    /// <summary>Kinds of files the viewer opens, by extension.</summary>
    public static readonly IReadOnlyDictionary<string, TableFormat> Extensions =
        new Dictionary<string, TableFormat>(StringComparer.OrdinalIgnoreCase)
        {
            [".xlsx"] = TableFormat.Excel,
            [".xlsm"] = TableFormat.Excel,
            [".xlsb"] = TableFormat.Excel,
            [".xls"] = TableFormat.Excel,
            [".ods"] = TableFormat.OpenDocument,
            [".csv"] = TableFormat.Csv,
            [".tsv"] = TableFormat.Csv,
            [".tab"] = TableFormat.Csv,
        };

    public static bool CanOpen(string path) => Extensions.ContainsKey(System.IO.Path.GetExtension(path));

    /// <summary>
    /// Opens the file for reading only. Excel may hold it open: sharing is allowed, nothing is ever written.
    /// </summary>
    public static FileStream OpenShared(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.SequentialScan);

    /// <summary>
    /// Reads a table file. <paramref name="progress"/> gets the number of rows read so far.
    /// For CSV, <paramref name="csv"/> overrides the guessed encoding and delimiter.
    /// </summary>
    public static Workbook Load(string path, CsvSettings? csv, IProgress<long>? progress, CancellationToken cancel)
    {
        string ext = System.IO.Path.GetExtension(path);
        if (!Extensions.TryGetValue(ext, out var format))
        {
            // Unknown extension: a text table is the only safe guess (it is read as plain text).
            format = TableFormat.Csv;
        }
        return format switch
        {
            TableFormat.Excel => ExcelLoader.Load(path, progress, cancel),
            TableFormat.OpenDocument => OdsLoader.Load(path, progress, cancel),
            _ => CsvLoader.Load(path, csv, progress, cancel),
        };
    }

    public void Dispose()
    {
        foreach (var sheet in Sheets)
            (sheet as IDisposable)?.Dispose();
    }
}
