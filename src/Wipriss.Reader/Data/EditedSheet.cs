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
    private readonly Dictionary<(int Row, int Column), CellFormat> _formats = [];
    private readonly HashSet<int> _formatRows = [];
    private List<CellRange>? _merges; // null: the file's merges
    private readonly List<Step> _undo = [], _redo = [];
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

    public int ChangeCount => _edits.Count + _formats.Count + (MergesChanged ? 1 : 0);

    /// <summary>Cells were merged or split since the file was read.</summary>
    public bool MergesChanged => _merges is not null;

    public bool CanUndo => _undo.Count > 0;

    public bool CanRedo => _redo.Count > 0;

    public override int DataRowCount => _dataRows;

    public override int DataColumnCount => _dataColumns;

    public override int RowCount => _editing ? _dataRows + Math.Max(MinSpareRows, FillRows - _dataRows) : _dataRows;

    public override int ColumnCount =>
        _editing ? Math.Min(MaxColumns, _dataColumns + Math.Max(MinSpareColumns, FillColumns - _dataColumns)) : _dataColumns;

    public override IReadOnlyList<CellRange> Merges => _merges ?? Inner.Merges;

    public override CellFormat Format(int row, int column) =>
        _formats.TryGetValue((row, column), out var format) ? format : Inner.Format(row, column);

    public override double DefaultFontSize => Inner.DefaultFontSize;

    public override (double Points, bool Custom)? RowHeight(int row) => Inner.RowHeight(row);

    public override bool RowMayWrap(int row) => _formatRows.Contains(row) || Inner.RowMayWrap(row);

    /// <summary>The cells whose formatting changed: their new look.</summary>
    public IReadOnlyDictionary<(int Row, int Column), CellFormat> FormatEdits => _formats;

    public bool IsFormatEdited(int row, int column) => _formats.ContainsKey((row, column));

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

    /// <summary>
    /// Changes the formatting of cells as one step. Cells beyond Excel's limits are skipped. Returns how many
    /// cells really changed.
    /// </summary>
    public int SetFormat(IEnumerable<(int Row, int Column)> cells, Func<CellFormat, CellFormat> change)
    {
        var changes = new List<FormatChange>();
        var seen = new HashSet<(int, int)>();
        foreach (var (row, column) in cells)
        {
            if (row < 0 || column < 0 || row >= MaxRows || column >= MaxColumns || !seen.Add((row, column))) continue;
            bool had = _formats.TryGetValue((row, column), out var before);
            var old = had ? before : Inner.Format(row, column);
            var now = change(old);
            if (now == old) continue;
            ApplyFormat(row, column, now);
            bool has = _formats.TryGetValue((row, column), out var after);
            changes.Add(new FormatChange(row, column, had, before, has, after));
        }
        if (changes.Count == 0) return 0;
        Push(new Step([], [.. changes], null));
        return changes.Count;
    }

    /// <summary>
    /// Merges a range into one cell. Merged areas it touches become part of it; the text of every cell but the
    /// first (top left) is cleared, like Excel does. Returns the merged range, or null when nothing changed.
    /// </summary>
    public CellRange? Merge(CellRange range)
    {
        var merges = Merges.ToList();
        range = Grow(range, merges);
        if (range.FromRow == range.ToRow && range.FromColumn == range.ToColumn) return null;
        if (merges.Contains(range)) return null;

        var texts = new List<Change>();
        for (int r = range.FromRow; r <= range.ToRow; r++)
        {
            for (int c = range.FromColumn; c <= range.ToColumn; c++)
            {
                if (r == range.FromRow && c == range.FromColumn) continue;
                bool had = _edits.TryGetValue((r, c), out var before);
                if ((had ? before : Inner.Text(r, c)) is null) continue;
                Apply(r, c, null);
                bool has = _edits.TryGetValue((r, c), out var after);
                texts.Add(new Change(r, c, had, before, has, after));
            }
        }
        var old = _merges;
        SetMerges(merges.Where(m => !Intersects(m, range)).Append(range).ToList());
        Push(new Step([.. texts], [], new MergeChange(old, _merges, range)));
        return range;
    }

    /// <summary>Splits every merged area that touches the range. Returns false when there was none.</summary>
    public bool Unmerge(CellRange range)
    {
        var merges = Merges.ToList();
        var next = merges.Where(m => !Intersects(m, range)).ToList();
        if (next.Count == merges.Count) return false;
        var old = _merges;
        SetMerges(next);
        Push(new Step([], [], new MergeChange(old, _merges, merges.First(m => Intersects(m, range)))));
        return true;
    }

    /// <summary>The range grown until no merged area sticks out of it (what <see cref="Merge"/> merges).</summary>
    public CellRange MergeArea(CellRange range) => Grow(range, Merges);

    /// <summary>How many cells of the range other than its first one hold text (merging clears them).</summary>
    public int TextsLostByMerge(CellRange range)
    {
        int count = 0;
        for (int r = range.FromRow; r <= range.ToRow; r++)
            for (int c = range.FromColumn; c <= range.ToColumn; c++)
                if ((r != range.FromRow || c != range.FromColumn) && Text(r, c) is not null) count++;
        return count;
    }

    private static CellRange Grow(CellRange range, IReadOnlyList<CellRange> merges)
    {
        bool grown;
        do
        {
            grown = false;
            foreach (var m in merges)
            {
                if (!Intersects(m, range)) continue;
                var union = new CellRange(Math.Min(m.FromRow, range.FromRow), Math.Min(m.FromColumn, range.FromColumn),
                    Math.Max(m.ToRow, range.ToRow), Math.Max(m.ToColumn, range.ToColumn));
                if (union != range)
                {
                    range = union;
                    grown = true;
                }
            }
        }
        while (grown);
        return range;
    }

    public static bool Intersects(CellRange a, CellRange b) =>
        a.FromRow <= b.ToRow && b.FromRow <= a.ToRow && a.FromColumn <= b.ToColumn && b.FromColumn <= a.ToColumn;

    /// <summary>Keeps the new list, or goes back to the file's merges when it is the same as them.</summary>
    private void SetMerges(List<CellRange> merges) =>
        _merges = merges.Count == Inner.Merges.Count && merges.ToHashSet().SetEquals(Inner.Merges) ? null : merges;

    private void ApplyFormat(int row, int column, CellFormat format)
    {
        if (Inner.Format(row, column) == format) _formats.Remove((row, column));
        else
        {
            _formats[(row, column)] = format;
            _formatRows.Add(row);
        }
    }

    private void Push(Step step)
    {
        _undo.Add(step);
        if (_undo.Count > 200) _undo.RemoveAt(0);
        _redo.Clear();
        Recount();
    }

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
        Push(new Step([.. changes], [], null));
        return changes.Count;
    }

    /// <summary>Takes the last step back. Returns the first cell of it to show, or null.</summary>
    public (int Row, int Column)? Undo() => Walk(_undo, _redo, back: true);

    public (int Row, int Column)? Redo() => Walk(_redo, _undo, back: false);

    /// <summary>Forgets every change (after saving, or when the owner throws them away).</summary>
    public void Clear()
    {
        _edits.Clear();
        _formats.Clear();
        _formatRows.Clear();
        _merges = null;
        _undo.Clear();
        _redo.Clear();
        Recount();
    }

    private (int Row, int Column)? Walk(List<Step> from, List<Step> to, bool back)
    {
        if (from.Count == 0) return null;
        var step = from[^1];
        from.RemoveAt(from.Count - 1);
        foreach (var change in step.Texts)
        {
            bool has = back ? change.HadBefore : change.HasAfter;
            string? text = back ? change.Before : change.After;
            if (has) _edits[(change.Row, change.Column)] = text;
            else _edits.Remove((change.Row, change.Column));
        }
        foreach (var change in step.Formats)
        {
            bool has = back ? change.HadBefore : change.HasAfter;
            var format = back ? change.Before : change.After;
            if (has)
            {
                _formats[(change.Row, change.Column)] = format;
                _formatRows.Add(change.Row);
            }
            else _formats.Remove((change.Row, change.Column));
        }
        if (step.Merges is { } merges) _merges = back ? merges.Before : merges.After;
        to.Add(step);
        Recount();
        if (step.Merges is { } m) return (m.Area.FromRow, m.Area.FromColumn);
        if (step.Texts.Length > 0) return (step.Texts[0].Row, step.Texts[0].Column);
        return (step.Formats[0].Row, step.Formats[0].Column);
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

    private readonly record struct FormatChange(int Row, int Column, bool HadBefore, CellFormat Before, bool HasAfter, CellFormat After);

    /// <summary>The merges before and after (null: the file's), and the area merged or split (to show it).</summary>
    private sealed record MergeChange(List<CellRange>? Before, List<CellRange>? After, CellRange Area);

    /// <summary>One step for Ctrl+Z: texts, formats and merges changed together.</summary>
    private sealed record Step(Change[] Texts, FormatChange[] Formats, MergeChange? Merges);
}
