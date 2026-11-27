namespace Wipriss.Reader.Data;

internal enum HAlign : byte { General, Left, Center, Right }

internal enum VAlign : byte { Bottom, Center, Top }

/// <summary>
/// How a text longer than its cell is shown: cut with "…" (the default here), wrapped onto more lines (the row grows),
/// or shown whole over the empty cells next to it, up to about three cells wide.
/// </summary>
internal enum TextMode : byte { Clip, Wrap, Overflow }

/// <summary>
/// The look of one cell, as far as the viewer shows and changes it. <see cref="Size"/> is in points, 0 for the
/// workbook's usual size. Colors are 0xAARRGGBB; null is automatic (text) or none (fill).
/// </summary>
internal readonly record struct CellFormat(
    double Size = 0,
    bool Bold = false,
    bool Italic = false,
    bool Underline = false,
    uint? Color = null,
    uint? Fill = null,
    HAlign Horizontal = HAlign.General,
    VAlign Vertical = VAlign.Bottom,
    TextMode Mode = TextMode.Clip)
{
    public static readonly CellFormat Plain = new();

    public bool IsPlain => this == Plain;
}

/// <summary>
/// The formatting of one sheet read from an XLSX: the workbook's cell styles and, for each cell, which one it uses.
/// Rows keep a small array of style numbers only when they have styled cells.
/// </summary>
internal sealed class SheetFormats
{
    private readonly CellFormat[] _styles;
    private readonly List<ushort[]?> _rows = [];
    private readonly Dictionary<int, (double Points, bool Custom)> _heights = [];
    private readonly HashSet<int> _wrapRows = [];

    public SheetFormats(CellFormat[] styles, double defaultSize)
    {
        _styles = styles;
        DefaultSize = defaultSize > 0 ? defaultSize : 11;
    }

    /// <summary>The usual font size of the workbook, in points (its first font).</summary>
    public double DefaultSize { get; }

    /// <summary>The usual row height of the sheet in points, when the file says it.</summary>
    public double? DefaultRowHeight { get; set; }

    public CellFormat this[int row, int column]
    {
        get
        {
            if ((uint)row >= (uint)_rows.Count || _rows[row] is not { } styles || (uint)column >= (uint)styles.Length) return CellFormat.Plain;
            int s = styles[column];
            return (uint)s < (uint)_styles.Length ? _styles[s] : CellFormat.Plain;
        }
    }

    public (double Points, bool Custom)? RowHeight(int row) => _heights.TryGetValue(row, out var h) ? h : null;

    public bool RowHasWrap(int row) => _wrapRows.Contains(row);

    internal void SetCell(int row, int column, int style)
    {
        if (style <= 0 || style >= _styles.Length || style > ushort.MaxValue || column >= EditedSheet.MaxColumns) return;
        if (_styles[style].IsPlain) return;
        while (_rows.Count <= row) _rows.Add(null);
        var cells = _rows[row];
        if (cells is null || cells.Length <= column)
        {
            var grown = new ushort[Math.Max(column + 1, Math.Min(EditedSheet.MaxColumns, (cells?.Length ?? 0) * 2))];
            cells?.CopyTo(grown, 0);
            _rows[row] = cells = grown;
        }
        cells[column] = (ushort)style;
        if (_styles[style].Mode == TextMode.Wrap) _wrapRows.Add(row);
    }

    internal void SetRowHeight(int row, double points, bool custom) => _heights[row] = (points, custom);
}
