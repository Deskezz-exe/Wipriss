using System.Globalization;

namespace Wipriss.Reader.Data;

/// <summary>A rectangle of merged cells, zero-based and inclusive.</summary>
internal readonly record struct CellRange(int FromRow, int FromColumn, int ToRow, int ToColumn)
{
    public bool Contains(int row, int column) =>
        row >= FromRow && row <= ToRow && column >= FromColumn && column <= ToColumn;
}

/// <summary>
/// One sheet of a table file, read-only. Rows and columns are zero-based. <see cref="Text"/> is what the file
/// shows (already formatted); numbers are kept apart for right alignment and sorting.
/// Reads may come from the UI thread while a CSV file is still being indexed, so implementations are thread-safe.
/// </summary>
internal abstract class Sheet
{
    protected Sheet(string name) => Name = name;

    public string Name { get; }

    /// <summary>A hidden sheet of a workbook: still shown, but marked.</summary>
    public bool Hidden { get; init; }

    public abstract int RowCount { get; }

    public abstract int ColumnCount { get; }

    /// <summary>The shown text of a cell, or null when it is empty or outside the sheet.</summary>
    public abstract string? Text(int row, int column);

    /// <summary>The number behind a cell (also dates, as OLE automation dates), for alignment and sorting.</summary>
    public abstract bool TryNumber(int row, int column, out double value);

    /// <summary>Rows that hold the table; the rest (up to <see cref="RowCount"/>) are spare rows shown for typing.</summary>
    public virtual int DataRowCount => RowCount;

    /// <summary>Columns that hold the table; the rest are spare columns shown for typing.</summary>
    public virtual int DataColumnCount => ColumnCount;

    /// <summary>A cell changed by the owner and not saved yet.</summary>
    public virtual bool IsEdited(int row, int column) => false;

    /// <summary>
    /// True when <see cref="TryNumber"/> only parses <see cref="Text"/> (a text table): sorting then reads numbers
    /// from the streamed rows instead of asking cell by cell.
    /// </summary>
    public virtual bool NumbersFromText => true;

    public virtual IReadOnlyList<CellRange> Merges => [];

    /// <summary>Column width saved in the file, in characters, or null.</summary>
    public virtual double? WidthHint(int column) => null;

    /// <summary>
    /// The text of every row in one column, in order, for sorting and searching. A CSV sheet streams the file
    /// instead of going through its row cache.
    /// </summary>
    public virtual IEnumerable<string?[]> Rows(CancellationToken cancel)
    {
        int rows = RowCount, columns = ColumnCount;
        for (int r = 0; r < rows; r++)
        {
            cancel.ThrowIfCancellationRequested();
            var row = new string?[columns];
            for (int c = 0; c < columns; c++) row[c] = Text(r, c);
            yield return row;
        }
    }

    /// <summary>Spreadsheet column name: 0 → A, 25 → Z, 26 → AA.</summary>
    public static string ColumnName(int column)
    {
        Span<char> buffer = stackalloc char[8];
        int i = buffer.Length;
        int n = column + 1;
        while (n > 0 && i > 0)
        {
            n--;
            buffer[--i] = (char)('A' + n % 26);
            n /= 26;
        }
        return new string(buffer[i..]);
    }

    /// <summary>
    /// Reads a number the way people type it in a CSV: "1234", "-1 234,5", "1,234.5", "12%", "3,5 ₽", "1e3".
    /// A lone comma or dot is the decimal point (Russian style for the comma); with both, the last one is.
    /// Thousands separators (space, apostrophe, the other mark) need groups of exactly three digits, so dates
    /// ("10.02.2026") and phone numbers ("8 999 123 45 67") stay text.
    /// </summary>
    public static bool TryParseLooseNumber(string? text, out double value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text) || text.Length > 40) return false;
        var span = text.AsSpan().Trim();
        // Currency marks around the number.
        span = span.Trim("₽$€£¥ \u00A0\u202F");
        if (span.EndsWith("руб.", StringComparison.OrdinalIgnoreCase)) span = span[..^4].TrimEnd();
        else if (span.EndsWith("р.", StringComparison.OrdinalIgnoreCase)) span = span[..^2].TrimEnd();
        bool percent = false;
        if (span.EndsWith("%"))
        {
            percent = true;
            span = span[..^1].TrimEnd();
        }
        if (span.IsEmpty) return false;

        bool negative = false;
        if (span[0] is '-' or '+' or '\u2212')
        {
            negative = span[0] != '+';
            span = span[1..];
        }

        // Exponent: "1,5e-3".
        int exponent = 0;
        int e = span.IndexOfAny('e', 'E');
        if (e >= 0)
        {
            if (!int.TryParse(span[(e + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out exponent)) return false;
            span = span[..e];
        }
        if (span.IsEmpty) return false;

        int commas = 0, dots = 0;
        foreach (char ch in span)
        {
            if (ch == ',') commas++;
            else if (ch == '.') dots++;
        }
        char decimalMark = '\0';
        if (commas > 0 && dots > 0) decimalMark = span.LastIndexOf(',') > span.LastIndexOf('.') ? ',' : '.';
        else if (commas == 1) decimalMark = ',';
        else if (dots == 1) decimalMark = '.';

        int point = decimalMark == '\0' ? -1 : span.LastIndexOf(decimalMark);
        var whole = point >= 0 ? span[..point] : span;
        var fraction = point >= 0 ? span[(point + 1)..] : [];
        if (whole.IsEmpty && fraction.IsEmpty) return false;
        foreach (char ch in fraction)
        {
            if (!char.IsAsciiDigit(ch)) return false;
        }

        // The whole part: digits in groups; every group after a separator has exactly three digits.
        Span<char> digits = stackalloc char[whole.Length + fraction.Length + 1];
        int n = 0, group = 0, groups = 0;
        foreach (char ch in whole)
        {
            if (char.IsAsciiDigit(ch))
            {
                digits[n++] = ch;
                group++;
            }
            else if (ch is ' ' or '\u00A0' or '\u202F' or '\'' or ',' or '.')
            {
                if (ch == decimalMark || group == 0 || (groups == 0 ? group > 3 : group != 3)) return false;
                groups++;
                group = 0;
            }
            else return false;
        }
        if (groups > 0 && group != 3) return false;
        if (n == 0 && fraction.IsEmpty) return false;
        if (n == 0) digits[n++] = '0';
        if (!fraction.IsEmpty)
        {
            digits[n++] = '.';
            fraction.CopyTo(digits[n..]);
            n += fraction.Length;
        }
        if (!double.TryParse(digits[..n], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value)) return false;
        if (exponent != 0) value *= Math.Pow(10, exponent);
        if (negative) value = -value;
        if (percent) value /= 100;
        return double.IsFinite(value);
    }
}