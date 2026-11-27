namespace Wipriss.Reader.Data;

/// <summary>
/// A sheet held in memory, filled once by a loader and read-only afterwards. Each row keeps only up to its last
/// non-empty cell; numbers sit in a parallel array that exists only for rows that have numbers.
/// </summary>
internal sealed class MemorySheet : Sheet
{
    private readonly List<string?[]> _texts = [];
    private readonly List<double[]?> _numbers = [];
    private readonly List<CellRange> _merges = [];
    private double?[] _widths = [];
    private int _columns;

    public MemorySheet(string name) : base(name) { }

    public override int RowCount => _texts.Count;

    public override int ColumnCount => _columns;

    public override IReadOnlyList<CellRange> Merges => _merges;

    public override bool NumbersFromText => false;

    /// <summary>Adds a row. <paramref name="numbers"/> holds NaN where a cell is not a number; null if none is.</summary>
    public void AddRow(string?[] texts, double[]? numbers)
    {
        int used = texts.Length;
        while (used > 0 && texts[used - 1] is null) used--;
        if (used < texts.Length) texts = texts[..used];
        if (numbers is not null)
        {
            if (numbers.Length > used) numbers = numbers[..used];
            if (Array.TrueForAll(numbers, double.IsNaN)) numbers = null;
        }
        _texts.Add(texts);
        _numbers.Add(numbers);
        if (used > _columns) _columns = used;
    }

    public void AddMerge(CellRange range) => _merges.Add(range);

    public void SetWidthHints(double?[] widths) => _widths = widths;

    /// <summary>Drops empty rows at the end (files often carry formatting far below the data).</summary>
    public void TrimEnd()
    {
        int last = _texts.Count;
        while (last > 0 && _texts[last - 1].Length == 0) last--;
        if (last < _texts.Count)
        {
            _texts.RemoveRange(last, _texts.Count - last);
            _numbers.RemoveRange(last, _numbers.Count - last);
        }
        _merges.RemoveAll(m => m.FromRow >= last || m.FromColumn >= Math.Max(_columns, 1));
    }

    public override string? Text(int row, int column)
    {
        if ((uint)row >= (uint)_texts.Count) return null;
        var texts = _texts[row];
        return (uint)column < (uint)texts.Length ? texts[column] : null;
    }

    public override bool TryNumber(int row, int column, out double value)
    {
        value = double.NaN;
        if ((uint)row >= (uint)_numbers.Count || _numbers[row] is not { } numbers || (uint)column >= (uint)numbers.Length)
            return false;
        value = numbers[column];
        return !double.IsNaN(value);
    }

    public override double? WidthHint(int column) => (uint)column < (uint)_widths.Length ? _widths[column] : null;

    public override IEnumerable<string?[]> Rows(CancellationToken cancel)
    {
        foreach (var row in _texts)
        {
            cancel.ThrowIfCancellationRequested();
            yield return row;
        }
    }
}
