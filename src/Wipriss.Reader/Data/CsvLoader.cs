using System.IO;
using System.Text;

namespace Wipriss.Reader.Data;

/// <summary>Opens CSV/TSV: guesses the encoding and the delimiter unless the owner picked them.</summary>
internal static class CsvLoader
{
    static CsvLoader() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    private const int SampleBytes = 1 << 20;
    /// <summary>UTF-16 files are read whole: larger ones are refused.</summary>
    private const long MaxWholeFileBytes = 1L << 30;

    public static Workbook Load(string path, CsvSettings? chosen, IProgress<long>? progress, CancellationToken cancel)
    {
        byte[] sample;
        using (var probe = Workbook.OpenShared(path))
        {
            sample = new byte[(int)Math.Min(probe.Length, SampleBytes)];
            probe.ReadExactly(sample);
        }

        var (detected, bom) = CsvFormat.DetectEncoding(sample);
        var encoding = chosen?.Encoding ?? detected;
        // A chosen encoding still skips the file's own byte order mark, if it has the same one.
        if (chosen is not null && chosen.Encoding.CodePage != detected.CodePage) bom = 0;

        string text = encoding.GetString(sample, bom, sample.Length - bom);
        var (sepDelimiter, sepLength) = CsvFormat.SepLine(text);
        char delimiter = chosen?.Delimiter ?? sepDelimiter ?? CsvFormat.DetectDelimiter(text[sepLength..], Path.GetExtension(path));
        var settings = new CsvSettings(encoding, delimiter);
        string name = Path.GetFileNameWithoutExtension(path);

        if (encoding is UnicodeEncoding or UTF32Encoding)
        {
            var sheet = new MemorySheet(name);
            using var file = Workbook.OpenShared(path);
            if (file.Length > MaxWholeFileBytes) throw new InvalidDataException("Файл в кодировке UTF-16 слишком большой.");
            file.Position = bom;
            using var reader = new StreamReader(file, encoding, detectEncodingFromByteOrderMarks: false);
            long rows = 0;
            bool skippedSep = sepLength == 0;
            foreach (var row in CsvFormat.ReadRows(reader, delimiter, cancel: cancel))
            {
                if (!skippedSep)
                {
                    skippedSep = true;
                    continue;
                }
                sheet.AddRow(row, null);
                if ((++rows & 0x3FF) == 0) progress?.Report(rows);
            }
            sheet.TrimEnd();
            return new Workbook(path, TableFormat.Csv, [new NumberedSheet(sheet)], settings);
        }

        long start = bom + encoding.GetByteCount(text.AsSpan(0, sepLength));
        var csv = new CsvSheet(name, path, encoding, delimiter, start);
        try
        {
            csv.Index(progress, cancel);
        }
        catch
        {
            csv.Dispose();
            throw;
        }
        return new Workbook(path, TableFormat.Csv, [csv], settings);
    }

    /// <summary>A text table in memory: numbers are read from the text, like in <see cref="CsvSheet"/>.</summary>
    private sealed class NumberedSheet(MemorySheet inner) : Sheet(inner.Name)
    {
        public override int RowCount => inner.RowCount;
        public override int ColumnCount => inner.ColumnCount;
        public override string? Text(int row, int column) => inner.Text(row, column);
        public override bool TryNumber(int row, int column, out double value) => TryParseLooseNumber(Text(row, column), out value);
        public override IEnumerable<string?[]> Rows(CancellationToken cancel) => inner.Rows(cancel);
    }
}
