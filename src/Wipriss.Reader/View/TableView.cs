using System.Globalization;
using Wipriss.Reader.Data;

namespace Wipriss.Reader.View;

/// <summary>
/// What the grid shows of a sheet: the top rows pinned (the header, with a report title above it if there is one),
/// the order of the other rows (sorted, or only the rows with search hits) and the search hits. View rows are
/// numbered from 0 below the pinned rows; the pinned rows are view rows -PinnedRows … -1.
/// </summary>
internal sealed class TableView
{
    /// <summary>More pinned rows would eat the screen: a header is looked for only this far down.</summary>
    public const int MaxPinnedRows = 5;

    private int[]? _order;
    private Dictionary<int, int[]>? _matches;

    public TableView(Sheet sheet, int pinnedRows)
    {
        Sheet = sheet;
        PinnedRows = Math.Clamp(pinnedRows, 0, Math.Min(MaxPinnedRows, Math.Max(0, sheet.RowCount - 1)));
    }

    public Sheet Sheet { get; }

    /// <summary>How many top rows stay on screen while scrolling (0 = none).</summary>
    public int PinnedRows { get; }

    public bool PinHeader => PinnedRows > 0;

    public int SortColumn { get; private set; } = -1;

    public bool SortDescending { get; private set; }

    public bool OnlyMatches { get; private set; }

    public int MatchCount { get; private set; }

    public bool HasMatches => _matches is { Count: > 0 };

    /// <summary>Merged cells are drawn only while rows keep their places.</summary>
    public bool KeepsFileOrder => _order is null;

    public int ColumnCount => Sheet.ColumnCount;

    public int RowCount => _order?.Length ?? Math.Max(0, Sheet.RowCount - PinnedRows);

    public int FirstSheetRow => PinnedRows;

    public int SheetRow(int viewRow)
    {
        if (viewRow < 0) return viewRow + PinnedRows; // a pinned row
        return _order is { } order ? order[viewRow] : viewRow + PinnedRows;
    }

    /// <summary>The view row of a sheet row, or int.MinValue when that row is not shown now.</summary>
    public int ViewRow(int sheetRow)
    {
        if (sheetRow < PinnedRows) return sheetRow - PinnedRows;
        if (_order is null) return sheetRow - PinnedRows;
        return Array.IndexOf(_order, sheetRow) is var i and >= 0 ? i : int.MinValue;
    }

    public bool IsMatch(int sheetRow, int column) =>
        _matches is { } matches && matches.TryGetValue(sheetRow, out var columns) && Array.BinarySearch(columns, column) >= 0;

    public IReadOnlyList<int>? MatchColumns(int sheetRow) =>
        _matches is { } matches && matches.TryGetValue(sheetRow, out var columns) ? columns : null;

    /// <summary>
    /// A guess at how many top rows to pin: the header is the first row (of the top few) that names the columns,
    /// with text in at least two cells and no numbers, under at most a title and empty rows. 0 when there is none.
    /// </summary>
    public static int GuessPinnedRows(Sheet sheet)
    {
        if (sheet.RowCount < 3 || sheet.ColumnCount < 2) return 0;
        int columns = Math.Min(sheet.ColumnCount, 200);
        for (int r = 0; r < MaxPinnedRows && r + 2 < sheet.RowCount; r++)
        {
            int filled = 0;
            bool numbers = false;
            for (int c = 0; c < columns; c++)
            {
                if (sheet.Text(r, c) is not { Length: > 0 } text) continue;
                filled++;
                if (sheet.TryNumber(r, c, out _) || text.Length > 80) numbers = true;
            }
            if (filled <= 1) continue; // a title or an empty row above the header
            if (numbers) return 0;
            int next = 0;
            for (int c = 0; c < columns; c++)
            {
                if (sheet.Text(r + 1, c) is { Length: > 0 }) next++;
            }
            return next > 0 && filled * 2 >= next ? r + 1 : 0;
        }
        return 0;
    }
    // ---- Sorting and the search filter: computed off the UI thread, then applied at once ----

    /// <summary>The new row order for a sort and/or the search filter. Null when the file order stays.</summary>
    public int[]? ComputeOrder(int sortColumn, bool descending, bool onlyMatches, Dictionary<int, int[]>? matches, CancellationToken cancel)
    {
        int first = FirstSheetRow;
        int rows = Sheet.RowCount - first;
        if (rows <= 0) return null;
        int[]? order = null;

        if (sortColumn >= 0)
        {
            var numbers = new double[rows];
            var texts = new string?[rows];
            bool numbersInText = Sheet is not MemorySheet;
            int r = 0;
            foreach (var row in Sheet.Rows(cancel))
            {
                if (r - first >= rows) break;
                if (r >= first)
                {
                    int i = r - first;
                    string? text = sortColumn < row.Length ? row[sortColumn] : null;
                    texts[i] = text;
                    numbers[i] = numbersInText
                        ? (Sheet.TryParseLooseNumber(text, out double n) ? n : double.NaN)
                        : (Sheet.TryNumber(r, sortColumn, out double m) ? m : double.NaN);
                }
                r++;
            }
            cancel.ThrowIfCancellationRequested();
            var comparer = TextComparer;
            order = new int[rows];
            for (int i = 0; i < rows; i++) order[i] = i;
            int sign = descending ? -1 : 1;
            Array.Sort(order, (a, b) =>
            {
                // Empty cells always go last, numbers before text.
                bool emptyA = texts[a] is null, emptyB = texts[b] is null;
                if (emptyA || emptyB) return emptyA == emptyB ? a.CompareTo(b) : emptyA ? 1 : -1;
                bool numA = !double.IsNaN(numbers[a]), numB = !double.IsNaN(numbers[b]);
                int result;
                if (numA && numB) result = numbers[a].CompareTo(numbers[b]);
                else if (numA != numB) return numA ? -1 : 1;
                else result = comparer.Compare(texts[a], texts[b]);
                return result != 0 ? result * sign : a.CompareTo(b);
            });
            for (int i = 0; i < rows; i++) order[i] += first;
        }

        if (onlyMatches && matches is not null)
        {
            var source = order ?? Enumerable.Range(first, rows).ToArray();
            order = Array.FindAll(source, matches.ContainsKey);
        }
        return order;
    }

    public void Apply(int sortColumn, bool descending, bool onlyMatches, int[]? order)
    {
        SortColumn = sortColumn;
        SortDescending = descending;
        OnlyMatches = onlyMatches;
        _order = order;
    }

    public void SetMatches(Dictionary<int, int[]>? matches, int count)
    {
        _matches = matches;
        MatchCount = count;
    }

    /// <summary>Cells that contain the text (any case), by sheet row; at most <paramref name="limit"/> cells.</summary>
    public static (Dictionary<int, int[]> Matches, int Count) Find(Sheet sheet, string query, int limit, CancellationToken cancel)
    {
        var matches = new Dictionary<int, int[]>();
        int count = 0;
        int r = 0;
        var columns = new List<int>();
        foreach (var row in sheet.Rows(cancel))
        {
            columns.Clear();
            for (int c = 0; c < row.Length; c++)
            {
                if (row[c] is { } text && text.Contains(query, StringComparison.CurrentCultureIgnoreCase)) columns.Add(c);
            }
            if (columns.Count > 0)
            {
                matches[r] = columns.ToArray();
                count += columns.Count;
                if (count >= limit) break;
            }
            r++;
        }
        return (matches, count);
    }

    /// <summary>Russian-friendly order: case does not matter and "файл 2" comes before "файл 10".</summary>
    private static readonly StringComparer TextComparer =
        StringComparer.Create(CultureInfo.CurrentCulture, CompareOptions.IgnoreCase | CompareOptions.NumericOrdering);
}
