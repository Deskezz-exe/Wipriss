using System.IO;
using System.Text;

namespace Wipriss.Reader.Data;

/// <summary>
/// A big text table that stays on disk: one pass finds where every row starts (8 bytes per row), then only the rows
/// on screen are read and split, with a small cache. So a file with millions of rows opens in seconds and takes
/// little memory. Works for encodings where the delimiter, quote and line breaks are single ASCII bytes
/// (UTF-8 and the one-byte code pages); UTF-16 files are read whole into a <see cref="MemorySheet"/> instead.
/// </summary>
internal sealed class CsvSheet : Sheet, IDisposable
{
    /// <summary>A row longer than this (a broken file) is cut when shown.</summary>
    private const int MaxRowBytes = 4 << 20;
    private const int CacheSize = 4096;
    public const int MaxRows = 50_000_000;

    private readonly Lock _gate = new();
    private readonly FileStream _file;
    private readonly Encoding _encoding;
    private readonly char _delimiter;
    private readonly long _start;
    private readonly long _end;
    private bool _quotes = true;
    private long[] _rows = new long[1024];
    private int _count;
    private int _columns;
    private readonly Dictionary<int, string?[]> _cache = [];

    public CsvSheet(string name, string path, Encoding encoding, char delimiter, long start) : base(name)
    {
        _file = Workbook.OpenShared(path);
        _encoding = encoding;
        _delimiter = delimiter;
        _start = start;
        _end = _file.Length;
    }

    public override int RowCount => _count;

    public override int ColumnCount => _columns;

    /// <summary>True when the file had a quote that never closed and was read as plain text instead.</summary>
    public bool QuotesIgnored => !_quotes;

    /// <summary>Where the rows begin: after the byte order mark and the "sep=" line.</summary>
    public long DataStart => _start;

    public string FilePath => _file.Name;

    /// <summary>The bytes of a row in the file, with its line break.</summary>
    public (long Start, long End) RowBytes(int row)
    {
        lock (_gate)
        {
            if ((uint)row >= (uint)_count) return (_end, _end);
            return (_rows[row], row + 1 < _count ? _rows[row + 1] : _end);
        }
    }

    /// <summary>Finds the start of every row. Runs once, on a background thread, before the sheet is shown.</summary>
    public void Index(IProgress<long>? progress, CancellationToken cancel)
    {
        if (!IndexOnce(progress, cancel))
        {
            // A quote that never closed would make the rest of the file one row: read quotes as plain text.
            _quotes = false;
            IndexOnce(progress, cancel);
        }
    }

    private bool IndexOnce(IProgress<long>? progress, CancellationToken cancel)
    {
        byte delimiter = (byte)_delimiter;
        var rows = new long[Math.Max(1024, (int)Math.Min(_end / 64, 1 << 20))];
        int count = 0;
        int columns = 0;
        var state = CsvFormat.State.FieldStart;
        int fields = 1;
        long rowStart = _start;
        bool rowHasData = false;
        bool lastWasCr = false;
        bool unclosed = false;

        using var file = Workbook.OpenShared(_file.Name);
        file.Position = _start;
        var buffer = new byte[1 << 20];
        long position = _start;
        int read;
        while ((read = file.Read(buffer, 0, buffer.Length)) > 0)
        {
            cancel.ThrowIfCancellationRequested();
            for (int i = 0; i < read; i++)
            {
                byte b = buffer[i];
                long at = position + i;
                if (lastWasCr)
                {
                    lastWasCr = false;
                    if (b == (byte)'\n')
                    {
                        rowStart = at + 1;
                        continue;
                    }
                }
                if ((b == (byte)'\n' || b == (byte)'\r') && state != CsvFormat.State.Quoted)
                {
                    if (!AddRow()) goto done;
                    lastWasCr = b == (byte)'\r';
                    rowStart = at + 1;
                    continue;
                }
                rowHasData = true;
                if (b == delimiter && state != CsvFormat.State.Quoted) fields++;
                state = CsvFormat.Next(state, (char)b, _delimiter, _quotes);
            }
            position += read;
            progress?.Report(count);
        }
        unclosed = state == CsvFormat.State.Quoted;
        if (rowHasData) AddRow();

    done:
        if (unclosed && _quotes) return false;
        lock (_gate)
        {
            _rows = rows;
            _count = count;
            _columns = columns;
            _cache.Clear();
        }
        progress?.Report(count);
        return true;

        bool AddRow()
        {
            if (count >= MaxRows) return false;
            if (count == rows.Length) Array.Resize(ref rows, rows.Length * 2);
            rows[count++] = rowStart;
            if (rowHasData && fields > columns) columns = Math.Min(fields, 16_384);
            fields = 1;
            rowHasData = false;
            state = CsvFormat.State.FieldStart;
            return true;
        }
    }

    public override string? Text(int row, int column)
    {
        var fields = Row(row);
        return (uint)column < (uint)fields.Length ? fields[column] : null;
    }

    public override bool TryNumber(int row, int column, out double value) => TryParseLooseNumber(Text(row, column), out value);

    private string?[] Row(int row)
    {
        lock (_gate)
        {
            if ((uint)row >= (uint)_count) return [];
            if (_cache.TryGetValue(row, out var cached)) return cached;
            long start = _rows[row];
            long end = row + 1 < _count ? _rows[row + 1] : _end;
            int length = (int)Math.Min(end - start, MaxRowBytes);
            var bytes = new byte[length];
            _file.Position = start;
            _file.ReadExactly(bytes);
            string line = _encoding.GetString(bytes).TrimEnd('\r', '\n');
            var fields = CsvFormat.ParseRow(line, _delimiter, _quotes);
            if (_cache.Count >= CacheSize) _cache.Clear();
            _cache[row] = fields;
            return fields;
        }
    }

    public override IEnumerable<string?[]> Rows(CancellationToken cancel)
    {
        using var file = Workbook.OpenShared(_file.Name);
        file.Position = _start;
        using var reader = new StreamReader(file, _encoding, detectEncodingFromByteOrderMarks: false, 1 << 16);
        int index = 0;
        foreach (var row in CsvFormat.ReadRows(reader, _delimiter, _quotes, cancel))
        {
            if (index++ >= _count) yield break;
            yield return row;
        }
    }

    public void Dispose()
    {
        lock (_gate) _file.Dispose();
    }
}
