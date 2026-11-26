using System.IO;
using System.Text;
using System.Text.Unicode;

namespace Wipriss.Reader.Data;

/// <summary>
/// Reading CSV the way Excel writes it (RFC 4180): fields split by a delimiter, a field may be quoted, a quote
/// inside a quoted field is doubled, quoted fields may hold line breaks. A quote in the middle of an unquoted
/// field is just a character (<c>5" экран</c>).
/// </summary>
internal static class CsvFormat
{
    public static readonly char[] Delimiters = [';', ',', '\t', '|'];

    public static string DelimiterName(char delimiter) => delimiter switch
    {
        ';' => "точка с запятой",
        ',' => "запятая",
        '\t' => "табуляция",
        '|' => "черта |",
        _ => delimiter.ToString(),
    };

    internal enum State : byte { FieldStart, Unquoted, Quoted, QuoteInQuoted }

    /// <summary>Splits one row (without its line break) into fields.</summary>
    public static string?[] ParseRow(ReadOnlySpan<char> line, char delimiter, bool quotes = true)
    {
        var fields = new List<string?>();
        var field = new StringBuilder();
        var state = State.FieldStart;
        foreach (char ch in line)
        {
            switch (state)
            {
                case State.FieldStart:
                    if (ch == '"' && quotes) state = State.Quoted;
                    else if (ch == delimiter) Add();
                    else { field.Append(ch); state = State.Unquoted; }
                    break;
                case State.Unquoted:
                    if (ch == delimiter) Add();
                    else field.Append(ch);
                    break;
                case State.Quoted:
                    if (ch == '"') state = State.QuoteInQuoted;
                    else field.Append(ch);
                    break;
                case State.QuoteInQuoted:
                    if (ch == '"') { field.Append('"'); state = State.Quoted; }
                    else if (ch == delimiter) Add();
                    else { field.Append(ch); state = State.Unquoted; } // stray text after the closing quote
                    break;
            }
        }
        Add();
        int used = fields.Count;
        while (used > 0 && fields[used - 1] is null) used--;
        return used == fields.Count ? fields.ToArray() : fields.GetRange(0, used).ToArray();

        void Add()
        {
            fields.Add(field.Length == 0 ? null : field.ToString());
            field.Clear();
            state = State.FieldStart;
        }
    }

    /// <summary>Reads rows from text: a line break inside quotes stays in the field. CR LF, LF and CR all end a row.</summary>
    public static IEnumerable<string?[]> ReadRows(TextReader reader, char delimiter, bool quotes = true, CancellationToken cancel = default)
    {
        var line = new StringBuilder();
        var state = State.FieldStart;
        var buffer = new char[1 << 15];
        bool lastWasCr = false;
        int read;
        long count = 0;
        while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
        {
            if ((++count & 0x3F) == 0) cancel.ThrowIfCancellationRequested();
            for (int i = 0; i < read; i++)
            {
                char ch = buffer[i];
                if (lastWasCr)
                {
                    lastWasCr = false;
                    if (ch == '\n') continue;
                }
                bool inQuotes = state == State.Quoted;
                if ((ch == '\n' || ch == '\r') && !inQuotes)
                {
                    lastWasCr = ch == '\r';
                    yield return ParseRow(line.ToString(), delimiter, quotes);
                    line.Clear();
                    state = State.FieldStart;
                    continue;
                }
                line.Append(ch);
                state = Next(state, ch, delimiter, quotes);
            }
        }
        if (line.Length > 0) yield return ParseRow(line.ToString(), delimiter, quotes);
    }

    internal static State Next(State state, char ch, char delimiter, bool quotes) => state switch
    {
        State.FieldStart => ch == '"' && quotes ? State.Quoted : ch == delimiter ? State.FieldStart : State.Unquoted,
        State.Unquoted => ch == delimiter ? State.FieldStart : State.Unquoted,
        State.Quoted => ch == '"' ? State.QuoteInQuoted : State.Quoted,
        _ => ch == '"' ? State.Quoted : ch == delimiter ? State.FieldStart : State.Unquoted,
    };

    // ---- Guessing how a file is written ----

    /// <summary>Encoding and the length of its byte order mark.</summary>
    public static (Encoding Encoding, int Bom) DetectEncoding(ReadOnlySpan<byte> sample)
    {
        if (sample.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF])) return (new UTF8Encoding(false), 3);
        if (sample.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE])) return (new UnicodeEncoding(false, false), 2);
        if (sample.StartsWith((ReadOnlySpan<byte>)[0xFE, 0xFF])) return (new UnicodeEncoding(true, false), 2);

        int nonAscii = 0, zeros = 0;
        foreach (byte b in sample)
        {
            if (b >= 0x80) nonAscii++;
            else if (b == 0) zeros++;
        }
        // UTF-16 without a mark: every other byte is zero in Latin text.
        if (zeros > sample.Length / 4 && sample.Length >= 4)
            return (new UnicodeEncoding(bigEndian: sample[0] == 0, byteOrderMark: false), 0);
        if (nonAscii == 0 || Utf8.IsValid(TrimPartialUtf8(sample))) return (new UTF8Encoding(false), 0);

        // Russian single-byte code pages: the one in which the most common letters come out wins.
        Encoding? best = null;
        int bestScore = -1;
        foreach (int codePage in (int[])[1251, 866, 20866])
        {
            var encoding = Encoding.GetEncoding(codePage);
            int score = 0;
            foreach (char ch in encoding.GetString(sample))
            {
                if ("оеаинтсрвлкмдпу".Contains(ch)) score++;
            }
            if (score > bestScore)
            {
                best = encoding;
                bestScore = score;
            }
        }
        // Russian text is mostly common letters; when they are rare, it is a Western European file.
        if (bestScore * 10 < nonAscii * 3) return (Encoding.GetEncoding(1252), 0);
        return (best!, 0);
    }

    /// <summary>The sample may end in the middle of a UTF-8 letter: cut that letter off before checking.</summary>
    private static ReadOnlySpan<byte> TrimPartialUtf8(ReadOnlySpan<byte> sample)
    {
        int end = sample.Length;
        for (int back = 1; back <= 4 && back <= sample.Length; back++)
        {
            byte b = sample[^back];
            if ((b & 0xC0) == 0x80) continue; // continuation byte
            int need = b >= 0xF0 ? 4 : b >= 0xE0 ? 3 : b >= 0xC0 ? 2 : 1;
            if (need > back) end = sample.Length - back;
            break;
        }
        return sample[..end];
    }

    /// <summary>
    /// Excel's "sep=;" first line names the delimiter. Returns it and the length of that line with its line break.
    /// </summary>
    public static (char? Delimiter, int LineLength) SepLine(string text)
    {
        if (!text.StartsWith("sep=", StringComparison.OrdinalIgnoreCase) || text.Length < 5) return (null, 0);
        int end = text.IndexOfAny(['\r', '\n']);
        string line = end < 0 ? text : text[..end];
        if (line.Length != 5) return (null, 0);
        int length = line.Length;
        if (end >= 0)
        {
            length++;
            if (text[end] == '\r' && end + 1 < text.Length && text[end + 1] == '\n') length++;
        }
        return (line[4], length);
    }

    /// <summary>
    /// The delimiter that splits the first rows into the same number of columns most often (more columns win a tie).
    /// </summary>
    public static char DetectDelimiter(string sample, string extension)
    {
        char fallback = extension.Equals(".tsv", StringComparison.OrdinalIgnoreCase) || extension.Equals(".tab", StringComparison.OrdinalIgnoreCase)
            ? '\t' : ';';
        char best = fallback;
        double bestScore = 0;
        foreach (char delimiter in Delimiters)
        {
            var counts = new List<int>();
            using (var reader = new StringReader(sample))
            {
                foreach (var row in ReadRows(reader, delimiter))
                {
                    if (row.Length == 0) continue;
                    counts.Add(row.Length);
                    if (counts.Count > 60) break;
                }
            }
            // The sample may cut the last row short.
            if (counts.Count > 2) counts.RemoveAt(counts.Count - 1);
            if (counts.Count == 0) continue;
            var mode = counts.GroupBy(c => c).OrderByDescending(g => g.Count()).ThenByDescending(g => g.Key).First();
            if (mode.Key <= 1) continue;
            double score = (double)mode.Count() / counts.Count * 1000 + Math.Min(mode.Key, 500);
            if (delimiter == fallback) score += 0.5;
            if (score > bestScore)
            {
                best = delimiter;
                bestScore = score;
            }
        }
        return best;
    }
}
