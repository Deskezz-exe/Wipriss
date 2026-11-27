namespace Wipriss.Reader.Data;

/// <summary>
/// A sheet with the owner's changes on top: the file stays as it is until it is saved. While editing, spare empty
/// rows and columns are shown after the table (like Google Sheets) so new data can be typed in; they hold nothing
/// and never reach the file. The table is as big as its last filled cell: typing into a spare cell makes the table
/// grow to it, and new spare rows and columns appear after it.
/// </summary>
internal sealed class EditedSheet : Sheet, IDisposable
{
    /// <summary>A small table is padded to about this many rows and columns, a big one gets at least the minimum.</summary>
    private const int FillRows = 100, MinSpareRows = 30;
    private const int FillColumns = 26, MinSpareColumns = 5;
    /// <summary>Excel's limits: a cell further away can't be saved.</summary>
    public const int MaxRows = 1_048_576, MaxColumns = 16_384;

    private readonly Dictionary<(int Row, int Column), string?> _edits = [];
    private readonly List<Change[]> _undo = [], _redo = [];
    private int _dataRows, _dataColumns;
    private bool _editing;

    public EditedSheet(Sheet inner) : base(inner.Name)
    {
        Inner = inner;
        Hidden = inner.Hidden;
        Recount();
    }

    public Sheet Inner { get; }

    /// <summary>Cells the file does not let change (part of an array formula, a smart table's header), with why.</summary>
    public Dictionary<(int Row, int Column), string>? Locks { get; set; }

    /// <summary>Spare rows and columns are shown only while editing.</summary>
    public bool Editing
    {
        get => _editing;
        set => _editing = value;
    }

    public int ChangeCount => _edits.Count;

    public bool CanUndo => _undo.Count > 0;

    public bool CanRedo => _redo.Count > 0;

    public override int DataRowCount => _dataRows;

    public override int DataColumnCount => _dataColumns;

    public override int RowCount => _editing ? _dataRows + Math.Max(MinSpareRows, FillRows - _dataRows) : _dataRows;

    public override int ColumnCount =>
        _editing ? Math.Min(MaxColumns, _dataColumns + Math.Max(MinSpareColumns, FillColumns - _dataColumns)) : _dataColumns;

    public override IReadOnlyList<CellRange> Merges => Inner.Merges;

    public override bool NumbersFromText => Inner.NumbersFromText;

    public override double? WidthHint(int column) => Inner.WidthHint(column);

    public override string? Text(int row, int column) =>
        _edits.TryGetValue((row, column), out var text) ? text : Inner.Text(row, column);

    public override bool TryNumber(int row, int column, out double value)
    {
        if (_edits.TryGetValue((row, column), out var text)) return TryParseLooseNumber(text, out value);
        return Inner.TryNumber(row, column, out value);
    }

    public override bool IsEdited(int row, int column) => _edits.ContainsKey((row, column));

    /// <summary>The changed cells: new text, or null for a cleared cell.</summary>
    public IReadOnlyDictionary<(int Row, int Column), string?> Edits => _edits;

    public string? LockReason(int row, int column) => Locks is { } locks && locks.TryGetValue((row, column), out var why) ? why : null;

    /// <summary>
    /// Changes cells as one step that Ctrl+Z takes back. Text that is empty or only spaces clears the cell.
    /// Locked cells and cells beyond Excel's limits are skipped. Returns how many cells really changed.
    /// </summary>
    public int Set(IEnumerable<(int Row, int Column, string? Text)> cells, out int locked)
    {
        locked = 0;
        var changes = new List<Change>();
        var seen = new HashSet<(int, int)>();
        foreach (var (row, column, raw) in cells)
        {
            if (row < 0 || column < 0 || row >= MaxRows || column >= MaxColumns || !seen.Add((row, column))) continue;
            if (LockReason(row, column) is not null)
            {
                locked++;
                continue;
            }
            string? text = string.IsNullOrWhiteSpace(raw) ? null : raw;
            bool had = _edits.TryGetValue((row, column), out var before);
            if ((had ? before : Inner.Text(row, column)) == text) continue;
            var change = new Change(row, column, had, before, false, null);
            Apply(row, column, text);
            bool has = _edits.TryGetValue((row, column), out var after);
            changes.Add(change with { HasAfter = has, After = after });
        }
        if (changes.Count == 0) return 0;
        _undo.Add([.. changes]);
        if (_undo.Count > 200) _undo.RemoveAt(0);
        _redo.Clear();
        Recount();
        return changes.Count;
    }

    /// <summary>Takes the last step back. Returns the first cell of it to show, or null.</summary>
    public (int Row, int Column)? Undo() => Step(_undo, _redo, back: true);

    public (int Row, int Column)? Redo() => Step(_redo, _undo, back: false);

    /// <summary>Forgets every change (after saving, or when the owner throws them away).</summary>
    public void Clear()
    {
        _edits.Clear();
        _undo.Clear();
        _redo.Clear();
        Recount();
    }

    private (int Row, int Column)? Step(List<Change[]> from, List<Change[]> to, bool back)
    {
        if (from.Count == 0) return null;
        var step = from[^1];
        from.RemoveAt(from.Count - 1);
        foreach (var change in step)
        {
            bool has = back ? change.HadBefore : change.HasAfter;
            string? text = back ? change.Before : change.After;
            if (has) _edits[(change.Row, change.Column)] = text;
            else _edits.Remove((change.Row, change.Column));
        }
        to.Add(step);
        Recount();
        return (step[0].Row, step[0].Column);
    }

    /// <summary>Stores the text as a change, or drops the change when the file already has that text.</summary>
    private void Apply(int row, int column, string? text)
    {
        if (Inner.Text(row, column) == text) _edits.Remove((row, column));
        else _edits[(row, column)] = text;
    }

    private void Recount()
    {
        int rows = Inner.RowCount, columns = Inner.ColumnCount;
        foreach (var ((row, column), text) in _edits)
        {
            if (text is null) continue;
            if (row >= rows) rows = row + 1;
            if (column >= columns) columns = column + 1;
        }
        _dataRows = rows;
        _dataColumns = columns;
    }

    public override IEnumerable<string?[]> Rows(CancellationToken cancel)
    {
        int columns = ColumnCount, rows = RowCount;
        Dictionary<int, List<(int Column, string? Text)>>? byRow = null;
        if (_edits.Count > 0)
        {
            byRow = [];
            foreach (var ((row, column), text) in _edits)
            {
                if (!byRow.TryGetValue(row, out var list)) byRow[row] = list = [];
                list.Add((column, text));
            }
        }
        int r = 0;
        foreach (var source in Inner.Rows(cancel))
        {
            if (r >= rows) yield break;
            yield return Patch(source, r++);
        }
        for (; r < rows; r++)
        {
            cancel.ThrowIfCancellationRequested();
            yield return Patch([], r);
        }

        string?[] Patch(string?[] row, int index)
        {
            if (byRow is null || !byRow.TryGetValue(index, out var list)) return row;
            var patched = new string?[Math.Max(row.Length, columns)];
            row.CopyTo(patched, 0);
            foreach (var (column, text) in list)
            {
                if (column < patched.Length) patched[column] = text;
            }
            return patched;
        }
    }

    public void Dispose() => (Inner as IDisposable)?.Dispose();

    private readonly record struct Change(int Row, int Column, bool HadBefore, string? Before, bool HasAfter, string? After);
}
