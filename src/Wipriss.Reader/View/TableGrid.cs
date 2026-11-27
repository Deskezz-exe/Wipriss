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
    /// <summary>Spare rows and columns while editing: darker, they are not part of the table yet.</summary>
    private static readonly Brush SpareBrush = Frozen(0x0E, 0x0D, 0x0C);
    private static readonly Brush FaintBrush = Frozen(0x5C, 0x59, 0x53);
    /// <summary>Cells changed and not saved yet.</summary>
    private static readonly Brush EditedBrush = Frozen(0x3A, 0xE0, 0xA8, 0x4A);
    private static readonly Brush EditedMarkBrush = Frozen(0xE0, 0xA8, 0x4A);
    private static readonly Pen ActivePen = FrozenPen(AccentBrush, 2);
    private static readonly Pen SelectionPen = FrozenPen(Frozen(0x80, 0xA6, 0xBE, 0xAB), 1);

    private static readonly Typeface Regular = new(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private static readonly Typeface Bold = new(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    private const double BaseFont = 13;
    private const double BaseRow = 28;
    private const double MinColumn = 28;
    /// <summary>"Show whole" text may cover this many cells next to its own.</summary>
    private const int OverflowColumns = 2;
    /// <summary>Excel's usual row is 15 pt; here it is <see cref="BaseRow"/> pixels.</summary>
    private const double PixelsPerPoint = BaseRow / 15;

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

    // Row heights at the current zoom, by sheet row (rows with a height from the file or wrapped text), and the tops
    // of the rows from the first one on screen.
    private readonly Dictionary<int, double> _heights = [];
    private readonly List<double> _tops = [];
    private int _topsFirst = -1;
    private readonly Dictionary<uint, SolidColorBrush> _brushes = [];
    private static readonly Typeface[] Faces = MakeFaces();

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

    /// <summary>F2, a double click or typing on a cell: start editing it (with the typed text, or null to keep its text).</summary>
    public event EventHandler<string?>? EditRequested;

    /// <summary>Delete on the selection.</summary>
    public event EventHandler? ClearRequested;

    /// <summary>The owner may change cells: typing starts editing.</summary>
    public bool CanEdit { get; set; }

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

    private double PinnedHeight
    {
        get
        {
            double height = 0;
            for (int r = -(_view?.PinnedRows ?? 0); r < 0; r++) height += RowH(r);
            return height;
        }
    }

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

    public int VisibleRows
    {
        get
        {
            if (_view is null) return Math.Max(1, (int)((ActualHeight - TopOfRows) / RowHeight));
            double room = ActualHeight - TopOfRows, used = 0;
            int count = 0;
            for (int r = _firstRow; r < _view.RowCount && count < 2000; r++)
            {
                used += RowH(r);
                if (used > room) break;
                count++;
            }
            if (_firstRow + count >= _view.RowCount) count += (int)Math.Max(0, (room - used) / RowHeight);
            return Math.Max(1, count);
        }
    }

    public double DataWidth => Math.Max(0, ActualWidth - RowHeaderWidth);

    public double TotalWidth => _lefts[^1];

    /// <summary>Shows another sheet (or the same sheet in a new order). Keeps the widths when the columns stay.</summary>
    public void SetView(TableView view, bool keepLayout)
    {
        ForgetHeights();
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

    public void Refresh()
    {
        ForgetHeights();
        InvalidateVisual();
    }

    /// <summary>After the sheet grew or shrank (spare columns shown, a new column filled): new columns get a width.</summary>
    public void SyncColumns()
    {
        if (_view is null) return;
        int columns = _view.ColumnCount;
        if (columns != _widths.Length)
        {
            int old = _widths.Length;
            Array.Resize(ref _widths, columns);
            for (int c = old; c < columns; c++) _widths[c] = 96;
            UpdateLefts();
        }
        int lastRow = Math.Max(0, _view.RowCount - 1), lastColumn = Math.Max(0, columns - 1);
        _active = (Math.Min(_active.Row, lastRow), Math.Min(_active.Column, lastColumn));
        _anchor = (Math.Min(_anchor.Row, lastRow), Math.Min(_anchor.Column, lastColumn));
        ClampScroll();
        InvalidateVisual();
        Scrolled?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Where a cell is drawn, in this element's coordinates (null when it is scrolled away).</summary>
    public Rect? CellRect(int viewRow, int column)
    {
        if (_view is null || (uint)column >= (uint)_widths.Length) return null;
        if (viewRow >= 0 && (viewRow < _firstRow || viewRow >= _firstRow + VisibleRows + 1)) return null;
        double rowHeadW = RowHeaderWidth;
        double x = ColumnX(column, rowHeadW), w = _lefts[column + 1] - _lefts[column];
        double y = RowY(viewRow);
        double left = Math.Max(x, rowHeadW);
        if (x + w <= rowHeadW || left >= ActualWidth) return null;
        return new Rect(left, y, Math.Max(24, Math.Min(x + w, ActualWidth) - left), RowH(viewRow));
    }

    public double CellFontSize => FontSize;

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
        ForgetHeights();
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
        double headH = HeaderHeight, rowHeadW = RowHeaderWidth, top = TopOfRows, pinnedH = PinnedHeight;
        int pinned = view.PinnedRows;
        int lastRow = _firstRow;
        while (lastRow < view.RowCount && RowY(lastRow) < height) lastRow++;
        var (firstCol, lastCol) = VisibleColumns();

        // --- Data area ---
        dc.PushClip(new RectangleGeometry(new Rect(rowHeadW, top, Math.Max(0, width - rowHeadW), Math.Max(0, height - top))));
        for (int r = _firstRow; r < lastRow; r++)
        {
            Brush? fill = r == _hoverRow ? HoverBrush : (r & 1) == 1 ? StripeBrush : null;
            if (fill is not null) dc.DrawRectangle(fill, null, new Rect(rowHeadW, RowY(r), width - rowHeadW, RowH(r)));
        }
        DrawSpare(dc, view, _firstRow, lastRow, rowHeadW, width);
        DrawFills(dc, view, _firstRow, lastRow, firstCol, lastCol, rowHeadW);
        DrawEdited(dc, view, _firstRow, lastRow, firstCol, lastCol, rowHeadW);
        DrawMatches(dc, view, _firstRow, lastRow, firstCol, lastCol, rowHeadW);
        DrawSelection(dc, top, rowHeadW, pinnedArea: false);
        DrawGridLines(dc, firstCol, lastCol, top, Math.Min(height, RowY(lastRow)), _firstRow, lastRow, rowHeadW);
        var covered = DrawMerges(dc, view, _firstRow, lastRow, firstCol, lastCol, rowHeadW, dip, pinnedArea: false);
        for (int r = _firstRow; r < lastRow; r++)
            DrawRowText(dc, view, r, view.SheetRow(r), RowY(r), RowH(r), firstCol, lastCol, rowHeadW, header: false, dip, covered);
        DrawActive(dc, view, rowHeadW, pinnedArea: false);
        dc.Pop();

        // --- Pinned rows: the header (and a report title above it) stay on top ---
        if (pinned > 0)
        {
            dc.PushClip(new RectangleGeometry(new Rect(rowHeadW, headH, Math.Max(0, width - rowHeadW), pinnedH)));
            dc.DrawRectangle(PinnedBrush, null, new Rect(rowHeadW, headH, width - rowHeadW, pinnedH));
            DrawSpare(dc, view, -pinned, 0, rowHeadW, width);
            DrawFills(dc, view, -pinned, 0, firstCol, lastCol, rowHeadW);
            DrawEdited(dc, view, -pinned, 0, firstCol, lastCol, rowHeadW);
            DrawMatches(dc, view, -pinned, 0, firstCol, lastCol, rowHeadW);
            DrawSelection(dc, top, rowHeadW, pinnedArea: true);
            DrawGridLines(dc, firstCol, lastCol, headH, top, -pinned, 0, rowHeadW);
            var coveredPinned = DrawMerges(dc, view, -pinned, 0, firstCol, lastCol, rowHeadW, dip, pinnedArea: true);
            for (int r = -pinned; r < 0; r++)
                DrawRowText(dc, view, r, view.SheetRow(r), RowY(r), RowH(r), firstCol, lastCol, rowHeadW, header: r == -1, dip, coveredPinned);
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
            var text = Text(label, view.SortColumn == c ? AccentBrush : c >= view.Sheet.DataColumnCount ? FaintBrush : SubtleBrush, Regular, FontSize * 0.92, w - 4, TextAlignment.Center, dip);
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
        if (viewRow < 0)
        {
            double y = HeaderHeight;
            for (int r = -pinned; r < viewRow; r++) y += RowH(r);
            return y;
        }
        if (_topsFirst != _firstRow)
        {
            _tops.Clear();
            _topsFirst = _firstRow;
        }
        if (_tops.Count == 0) _tops.Add(TopOfRows);
        if (viewRow < _firstRow)
        {
            // Above the screen (a merged area starting there): only its distance matters, and it is short.
            double y = _tops[0];
            for (int r = _firstRow - 1; r >= viewRow && _firstRow - r <= 500; r--) y -= RowH(r);
            return y - Math.Max(0, _firstRow - viewRow - 500) * RowHeight;
        }
        int index = viewRow - _firstRow;
        // Far below the screen only the usual height is counted.
        while (_tops.Count <= index && _tops.Count <= 2000) _tops.Add(_tops[^1] + RowH(_firstRow + _tops.Count - 1));
        return index < _tops.Count ? _tops[index] : _tops[^1] + (index - _tops.Count + 1) * RowHeight;
    }

    /// <summary>Height of a view row on screen: the file's height when it is bigger, or as tall as its wrapped text.</summary>
    private double RowH(int viewRow)
    {
        if (_view is not { } view) return RowHeight;
        int sheetRow = view.SheetRow(viewRow);
        if (_heights.TryGetValue(sheetRow, out double known)) return known;
        var sheet = view.Sheet;
        double height = RowHeight;
        var hint = sheet.RowHeight(sheetRow);
        if (hint is { } h) height = Math.Max(height, Math.Round(h.Points * PixelsPerPoint * _zoom));
        if (hint is not { Custom: true } && sheet.RowMayWrap(sheetRow)) height = Math.Max(height, WrappedHeight(sheet, sheetRow));
        height = Math.Min(height, RowHeight * 20);
        if (height != RowHeight || _heights.Count < 50_000) _heights[sheetRow] = height;
        return height;
    }

    /// <summary>How tall a row must be for the wrapped text of its cells (merged cells don't count, like in Excel).</summary>
    private double WrappedHeight(Sheet sheet, int sheetRow)
    {
        double height = 0, pad = Pad;
        double dip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        int columns = Math.Min(sheet.ColumnCount, _widths.Length);
        for (int c = 0; c < columns; c++)
        {
            var format = sheet.Format(sheetRow, c);
            if (format.Mode != TextMode.Wrap || sheet.Text(sheetRow, c) is not { } value) continue;
            if (sheet.Merges.Count > 0 && sheet.Merges.Any(m => m.Contains(sheetRow, c))) continue;
            double w = _lefts[c + 1] - _lefts[c] - 2 * pad;
            if (w < 4) continue;
            var text = new FormattedText(Limit(value), CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, Face(format, false),
                SizeOf(format, sheet), TextBrush, dip) { MaxTextWidth = w };
            height = Math.Max(height, Math.Ceiling(text.Height + 2 * VPad));
        }
        return height;
    }

    private void ForgetHeights()
    {
        _heights.Clear();
        _tops.Clear();
        _topsFirst = -1;
    }

    private double VPad => Math.Round(4 * _zoom);

    private void DrawRowNumber(DrawingContext dc, TableView view, int r, int selTop, int selBottom, double rowHeadW, double dip)
    {
        double y = RowY(r), rowH = RowH(r);
        if (r >= selTop && r <= selBottom) dc.DrawRectangle(HeaderActiveBrush, null, new Rect(0, y, rowHeadW, rowH));
        int number = view.SheetRow(r) + 1;
        var brush = r == _hoverRow ? TextBrush : number > view.Sheet.DataRowCount ? FaintBrush : SubtleBrush;
        var text = Text(number.ToString(CultureInfo.InvariantCulture), brush, r == -1 ? Bold : Regular,
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
            dc.DrawRectangle(LineBrush, null, new Rect(rowHeadW, RowY(r) + RowH(r) - 1, Math.Max(0, right - rowHeadW), 1));
    }

    /// <summary>Spare rows and columns (shown while editing) are darker than the table.</summary>
    private void DrawSpare(DrawingContext dc, TableView view, int fromRow, int toRow, double rowHeadW, double width)
    {
        var sheet = view.Sheet;
        if (sheet.RowCount == sheet.DataRowCount && sheet.ColumnCount == sheet.DataColumnCount) return;
        double spareX = sheet.DataColumnCount < _widths.Length ? Math.Max(rowHeadW, ColumnX(sheet.DataColumnCount, rowHeadW)) : width;
        for (int r = fromRow; r < toRow; r++)
        {
            double y = RowY(r), rowH = RowH(r);
            if (view.SheetRow(r) >= sheet.DataRowCount) dc.DrawRectangle(SpareBrush, null, new Rect(rowHeadW, y, Math.Max(0, width - rowHeadW), rowH));
            else if (spareX < width) dc.DrawRectangle(SpareBrush, null, new Rect(spareX, y, width - spareX, rowH));
        }
    }

    /// <summary>Changed cells are tinted, with a mark in the corner, until they are saved.</summary>
    private void DrawEdited(DrawingContext dc, TableView view, int fromRow, int toRow, int firstCol, int lastCol, double rowHeadW)
    {
        if (view.Sheet is not EditedSheet { ChangeCount: > 0 } sheet) return;
        double mark = Math.Max(4, Math.Round(5 * _zoom));
        for (int r = fromRow; r < toRow; r++)
        {
            int sheetRow = view.SheetRow(r);
            double y = RowY(r);
            for (int c = firstCol; c <= lastCol; c++)
            {
                bool text = sheet.IsEdited(sheetRow, c);
                if (!text && !sheet.IsFormatEdited(sheetRow, c)) continue;
                double x = ColumnX(c, rowHeadW), w = _lefts[c + 1] - _lefts[c];
                // A new look shows itself: only the mark in the corner; changed text is tinted too.
                if (text) dc.DrawRectangle(EditedBrush, null, new Rect(x, y, Math.Max(0, w - 1), RowH(r) - 1));
                dc.DrawRectangle(EditedMarkBrush, null, new Rect(x, y, mark, 2));
                dc.DrawRectangle(EditedMarkBrush, null, new Rect(x, y, 2, mark));
            }
        }
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
                dc.DrawRectangle(MatchBrush, null, new Rect(ColumnX(c, rowHeadW), y, _lefts[c + 1] - _lefts[c] - 1, RowH(r) - 1));
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
            y2 = selBottom >= 0 ? top : RowY(selBottom) + RowH(selBottom);
        }
        else
        {
            if (selBottom < 0) return;
            y1 = selTop < 0 ? top : RowY(selTop);
            y2 = RowY(selBottom) + RowH(selBottom);
        }
        dc.DrawRectangle(SelectionBrush, SelectionPen, new Rect(x1, y1, Math.Max(0, x2 - x1 - 1), Math.Max(0, y2 - y1 - 1)));
    }

    /// <summary>The frame of the active cell; the whole merged area when the cell is in one.</summary>
    private void DrawActive(DrawingContext dc, TableView view, double rowHeadW, bool pinnedArea)
    {
        var (row, column) = _active;
        if ((uint)column >= (uint)_widths.Length || (row < 0) != pinnedArea) return;
        double x1 = ColumnX(column, rowHeadW), x2 = ColumnX(column + 1, rowHeadW);
        double y1 = RowY(row), y2 = y1 + RowH(row);
        if (MergeAt(view, row, column) is { } m)
        {
            x1 = ColumnX(m.FromColumn, rowHeadW);
            x2 = ColumnX(Math.Min(m.ToColumn, _widths.Length - 1) + 1, rowHeadW);
            y1 = RowY(view.ViewRow(m.FromRow));
            y2 = RowY(view.ViewRow(m.ToRow)) + RowH(view.ViewRow(m.ToRow));
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
            int lastView = view.ViewRow(m.ToRow);
            double y1 = RowY(view.ViewRow(m.FromRow)), y2 = RowY(lastView) + RowH(lastView);
            var rect = new Rect(x1, y1, Math.Max(0, x2 - x1 - 1), Math.Max(0, y2 - y1 - 1));
            var format = view.Sheet.Format(m.FromRow, m.FromColumn);
            dc.DrawRectangle(format.Fill is { } fill ? BrushOf(fill) : pinnedArea ? PinnedBrush : Background, null, rect);
            if (view.Sheet.Text(m.FromRow, m.FromColumn) is { } value)
            {
                // A merged area the file says nothing about (or a table without formatting) is centered, as a title.
                if (format.IsPlain) format = format with { Horizontal = HAlign.Center, Vertical = VAlign.Center };
                dc.PushClip(new RectangleGeometry(rect));
                DrawCellText(dc, view.Sheet, value, format, number: false, header: pinnedArea, rect.X, rect.Width, rect.Y, rect.Height + 1, dip);
                dc.Pop();
            }
        }
        return covered;
    }

    private void DrawRowText(DrawingContext dc, TableView view, int viewRow, int sheetRow, double y, double rowH, int firstCol, int lastCol,
        double rowHeadW, bool header, double dip, HashSet<long>? covered)
    {
        var sheet = view.Sheet;
        int columns = _widths.Length;
        // Text shown whole may reach in from cells just off the screen.
        for (int c = Math.Max(0, firstCol - OverflowColumns); c <= Math.Min(columns - 1, lastCol + OverflowColumns); c++)
        {
            if (covered is not null && covered.Contains(CellKey(sheetRow, c))) continue;
            if (sheet.Text(sheetRow, c) is not { } value) continue;
            var format = sheet.Format(sheetRow, c);
            bool number = viewRow >= 0 && sheet.TryNumber(sheetRow, c, out _);
            if ((c < firstCol || c > lastCol) && (format.Mode != TextMode.Overflow || number)) continue;
            int from = c, to = c;
            if (format.Mode == TextMode.Overflow && !number)
            {
                var align = Align(format, number);
                bool left = align != TextAlignment.Left, right = align != TextAlignment.Right;
                int spare = OverflowColumns;
                // Grows over empty cells; centered text grows both ways.
                while (spare > 0 && (right || left))
                {
                    if (right && (right = Free(to + 1))) { to++; spare--; }
                    if (spare > 0 && left && (left = Free(from - 1))) { from--; spare--; }
                }
            }
            double x = ColumnX(from, rowHeadW), w = _lefts[to + 1] - _lefts[from];
            DrawCellText(dc, sheet, value, format, number, header, x, w, y, rowH, dip);
        }

        bool Free(int column) => column >= 0 && column < columns && sheet.Text(sheetRow, column) is null
                                 && (covered is null || !covered.Contains(CellKey(sheetRow, column)));
    }

    /// <summary>One cell's text in its box, in its font, color and alignment; cut with "…" or wrapped.</summary>
    private void DrawCellText(DrawingContext dc, Sheet sheet, string value, CellFormat format, bool number, bool header,
        double x, double w, double y, double h, double dip)
    {
        double pad = Pad;
        if (w - 2 * pad < 4) return;
        bool wrap = format.Mode == TextMode.Wrap;
        var text = new FormattedText(wrap ? Limit(value) : FirstLine(value, 1000), CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            Face(format, header), SizeOf(format, sheet), Foreground(format, number), dip)
        {
            MaxTextWidth = w - 2 * pad,
            Trimming = TextTrimming.CharacterEllipsis,
            TextAlignment = Align(format, number),
        };
        if (wrap) text.MaxTextHeight = Math.Max(1, h - 2 * VPad);
        else text.MaxLineCount = 1;
        if (format.Underline) text.SetTextDecorations(TextDecorations.Underline);
        double top = format.Vertical switch
        {
            VAlign.Top => y + VPad,
            VAlign.Center => y + (h - text.Height) / 2,
            // Excel puts text at the bottom; in a row of the usual height that is the middle.
            _ => h > RowHeight + 0.5 ? y + h - text.Height - VPad : y + (h - text.Height) / 2,
        };
        if (wrap) dc.PushClip(new RectangleGeometry(new Rect(x, y, w, h)));
        dc.DrawText(text, new Point(x + pad, top));
        if (wrap) dc.Pop();
    }

    private static TextAlignment Align(CellFormat format, bool number) => format.Horizontal switch
    {
        HAlign.Left => TextAlignment.Left,
        HAlign.Center => TextAlignment.Center,
        HAlign.Right => TextAlignment.Right,
        _ => number ? TextAlignment.Right : TextAlignment.Left,
    };

    /// <summary>Font size on screen: the workbook's usual size is <see cref="BaseFont"/>, others in proportion.</summary>
    private double SizeOf(CellFormat format, Sheet sheet) =>
        format.Size > 0 ? Math.Clamp(FontSize * format.Size / sheet.DefaultFontSize, 5 * _zoom, 120 * _zoom) : FontSize;

    private static Typeface Face(CellFormat format, bool header) =>
        Faces[(format.Bold ? 2 : header ? 1 : 0) + (format.Italic ? 3 : 0)];

    private static Typeface[] MakeFaces()
    {
        var family = new FontFamily("Segoe UI Variable Text, Segoe UI");
        Typeface Make(FontStyle style, FontWeight weight) => new(family, style, weight, FontStretches.Normal);
        return
        [
            Make(FontStyles.Normal, FontWeights.Normal), Make(FontStyles.Normal, FontWeights.SemiBold), Make(FontStyles.Normal, FontWeights.Bold),
            Make(FontStyles.Italic, FontWeights.Normal), Make(FontStyles.Italic, FontWeights.SemiBold), Make(FontStyles.Italic, FontWeights.Bold),
        ];
    }

    /// <summary>
    /// The text color. The table is dark: black or dark text the file asks for (written for a white sheet) would
    /// vanish, so a color too close to its background gives way to light or dark text that can be read.
    /// </summary>
    private Brush Foreground(CellFormat format, bool number)
    {
        uint background = format.Fill ?? 0xFF131210;
        if (format.Color is { } color && Contrast(color, background) >= 2) return BrushOf(color);
        if (format.Fill is null) return number ? NumberBrush : TextBrush;
        return Luminance(background) > 0.35 ? BrushOf(0xFF1A1A1A) : TextBrush;
    }

    private static double Luminance(uint argb)
    {
        static double Linear(uint channel)
        {
            double v = channel / 255.0;
            return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Linear(argb >> 16 & 0xFF) + 0.7152 * Linear(argb >> 8 & 0xFF) + 0.0722 * Linear(argb & 0xFF);
    }

    private static double Contrast(uint a, uint b)
    {
        double la = Luminance(a), lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    private SolidColorBrush BrushOf(uint argb)
    {
        if (_brushes.TryGetValue(argb, out var brush)) return brush;
        if (_brushes.Count > 256) _brushes.Clear();
        return _brushes[argb] = Frozen((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
    }

    /// <summary>Cell fills, under the selection and the grid.</summary>
    private void DrawFills(DrawingContext dc, TableView view, int fromRow, int toRow, int firstCol, int lastCol, double rowHeadW)
    {
        var sheet = view.Sheet;
        for (int r = fromRow; r < toRow; r++)
        {
            int sheetRow = view.SheetRow(r);
            double y = RowY(r), h = RowH(r);
            for (int c = firstCol; c <= lastCol; c++)
            {
                if (sheet.Format(sheetRow, c).Fill is not { } fill) continue;
                dc.DrawRectangle(BrushOf(fill), null, new Rect(ColumnX(c, rowHeadW), y, _lefts[c + 1] - _lefts[c], h));
            }
        }
    }

    private static FormattedText Text(string value, Brush brush, Typeface face, double size, double maxWidth, TextAlignment alignment, double dip) =>
        new(value, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, face, size, brush, dip)
        {
            MaxTextWidth = Math.Max(1, maxWidth),
            MaxLineCount = 1,
            Trimming = TextTrimming.CharacterEllipsis,
            TextAlignment = alignment,
        };

    /// <summary>Wrapped text is drawn whole, up to a sane length.</summary>
    private static string Limit(string value) => value.Length <= 3000 ? value : value[..3000] + " …";

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
        if (_view.PinHeader && p.Y < TopOfRows)
        {
            row = -1;
            for (int r = -_view.PinnedRows; r < 0; r++)
            {
                if (p.Y < RowY(r) + RowH(r))
                {
                    row = r;
                    break;
                }
            }
        }
        else
        {
            row = _firstRow;
            while (row < _view.RowCount - 1 && p.Y >= RowY(row) + RowH(row)) row++;
        }
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
            case Zone.Cell when row >= -_view.PinnedRows && e.ClickCount == 2 && CanEdit:
                Select(row, column);
                EditRequested?.Invoke(this, null);
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
            case Key.F2 when CanEdit: EditRequested?.Invoke(this, null); break;
            case Key.Delete when CanEdit: ClearRequested?.Invoke(this, EventArgs.Empty); break;
            case Key.Back when CanEdit: Select(row, column); EditRequested?.Invoke(this, ""); break;
            default: return;
        }
        e.Handled = true;
    }

    /// <summary>Typing on a cell starts editing it with the typed letter, like in Excel.</summary>
    protected override void OnTextInput(TextCompositionEventArgs e)
    {
        base.OnTextInput(e);
        if (!CanEdit || _view is null || string.IsNullOrEmpty(e.Text) || char.IsControl(e.Text[0])) return;
        if ((Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Alt)) == ModifierKeys.Control) return;
        var (row, column) = _active;
        Select(row, column);
        EditRequested?.Invoke(this, e.Text);
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
