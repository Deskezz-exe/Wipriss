using System.IO;
using System.Text;

namespace Wipriss.Reader.Data;

/// <summary>
/// Writes a text table with the owner's changes. Rows nobody touched are copied byte for byte from the file
/// (their quotes, spaces and line breaks stay exactly as they were); changed and new rows are written in the
/// file's encoding and delimiter. The byte order mark and Excel's "sep=" line are kept. Empty rows at the end are
/// not written: the file ends at its last filled row.
/// </summary>
internal static class CsvWriter
{
    public static void Write(EditedSheet sheet, CsvSettings settings, long dataStart, string sourcePath, Stream output, CancellationToken cancel)
    {
        // A letter the file's encoding has no place for would silently become "?": stop and say so instead.
        var encoding = (Encoding)settings.Encoding.Clone();
        encoding.EncoderFallback = EncoderFallback.ExceptionFallback;
        char delimiter = settings.Delimiter;

        using var source = Workbook.OpenShared(sourcePath);
        Copy(source, output, 0, Math.Min(dataStart, source.Length));

        var csv = sheet.Inner as CsvSheet;
        int last = LastFilledRow(sheet);
        int width = sheet.DataColumnCount;
        var changedRows = new HashSet<int>();
        foreach (var (row, _) in sheet.Edits.Keys) changedRows.Add(row);
        byte[] newline = encoding.GetBytes(csv is not null ? LineBreak(csv, source) : "\r\n");
        bool endsWithBreak = csv is null || source.Length == 0 || EndsWithBreak(source, source.Length);

        bool openRow = false; // the row written last has no line break yet
        var line = new StringBuilder();
        for (int r = 0; r <= last; r++)
        {
            if ((r & 0xFFF) == 0) cancel.ThrowIfCancellationRequested();
            if (openRow) output.Write(newline);
            if (csv is not null && r < csv.RowCount && !changedRows.Contains(r))
            {
                var (start, end) = csv.RowBytes(r);
                Copy(source, output, start, end);
                openRow = end <= start || !EndsWithBreak(source, end);
                continue;
            }
            line.Clear();
            for (int c = 0; c < width; c++)
            {
                if (c > 0) line.Append(delimiter);
                if (sheet.Text(r, c) is { } text) AppendField(line, text, delimiter);
            }
            output.Write(encoding.GetBytes(line.ToString()));
            openRow = true;
            if (r < last || endsWithBreak)
            {
                output.Write(newline);
                openRow = false;
            }
        }
    }

    /// <summary>The last row with any text, or -1 for an empty table.</summary>
    internal static int LastFilledRow(Sheet sheet)
    {
        int columns = sheet.DataColumnCount;
        for (int r = sheet.DataRowCount - 1; r >= 0; r--)
        {
            for (int c = 0; c < columns; c++)
            {
                if (sheet.Text(r, c) is not null) return r;
            }
        }
        return -1;
    }

    /// <summary>Quotes a field when it holds the delimiter, a quote, a line break or spaces at an edge.</summary>
    internal static void AppendField(StringBuilder line, string text, char delimiter)
    {
        bool quote = text.Length > 0 && (char.IsWhiteSpace(text[0]) || char.IsWhiteSpace(text[^1]));
        if (!quote)
        {
            foreach (char ch in text)
            {
                if (ch == delimiter || ch is '"' or '\r' or '\n')
                {
                    quote = true;
                    break;
                }
            }
        }
        if (!quote)
        {
            line.Append(text);
            return;
        }
        line.Append('"').Append(text.Replace("\"", "\"\"")).Append('"');
    }

    /// <summary>The file's own line break (taken from its first row), CR LF when it has none.</summary>
    private static string LineBreak(CsvSheet csv, FileStream source)
    {
        if (csv.RowCount == 0) return "\r\n";
        var (start, end) = csv.RowBytes(0);
        if (end - start >= 2 && ByteAt(source, end - 2) == '\r' && ByteAt(source, end - 1) == '\n') return "\r\n";
        if (end > start && ByteAt(source, end - 1) == '\n') return "\n";
        if (end > start && ByteAt(source, end - 1) == '\r') return "\r";
        return "\r\n";
    }

    private static bool EndsWithBreak(FileStream source, long end) => end > 0 && ByteAt(source, end - 1) is '\n' or '\r';

    private static int ByteAt(FileStream source, long at)
    {
        source.Position = at;
        return source.ReadByte();
    }

    private static void Copy(FileStream source, Stream output, long start, long end)
    {
        if (end <= start) return;
        source.Position = start;
        var buffer = new byte[(int)Math.Min(1 << 20, end - start)];
        long left = end - start;
        while (left > 0)
        {
            int read = source.Read(buffer, 0, (int)Math.Min(buffer.Length, left));
            if (read <= 0) throw new EndOfStreamException("Файл изменился во время сохранения.");
            output.Write(buffer, 0, read);
            left -= read;
        }
    }
}
