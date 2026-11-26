using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Wipriss.Reader.Data;

namespace Wipriss.Reader.View;

/// <summary>
/// The table itself, drawn by hand: only the cells on screen are drawn, every row has the same height, so millions
/// of rows scroll as fast as ten. Column letters on top, row numbers on the left, the pinned rows (header) under the
/// letters, zebra rows, the row under the mouse lit, numbers on the right, long text spilling into empty
/// neighbours like in Excel, merged cells, search hits. No timers: it redraws only on input or a change.
/// </summary>
internal sealed class TableGrid : FrameworkElement
{
    // Warm graphite, same family as the island's theme (Theme.xaml).
    private static readonly Brush Background = Frozen(0x13, 0x12, 0x10);
    private static readonly Brush StripeBrush = Frozen(0x17, 0x16, 0x14);
    private static readonly Brush HoverBrush = Frozen(0x1E, 0x1D, 0x1A);
    private static readonly Brush HeaderBrush = Frozen(0x1A, 0x19, 0x16);
    private static readonly Brush HeaderActiveBrush = Frozen(0x24, 0x2B, 0x25);
    private static readonly Brush PinnedBrush = Frozen(0x1D, 0x1C, 0x19);
    private static readonly Brush LineBrush = Frozen(0x25, 0x23, 0x20);
    private static readonly Brush StrongLineBrush = Frozen(0x3A, 0x38, 0x33);
    private static readonly Brush TextBrush = Frozen(0xEC, 0xE8, 0xE1);
    private static readonly Brush NumberBrush = Frozen(0xDD, 0xE4, 0xDA);
    private static readonly Brush SubtleBrush = Frozen(0x8E, 0x8A, 0x81);
    private static readonly Brush AccentBrush = Frozen(0xA6, 0xBE, 0xAB);
    private static readonly Brush SelectionBrush = Frozen(0x33, 0xA6, 0xBE, 0xAB);
    private static readonly Brush MatchBrush = Frozen(0x4A, 0x3E, 0x20);
    private static readonly Pen ActivePen = FrozenPen(AccentBrush, 2);
    private static readonly Pen SelectionPen = FrozenPen(Frozen(0x80, 0xA6, 0xBE, 0xAB), 1);

    private static readonly Typeface Regular = new(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private static readonly Typeface Bold = new(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    private const double BaseFont = 13;
    private const double BaseRow = 28;
    private const double MinColumn = 28;
    private const int SpillColumns = 8;

    private TableView? _view;
    private double[] _widths = []; // at zoom 1
    private double[] _lefts = [0]; // prefix sums at the current zoom
    private double _zoom = 1;
    private int _firstRow;
    private double _offsetX;
    private int _hoverRow = int.MinValue;

    private (int Row, int Column) _anchor;
    private (int Row, int Column) _active;
    private bool _selecting;
    private int _resizing = -1;
    private double _resizeStart, _resizeWidth;

    public TableGrid()
    {
        Focusable = true;
        FocusVisualStyle = null;
        ClipToBounds = true;
        SnapsToDevicePixels = true;
        RenderOptions.SetEdgeMode(this, EdgeMode.Aliased);
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        TextOptions.SetTextRenderingMode(this, TextRenderingMode.ClearType);
    }

    /// <summary>Scroll position, size or content changed: the window updates its scroll bars.</summary>
    public event EventHandler? Scrolled;

    /// <summary>The active cell moved: the window shows its full text.</summary>
    public event EventHandler? ActiveCellChanged;

    /// <summary>A column header was clicked: the window sorts by it.</summary>
    public event EventHandler<int>? HeaderClicked;

    public event EventHandler? ZoomChanged;

    public TableView? View => _view;

    public double Zoom
    {
        get => _zoom;
        set
        {
            double zoom = Math.Clamp(Math.Round(value, 2), 0.5, 3);
            if (zoom == _zoom) return;
            _zoom = zoom;
            UpdateLefts();
            ClampScroll();
            InvalidateVisual();
            Scrolled?.Invoke(this, EventArgs.Empty);
            ZoomChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public int FirstRow => _firstRow;

    public double OffsetX => _offsetX;

    public (int Row, int Column) Active => _active;

    private double FontSize => BaseFont * _zoom;

    private double RowHeight => Math.Round(BaseRow * _zoom);

    private double HeaderHeight => Math.Round(BaseRow * _zoom);

    private double Pad => Math.Round(8 * _zoom);

    private double PinnedHeight => (_view?.PinnedRows ?? 0) * RowHeight;

    private double TopOfRows => HeaderHeight + PinnedHeight;

    private double RowHeaderWidth
    {
        get
        {
            int rows = _view?.Sheet.RowCount ?? 1;
            int digits = Math.Max(2, rows.ToString(CultureInfo.InvariantCulture).Length);
            return Math.Round((digits * 7.4 + 22) * _zoom);
        }
    }

    public int VisibleRows => Math.Max(1, (int)((ActualHeight - TopOfRows) / RowHeight));

    public double DataWidth => Math.Max(0, ActualWidth - RowHeaderWidth);

    public double TotalWidth => _lefts[^1];

    /// <summary>Shows another sheet (or the same sheet in a new order). Keeps the widths when the columns stay.</summary>
    public void SetView(TableView view, bool keepLayout)
    {
        bool sameColumns = keepLayout && _view is not null && _view.Sheet == view.Sheet && _widths.Length == view.ColumnCount;
        _view = view;
        if (!sameColumns)
        {
            _widths = MeasureColumns(view);
            _firstRow = 0;
            _offsetX = 0;
            _anchor = _active = (0, 0);
        }
        UpdateLefts();
        ClampScroll();
        InvalidateVisual();
        Scrolled?.Invoke(this, EventArgs.Empty);
        ActiveCellChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Refresh() => InvalidateVisual();

    public void ScrollToRow(double row)
    {
        int first = (int)Math.Round(row);
        if (first == _firstRow) return;
        _firstRow = first;
        ClampScroll();
        InvalidateVisual();
        Scrolled?.Invoke(this, EventArgs.Empty);
    }

    public void ScrollToX(double x)
    {
        _offsetX = x;
        ClampScroll();
        InvalidateVisual();
        Scrolled?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Moves the active cell there and scrolls it into view.</summary>
    public void Select(int row, int column, bool extend = false)
    {
        if (_view is null) return;
        int minRow = -_view.PinnedRows;
        row = Math.Clamp(row, minRow, Math.Max(minRow, _view.RowCount - 1));
        column = Math.Clamp(column, 0, Math.Max(0, _view.ColumnCount - 1));
        _active = (row, column);
        if (!extend) _anchor = _active;
        BringIntoView(row, column);
        InvalidateVisual();
        ActiveCellChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SelectAll()
    {
        if (_view is null) return;
        _anchor = (-_view.PinnedRows, 0);
        _active = (Math.Max(0, _view.RowCount - 1), Math.Max(0, _view.ColumnCount - 1));
        InvalidateVisual();
        ActiveCellChanged?.Invoke(this, EventArgs.Empty);
    }

    public (int Top, int Left, int Bottom, int Right) Selection =>
        (Math.Min(_anchor.Row, _active.Row), Math.Min(_anchor.Column, _active.Column),
         Math.Max(_anchor.Row, _active.Row), Math.Max(_anchor.Column, _active.Column));

    /// <summary>The selected cells as tab-separated text (what Excel pastes as a table).</summary>
    public string? SelectionText(int maxCells = 2_000_000)
    {
        if (_view is null) return null;
        var (top, left, bottom, right) = Selection;
        long cells = (long)(bottom - top + 1) * (right - left + 1);
        if (cells > maxCells) return null;
        var text = new StringBuilder();
        for (int r = top; r <= bottom; r++)
        {
            int sheetRow = _view.SheetRow(r);
            for (int c = left; c <= right; c++)
            {
                if (c > left) text.Append('\t');
                string? value = _view.Sheet.Text(sheetRow, c);
                if (value is null) continue;
                // Tabs and line breaks inside a cell would break the table: quote the cell like Excel does.
                if (value.AsSpan().IndexOfAny('\t', '\n', '"') >= 0) text.Append('"').Append(value.Replace("\"", "\"\"")).Append('"');
                else text.Append(value);
            }
            if (r < bottom) text.Append("\r\n");
        }
        return text.ToString();
    }

    public int SelectionCellCount
    {
        get
        {
            var (top, left, bottom, right) = Selection;
            return (int)Math.Min(int.MaxValue, (long)(bottom - top + 1) * (right - left + 1));
        }
    }

    // ---- Column widths ----

    private static double[] MeasureColumns(TableView view)
    {
        var sheet = view.Sheet;
        int columns = sheet.ColumnCount;
        var widths = new double[columns];
        int sample = Math.Min(sheet.RowCount, 400);
        var glyphs = Regular.TryGetGlyphTypeface(out var g) ? g : null;
        var bold = Bold.TryGetGlyphTypeface(out var b) ? b : glyphs;
        // Text of cells merged across columns (report titles) must not make their first column wide.
        var titles = new HashSet<long>();
        foreach (var m in sheet.Merges)
        {
            if (m.ToColumn > m.FromColumn) titles.Add(CellKey(m.FromRow, m.FromColumn));
        }
        for (int c = 0; c < columns; c++)
        {
            double width = TextWidth(Sheet.ColumnName(c), glyphs, BaseFont);
            for (int r = 0; r < sample; r++)
            {
                if (sheet.Text(r, c) is not { } text) continue;
                if (titles.Contains(CellKey(r, c))) continue; // a title merged across columns
                bool header = r == view.PinnedRows - 1;
                width = Math.Max(width, TextWidth(FirstLine(text, 120), header ? bold : glyphs, BaseFont) + (header ? 14 : 0));
            }
            if (sheet.WidthHint(c) is { } hint) width = Math.Max(width, hint * 7.0 - 16);
            widths[c] = Math.Clamp(Math.Ceiling(width + 18), 52, 420);
        }
        return widths;
    }

    private static double TextWidth(string text, GlyphTypeface? glyphs, double size)
    {
        if (glyphs is null) return text.Length * size * 0.56;
        double width = 0;
        foreach (char ch in text)
        {
            width += glyphs.CharacterToGlyphMap.TryGetValue(ch, out ushort glyph)
                ? glyphs.AdvanceWidths[glyph] * size
                : size * 0.6;
        }
        return width;
    }

    /// <summary>Double click on a column border: as wide as the text on screen and in the first rows.</summary>
    private void FitColumn(int column)
    {
        if (_view is null || (uint)column >= (uint)_widths.Length) return;
        var glyphs = Regular.TryGetGlyphTypeface(out var g) ? g : null;
        double width = TextWidth(Sheet.ColumnName(column), glyphs, BaseFont);
        int last = Math.Min(_view.RowCount, _firstRow + VisibleRows + 1);
        IEnumerable<int> rows = Enumerable.Range(_firstRow, Math.Max(0, last - _firstRow)).Concat(Enumerable.Range(0, Math.Min(_view.RowCount, 200)));
        rows = rows.Concat(Enumerable.Range(-_view.PinnedRows, _view.PinnedRows));
        foreach (int r in rows.Distinct())
        {
            if (_view.Sheet.Text(_view.SheetRow(r), column) is { } text)
                width = Math.Max(width, TextWidth(FirstLine(text, 400), glyphs, BaseFont) + (r < 0 ? 14 : 0));
        }
        _widths[column] = Math.Clamp(Math.Ceiling(width + 20), 40, 1200);
        UpdateLefts();
        ClampScroll();
        InvalidateVisual();
        Scrolled?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateLefts()
    {
        var lefts = new double[_widths.Length + 1];
        for (int c = 0; c < _widths.Length; c++) lefts[c + 1] = lefts[c] + Math.Round(_widths[c] * _zoom);
        _lefts = lefts;
    }

    // ---- Scrolling ----

    private void ClampScroll()
    {
        int rows = _view?.RowCount ?? 0;
        _firstRow = Math.Clamp(_firstRow, 0, Math.Max(0, rows - VisibleRows + 1));
        _offsetX = Math.Clamp(_offsetX, 0, Math.Max(0, TotalWidth - DataWidth + 40));
    }

    private void BringIntoView(int row, int column)
    {
        if (row >= 0)
        {
            int visible = VisibleRows;
            if (row < _firstRow) _firstRow = row;
            else if (row >= _firstRow + visible) _firstRow = row - visible + 1;
        }
        if ((uint)column < (uint)_widths.Length)
        {
            double left = _lefts[column], right = _lefts[column + 1];
            if (left < _offsetX) _offsetX = left;
            else if (right > _offsetX + DataWidth) _offsetX = Math.Min(left, right - DataWidth);
        }
        ClampScroll();
        Scrolled?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        ClampScroll();
        Scrolled?.Invoke(this, EventArgs.Empty);
    }

    // ---- Drawing ----

    protected override void OnRender(DrawingContext dc)
    {
        double width = ActualWidth, height = ActualHeight;
        dc.DrawRectangle(Background, null, new Rect(0, 0, width, height));
        if (_view is not { } view || width <= 0 || height <= 0) return;

        double dip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        double rowH = RowHeight, headH = HeaderHeight, rowHeadW = RowHeaderWidth, top = TopOfRows;
        int pinned = view.PinnedRows;
        int visible = (int)Math.Ceiling((height - top) / rowH) + 1;
        int lastRow = Math.Min(view.RowCount, _firstRow + visible);
        var (firstCol, lastCol) = VisibleColumns();

        // --- Data area ---
        dc.PushClip(new RectangleGeometry(new Rect(rowHeadW, top, Math.Max(0, width - rowHeadW), Math.Max(0, height - top))));
        for (int r = _firstRow; r < lastRow; r++)
        {
            Brush? fill = r == _hoverRow ? HoverBrush : (r & 1) == 1 ? StripeBrush : null;
            if (fill is not null) dc.DrawRectangle(fill, null, new Rect(rowHeadW, RowY(r), width - rowHeadW, rowH));
        }
        DrawMatches(dc, view, _firstRow, lastRow, firstCol, lastCol, rowHeadW);
        DrawSelection(dc, top, rowHeadW, pinnedArea: false);
        DrawGridLines(dc, firstCol, lastCol, top, Math.Min(height, RowY(lastRow)), _firstRow, lastRow, rowHeadW);
        var covered = DrawMerges(dc, view, _firstRow, lastRow, firstCol, lastCol, rowHeadW, dip, pinnedArea: false);
        for (int r = _firstRow; r < lastRow; r++)
            DrawRowText(dc, view, r, view.SheetRow(r), RowY(r), rowH, firstCol, lastCol, rowHeadW, Regular, dip, covered);
        DrawActive(dc, view, rowHeadW, pinnedArea: false);
        dc.Pop();

        // --- Pinned rows: the header (and a report title above it) stay on top ---
        if (pinned > 0)
        {
            dc.PushClip(new RectangleGeometry(new Rect(rowHeadW, headH, Math.Max(0, width - rowHeadW), pinned * rowH)));
            dc.DrawRectangle(PinnedBrush, null, new Rect(rowHeadW, headH, width - rowHeadW, pinned * rowH));
            DrawMatches(dc, view, -pinned, 0, firstCol, lastCol, rowHeadW);
            DrawSelection(dc, top, rowHeadW, pinnedArea: true);
            DrawGridLines(dc, firstCol, lastCol, headH, top, -pinned, 0, rowHeadW);
            var coveredPinned = DrawMerges(dc, view, -pinned, 0, firstCol, lastCol, rowHeadW, dip, pinnedArea: true);
            for (int r = -pinned; r < 0; r++)
                DrawRowText(dc, view, r, view.SheetRow(r), RowY(r), rowH, firstCol, lastCol, rowHeadW, r == -1 ? Bold : Regular, dip, coveredPinned);
            DrawActive(dc, view, rowHeadW, pinnedArea: true);
            dc.Pop();
            dc.DrawRectangle(StrongLineBrush, null, new Rect(0, top - 1, width, 1));
        }

        // --- Column letters ---
        var (selTop, selLeft, selBottom, selRight) = Selection;
        dc.DrawRectangle(HeaderBrush, null, new Rect(0, 0, width, headH));
        dc.PushClip(new RectangleGeometry(new Rect(rowHeadW, 0, Math.Max(0, width - rowHeadW), headH)));
        for (int c = firstCol; c <= lastCol; c++)
        {
            double x = ColumnX(c, rowHeadW);
            double w = _lefts[c + 1] - _lefts[c];
            if (c >= selLeft && c <= selRight) dc.DrawRectangle(HeaderActiveBrush, null, new Rect(x, 0, w, headH));
            string label = Sheet.ColumnName(c);
            if (view.SortColumn == c) label += view.SortDescending ? "  ▼" : "  ▲";
            var text = Text(label, view.SortColumn == c ? AccentBrush : SubtleBrush, Regular, FontSize * 0.92, w - 4, TextAlignment.Center, dip);
            dc.DrawText(text, new Point(x + 2, (headH - text.Height) / 2));
            dc.DrawRectangle(LineBrush, null, new Rect(x + w - 1, 0, 1, headH));
        }
        dc.Pop();

        // --- Row numbers (the sheet's own numbers, also when sorted) ---
        dc.DrawRectangle(HeaderBrush, null, new Rect(0, headH, rowHeadW, height - headH));
        dc.PushClip(new RectangleGeometry(new Rect(0, top, rowHeadW, Math.Max(0, height - top))));
        for (int r = _firstRow; r < lastRow; r++) DrawRowNumber(dc, view, r, selTop, selBottom, rowHeadW, dip);
        dc.Pop();
        for (int r = -pinned; r < 0; r++) DrawRowNumber(dc, view, r, selTop, selBottom, rowHeadW, dip);
        dc.DrawRectangle(StrongLineBrush, null, new Rect(rowHeadW - 1, 0, 1, height));
        dc.DrawRectangle(StrongLineBrush, null, new Rect(0, headH - 1, width, 1));
    }

    /// <summary>Top of a view row on screen: pinned rows under the letters, the others scrolled.</summary>
    private double RowY(int viewRow)
    {
        int pinned = _view?.PinnedRows ?? 0;
        return viewRow < 0
            ? HeaderHeight + (viewRow + pinned) * RowHeight
            : TopOfRows + (viewRow - _firstRow) * RowHeight;
    }

    private void DrawRowNumber(DrawingContext dc, TableView view, int r, int selTop, int selBottom, double rowHeadW, double dip)
    {
        double y = RowY(r), rowH = RowHeight;
        if (r >= selTop && r <= selBottom) dc.DrawRectangle(HeaderActiveBrush, null, new Rect(0, y, rowHeadW, rowH));
        int number = view.SheetRow(r) + 1;
        var text = Text(number.ToString(CultureInfo.InvariantCulture), r == _hoverRow ? TextBrush : SubtleBrush, r == -1 ? Bold : Regular,
            FontSize * 0.88, rowHeadW - Pad, TextAlignment.Right, dip);
        dc.DrawText(text, new Point(Pad / 2, y + (rowH - text.Height) / 2));
    }

    private (int First, int Last) VisibleColumns()
    {
        int columns = _widths.Length;
        if (columns == 0) return (0, -1);
        int first = Math.Max(0, Array.BinarySearch(_lefts, _offsetX) is var i && i >= 0 ? i : ~i - 1);
        first = Math.Min(first, columns - 1);
        double right = _offsetX + DataWidth;
        int last = first;
        while (last < columns - 1 && _lefts[last + 1] < right) last++;
        return (first, last);
    }

    private double ColumnX(int column, double rowHeadW) => rowHeadW + _lefts[column] - _offsetX;

    private void DrawGridLines(DrawingContext dc, int firstCol, int lastCol, double y1, double y2, int fromRow, int toRow, double rowHeadW)
    {
        for (int c = firstCol; c <= lastCol; c++)
        {
            double x = ColumnX(c + 1, rowHeadW);
            dc.DrawRectangle(LineBrush, null, new Rect(x - 1, y1, 1, Math.Max(0, y2 - y1)));
        }
        double right = Math.Min(ActualWidth, ColumnX(_widths.Length, rowHeadW));
        for (int r = fromRow; r < toRow; r++)
            dc.DrawRectangle(LineBrush, null, new Rect(rowHeadW, RowY(r) + RowHeight - 1, Math.Max(0, right - rowHeadW), 1));
    }

    private void DrawMatches(DrawingContext dc, TableView view, int fromRow, int toRow, int firstCol, int lastCol, double rowHeadW)
    {
        if (!view.HasMatches) return;
        for (int r = fromRow; r < toRow; r++)
        {
            if (view.MatchColumns(view.SheetRow(r)) is not { } columns) continue;
            double y = RowY(r);
            foreach (int c in columns)
            {
                if (c < firstCol || c > lastCol) continue;
                dc.DrawRectangle(MatchBrush, null, new Rect(ColumnX(c, rowHeadW), y, _lefts[c + 1] - _lefts[c] - 1, RowHeight - 1));
            }
        }
    }

    /// <summary>The selection's fill, in the pinned rows or in the scrolled ones (each area clips its own part).</summary>
    private void DrawSelection(DrawingContext dc, double top, double rowHeadW, bool pinnedArea)
    {
        var (selTop, selLeft, selBottom, selRight) = Selection;
        if (selTop == selBottom && selLeft == selRight) return; // one cell: only the frame
        if ((uint)selLeft >= (uint)_widths.Length) return;
        int right = Math.Min(selRight, _widths.Length - 1);
        double x1 = ColumnX(selLeft, rowHeadW), x2 = ColumnX(right + 1, rowHeadW);
        double y1, y2;
        if (pinnedArea)
        {
            if (selTop >= 0) return;
            y1 = RowY(selTop);
            y2 = selBottom >= 0 ? top : RowY(selBottom) + RowHeight;
        }
        else
        {
            if (selBottom < 0) return;
            y1 = selTop < 0 ? top : RowY(selTop);
            y2 = RowY(selBottom) + RowHeight;
        }
        dc.DrawRectangle(SelectionBrush, SelectionPen, new Rect(x1, y1, Math.Max(0, x2 - x1 - 1), Math.Max(0, y2 - y1 - 1)));
    }

    /// <summary>The frame of the active cell; the whole merged area when the cell is in one.</summary>
    private void DrawActive(DrawingContext dc, TableView view, double rowHeadW, bool pinnedArea)
    {
        var (row, column) = _active;
        if ((uint)column >= (uint)_widths.Length || (row < 0) != pinnedArea) return;
        double x1 = ColumnX(column, rowHeadW), x2 = ColumnX(column + 1, rowHeadW);
        double y1 = RowY(row), y2 = y1 + RowHeight;
        if (MergeAt(view, row, column) is { } m)
        {
            x1 = ColumnX(m.FromColumn, rowHeadW);
            x2 = ColumnX(Math.Min(m.ToColumn, _widths.Length - 1) + 1, rowHeadW);
            y1 = RowY(view.ViewRow(m.FromRow));
            y2 = RowY(view.ViewRow(m.ToRow)) + RowHeight;
        }
        dc.DrawRectangle(null, ActivePen, new Rect(x1, y1, Math.Max(0, x2 - x1 - 1), Math.Max(0, y2 - y1 - 1)));
    }

    /// <summary>The merged area a cell belongs to (pinned rows always; others while rows keep their file order).</summary>
    private static CellRange? MergeAt(TableView view, int viewRow, int column)
    {
        if (view.Sheet.Merges.Count == 0 || (!view.KeepsFileOrder && viewRow >= 0)) return null;
        int sheetRow = view.SheetRow(viewRow);
        foreach (var m in view.Sheet.Merges)
        {
            if (m.Contains(sheetRow, column) && (m.ToRow < view.PinnedRows) == (m.FromRow < view.PinnedRows)) return m;
        }
        return null;
    }

    /// <summary>
    /// Merged cells, while rows keep their file order, in one area (pinned or scrolled; an area crossing the edge is
    /// not merged). Returns the cells they cover: their own text is not drawn.
    /// </summary>
    private HashSet<long>? DrawMerges(DrawingContext dc, TableView view, int fromRow, int toRow, int firstCol, int lastCol,
        double rowHeadW, double dip, bool pinnedArea)
    {
        var merges = view.Sheet.Merges;
        // Pinned rows never move, so their merges stay even while the other rows are sorted.
        if (merges.Count == 0 || (!view.KeepsFileOrder && !pinnedArea) || toRow <= fromRow) return null;
        int pinned = view.PinnedRows;
        int sheetFirst = view.SheetRow(fromRow), sheetLast = view.SheetRow(toRow - 1);
        HashSet<long>? covered = null;
        foreach (var m in merges)
        {
            if (m.ToRow < sheetFirst || m.FromRow > sheetLast || m.ToColumn < firstCol || m.FromColumn > lastCol) continue;
            if (pinnedArea ? m.ToRow >= pinned : m.FromRow < pinned) continue;
            int right = Math.Min(m.ToColumn, _widths.Length - 1);
            covered ??= [];
            for (int r = Math.Max(m.FromRow, sheetFirst); r <= Math.Min(m.ToRow, sheetLast); r++)
                for (int c = Math.Max(m.FromColumn, firstCol); c <= Math.Min(right, lastCol); c++) covered.Add(CellKey(r, c));

            double x1 = ColumnX(m.FromColumn, rowHeadW), x2 = ColumnX(right + 1, rowHeadW);
            double y1 = RowY(view.ViewRow(m.FromRow)), y2 = RowY(view.ViewRow(m.ToRow)) + RowHeight;
            var rect = new Rect(x1, y1, Math.Max(0, x2 - x1 - 1), Math.Max(0, y2 - y1 - 1));
            dc.DrawRectangle(pinnedArea ? PinnedBrush : Background, null, rect);
            if (view.Sheet.Text(m.FromRow, m.FromColumn) is { } value)
            {
                var text = Text(FirstLine(value, 500), TextBrush, pinnedArea ? Bold : Regular, FontSize, rect.Width - 2 * Pad, TextAlignment.Center, dip);
                dc.PushClip(new RectangleGeometry(rect));
                dc.DrawText(text, new Point(rect.X + Pad, rect.Y + (rect.Height - text.Height) / 2));
                dc.Pop();
            }
        }
        return covered;
    }

    private void DrawRowText(DrawingContext dc, TableView view, int viewRow, int sheetRow, double y, double rowH, int firstCol, int lastCol,
        double rowHeadW, Typeface face, double dip, HashSet<long>? covered)
    {
        var sheet = view.Sheet;
        double pad = Pad;
        for (int c = firstCol; c <= lastCol; c++)
        {
            if (covered is not null && covered.Contains(CellKey(sheetRow, c))) continue;
            if (sheet.Text(sheetRow, c) is not { } value) continue;
            bool number = viewRow >= 0 && sheet.TryNumber(sheetRow, c, out _);
            double x = ColumnX(c, rowHeadW);
            double w = _lefts[c + 1] - _lefts[c];
            if (!number)
            {
                // Text spills into empty cells on the right, like in Excel.
                int next = c + 1;
                while (next < _widths.Length && next <= c + SpillColumns && sheet.Text(sheetRow, next) is null
                       && (covered is null || !covered.Contains(CellKey(sheetRow, next))))
                {
                    w += _lefts[next + 1] - _lefts[next];
                    next++;
                }
            }
            if (w - 2 * pad < 4) continue;
            var text = Text(FirstLine(value, 1000), number ? NumberBrush : TextBrush, face, FontSize, w - 2 * pad,
                number ? TextAlignment.Right : TextAlignment.Left, dip);
            dc.DrawText(text, new Point(x + pad, y + (rowH - text.Height) / 2));
        }
    }

    private static FormattedText Text(string value, Brush brush, Typeface face, double size, double maxWidth, TextAlignment alignment, double dip)
    {
        var text = new FormattedText(value, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, face, size, brush, dip)
        {
            MaxTextWidth = Math.Max(1, maxWidth),
            MaxLineCount = 1,
            Trimming = TextTrimming.CharacterEllipsis,
            TextAlignment = alignment,
        };
        return text;
    }

    /// <summary>A cell shows one line: the rest of a long or multi-line text is in the bar above the table.</summary>
    private static string FirstLine(string value, int max)
    {
        int end = value.AsSpan().IndexOfAny('\r', '\n');
        if (end < 0 && value.Length <= max) return value;
        string line = end < 0 ? value[..max] : value[..Math.Min(end, max)];
        return line + " …";
    }

    private static long CellKey(int row, int column) => (long)row << 15 | (uint)column;

    // ---- Mouse ----

    private enum Zone { None, ColumnHeader, ColumnBorder, RowHeader, Corner, Cell }

    private Zone HitTest(Point p, out int row, out int column)
    {
        row = column = -1;
        if (_view is null) return Zone.None;
        double rowHeadW = RowHeaderWidth;
        if (p.X >= rowHeadW && _widths.Length > 0)
        {
            double x = p.X - rowHeadW + _offsetX;
            int i = Array.BinarySearch(_lefts, x);
            column = i >= 0 ? Math.Min(i, _widths.Length - 1) : Math.Min(~i - 1, _widths.Length - 1);
            if (x > _lefts[^1]) column = -1;
        }
        if (p.Y < HeaderHeight)
        {
            if (p.X < rowHeadW) return Zone.Corner;
            // Near the right edge of a column: resize.
            double x = p.X - rowHeadW + _offsetX;
            for (int c = Math.Max(0, column - 1); c <= Math.Min(_widths.Length - 1, column + 1); c++)
            {
                if (c >= 0 && Math.Abs(_lefts[c + 1] - x) <= 4)
                {
                    column = c;
                    return Zone.ColumnBorder;
                }
            }
            return column >= 0 ? Zone.ColumnHeader : Zone.None;
        }
        if (_view.PinHeader && p.Y < TopOfRows) row = Math.Min(-1, (int)((p.Y - HeaderHeight) / RowHeight) - _view.PinnedRows);
        else row = _firstRow + (int)((p.Y - TopOfRows) / RowHeight);
        if (row >= _view.RowCount) row = _view.RowCount - 1;
        if (p.X < rowHeadW) return Zone.RowHeader;
        return column >= 0 ? Zone.Cell : Zone.None;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        if (_view is null) return;
        var zone = HitTest(e.GetPosition(this), out int row, out int column);
        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        switch (zone)
        {
            case Zone.ColumnBorder:
                if (e.ClickCount == 2) FitColumn(column);
                else
                {
                    _resizing = column;
                    _resizeStart = e.GetPosition(this).X;
                    _resizeWidth = _widths[column];
                    CaptureMouse();
                }
                break;
            case Zone.ColumnHeader:
                HeaderClicked?.Invoke(this, column);
                break;
            case Zone.Corner:
                SelectAll();
                break;
            case Zone.RowHeader when row >= -_view.PinnedRows && _view.ColumnCount > 0:
                if (!shift) _anchor = (row, 0);
                _active = (row, _view.ColumnCount - 1);
                if (!shift) _anchor = (row, 0);
                InvalidateVisual();
                ActiveCellChanged?.Invoke(this, EventArgs.Empty);
                break;
            case Zone.Cell when row >= -_view.PinnedRows:
                Select(row, column, shift);
                _selecting = true;
                CaptureMouse();
                break;
        }
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var p = e.GetPosition(this);
        if (_resizing >= 0)
        {
            _widths[_resizing] = Math.Max(MinColumn, _resizeWidth + (p.X - _resizeStart) / _zoom);
            UpdateLefts();
            ClampScroll();
            InvalidateVisual();
            Scrolled?.Invoke(this, EventArgs.Empty);
            return;
        }
        var zone = HitTest(p, out int row, out int column);
        Cursor = zone == Zone.ColumnBorder ? Cursors.SizeWE : zone == Zone.ColumnHeader ? Cursors.Hand : null;
        if (_selecting && e.LeftButton == MouseButtonState.Pressed && _view is not null)
        {
            // Dragging past an edge scrolls a step per move.
            if (p.Y > ActualHeight) row = Math.Min(_view.RowCount - 1, _firstRow + VisibleRows);
            else if (p.Y < TopOfRows && _firstRow > 0 && _anchor.Row >= 0) row = _firstRow - 1;
            if (column < 0) column = p.X < RowHeaderWidth ? Math.Max(0, _active.Column - 1) : _widths.Length - 1;
            if (row >= -_view.PinnedRows && (row, column) != _active) Select(row, column, extend: true);
        }
        int hover = zone is Zone.Cell or Zone.RowHeader ? row : int.MinValue;
        if (hover != _hoverRow)
        {
            _hoverRow = hover;
            InvalidateVisual();
        }
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hoverRow != int.MinValue)
        {
            _hoverRow = int.MinValue;
            InvalidateVisual();
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        _selecting = false;
        _resizing = -1;
        if (IsMouseCaptured) ReleaseMouseCapture();
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonDown(e);
        Focus();
        // Right click outside the selection selects that cell first, like in Excel.
        if (HitTest(e.GetPosition(this), out int row, out int column) == Zone.Cell)
        {
            var (top, left, bottom, right) = Selection;
            if (row < top || row > bottom || column < left || column > right) Select(row, column);
        }
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (_view is null) return;
        var modifiers = Keyboard.Modifiers;
        if ((modifiers & ModifierKeys.Control) != 0) Zoom += e.Delta > 0 ? 0.1 : -0.1;
        else if ((modifiers & ModifierKeys.Shift) != 0) ScrollToX(_offsetX - e.Delta / 120.0 * 80 * _zoom);
        else ScrollToRow(_firstRow - e.Delta / 120.0 * 3);
        e.Handled = true;
    }

    // ---- Keyboard ----

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_view is null) return;
        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        var (row, column) = _active;
        int page = Math.Max(1, VisibleRows - 1);
        int lastRow = Math.Max(0, _view.RowCount - 1), lastColumn = Math.Max(0, _view.ColumnCount - 1);
        int firstRow = -_view.PinnedRows;
        switch (e.Key)
        {
            case Key.Up: Select(ctrl ? firstRow : row - 1, column, shift); break;
            case Key.Down: Select(ctrl ? lastRow : row + 1, column, shift); break;
            case Key.Left: Select(row, ctrl ? 0 : column - 1, shift); break;
            case Key.Right: Select(row, ctrl ? lastColumn : column + 1, shift); break;
            case Key.Tab: Select(row, column + (shift ? -1 : 1)); break;
            case Key.Enter: Select(row + (shift ? -1 : 1), column); break;
            case Key.PageUp: ScrollToRow(_firstRow - page); Select(Math.Max(firstRow, row - page), column, shift); break;
            case Key.PageDown: ScrollToRow(_firstRow + page); Select(Math.Min(lastRow, row + page), column, shift); break;
            case Key.Home: Select(ctrl ? firstRow : row, 0, shift); break;
            case Key.End: Select(ctrl ? lastRow : row, lastColumn, shift); break;
            case Key.A when ctrl: SelectAll(); break;
            case Key.OemPlus or Key.Add when ctrl: Zoom += 0.1; break;
            case Key.OemMinus or Key.Subtract when ctrl: Zoom -= 0.1; break;
            case Key.D0 or Key.NumPad0 when ctrl: Zoom = 1; break;
            default: return;
        }
        e.Handled = true;
    }

    private static SolidColorBrush Frozen(byte r, byte g, byte b) => Frozen(0xFF, r, g, b);

    private static SolidColorBrush Frozen(byte a, byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }

    private static Pen FrozenPen(Brush brush, double thickness)
    {
        var pen = new Pen(brush, thickness);
        pen.Freeze();
        return pen;
    }
}
