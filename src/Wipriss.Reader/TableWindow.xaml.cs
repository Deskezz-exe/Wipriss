using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Wipriss.Core.Text;
using Wipriss.Core.Ui;
using Wipriss.Reader.Data;
using Wipriss.Reader.View;

namespace Wipriss.Reader;

/// <summary>
/// One table file: the sheet tabs, search, sorting, the pinned header, zoom and full screen. Reading, sorting and
/// searching run off the UI thread and can be cancelled; nothing runs while the owner only looks.
/// </summary>
public partial class TableWindow : Window
{
    private const int SearchLimit = 1_000_000;
    private const int StatsLimit = 200_000;

    private string _path;
    private Workbook? _book;
    private int _sheetIndex;
    private TableView? _view;
    private Dictionary<int, int[]>? _matches;
    private readonly Dictionary<Sheet, int> _headerChoice = [];
    private CancellationTokenSource? _load, _work, _search;
    private DispatcherTimer? _searchDelay;
    private bool _syncing;
    private bool _fullScreen;
    private WindowState _savedState;
    private WindowStyle _savedStyle;
    private ResizeMode _savedResize;

    public TableWindow(string path)
    {
        InitializeComponent();
        _path = path;
        Table.Scrolled += (_, _) => SyncScrollBars();
        Table.ActiveCellChanged += (_, _) => ShowActiveCell();
        Table.HeaderClicked += (_, column) => SortByHeader(column);
        Table.ZoomChanged += (_, _) => ZoomText.Content = $"{Table.Zoom * 100:0}%";
        PreviewKeyDown += Window_PreviewKeyDown;
        Loaded += (_, _) => _ = LoadAsync(null);
        Closed += (_, _) =>
        {
            _load?.Cancel();
            _work?.Cancel();
            _search?.Cancel();
            _searchDelay?.Stop();
            _book?.Dispose();
            _book = null;
        };
        ShowName();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        NativeWindowStyle.DarkTitleBar(this);
        NativeWindowStyle.FrameColors(this, Color.FromRgb(0x2A, 0x28, 0x25), Color.FromRgb(0x12, 0x11, 0x10), Color.FromRgb(0xEC, 0xE8, 0xE1));
    }

    private void ShowName()
    {
        string name = Path.GetFileName(_path);
        FileName.Text = name;
        Title = $"{name} · Wipriss";
    }

    // ---- Opening ----

    private async Task LoadAsync(CsvSettings? csv)
    {
        _load?.Cancel();
        _work?.Cancel();
        _search?.Cancel();
        var cancel = _load = new CancellationTokenSource();
        ShowName();
        ShowOverlay("Открываю таблицу…", Path.GetFileName(_path), buttons: false);
        var progress = new Progress<long>(rows =>
        {
            if (cancel == _load && rows > 0) OverlayText.Text = $"Прочитано строк: {rows.ToString("N0", CultureInfo.CurrentCulture)}";
        });

        Workbook book;
        try
        {
            string path = _path;
            book = await Task.Run(() => Workbook.Load(path, csv, progress, cancel.Token), cancel.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            if (cancel == _load) ShowError(ex);
            return;
        }
        if (cancel != _load || !IsLoaded)
        {
            book.Dispose();
            return;
        }

        var old = _book;
        bool sameFile = old is not null && string.Equals(old.Path, book.Path, StringComparison.OrdinalIgnoreCase);
        _book = book;
        old?.Dispose();
        _headerChoice.Clear();
        BuildTabs();
        int index = sameFile && _sheetIndex < book.Sheets.Count ? _sheetIndex : FirstVisibleSheet(book);
        HideOverlay();
        ShowSheet(index);
        UpdateCsvButtons();
        Table.Focus();
    }

    private static int FirstVisibleSheet(Workbook book)
    {
        for (int i = 0; i < book.Sheets.Count; i++)
        {
            if (!book.Sheets[i].Hidden) return i;
        }
        return 0;
    }

    private void ShowError(Exception ex)
    {
        _book?.Dispose();
        _book = null;
        _view = null;
        Tabs.Children.Clear();
        TabsTrack.Visibility = Visibility.Collapsed;
        EncodingButton.Visibility = DelimiterButton.Visibility = Visibility.Collapsed;
        FileInfo.Text = "";
        var (title, text) = Describe(ex);
        ShowOverlay(title, text, buttons: true);
    }

    private static (string Title, string Text) Describe(Exception ex)
    {
        string name = ex.GetType().Name;
        switch (ex)
        {
            case FileNotFoundException or DirectoryNotFoundException:
                return ("Файл не найден", "Возможно, его переместили, переименовали или удалили.");
            case UnauthorizedAccessException:
                return ("Нет доступа к файлу", "Windows не даёт его прочитать. Проверьте, что файл не на отключённом диске.");
            case OutOfMemoryException:
                return ("Таблица слишком большая", "Не хватило памяти, чтобы её открыть.");
        }
        if (name.Contains("Password", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("encrypt", StringComparison.OrdinalIgnoreCase))
            return ("Таблица защищена паролем", "Такие файлы Wipriss пока не открывает.");
        if (ex is IOException io && (io.HResult & 0xFFFF) is 32 or 33)
            return ("Файл занят", "Другая программа держит его закрытым для чтения. Закройте её и попробуйте снова.");
        if (ex is IOException or InvalidDataException or System.Xml.XmlException or NotSupportedException or FormatException
            || name.Contains("Header", StringComparison.Ordinal) || ex.GetType().Namespace?.StartsWith("ExcelDataReader", StringComparison.Ordinal) == true)
            return ("Не получилось прочитать таблицу", "Файл повреждён, ещё не докачан или это не таблица.");
        Core.ErrorLog.Write(ex);
        return ("Не получилось открыть таблицу", "Что-то пошло не так при чтении файла.");
    }

    private void ShowOverlay(string title, string text, bool buttons)
    {
        OverlayTitle.Text = title;
        OverlayText.Text = text;
        OverlayButtons.Visibility = buttons ? Visibility.Visible : Visibility.Collapsed;
        Overlay.Visibility = Visibility.Visible;
    }

    private void HideOverlay() => Overlay.Visibility = Visibility.Collapsed;

    // ---- Sheets ----

    private void BuildTabs()
    {
        Tabs.Children.Clear();
        if (_book is not { } book) return;
        for (int i = 0; i < book.Sheets.Count; i++)
        {
            var sheet = book.Sheets[i];
            var tab = new RadioButton
            {
                Content = sheet.Hidden ? $"{sheet.Name} (скрытый)" : sheet.Name,
                Style = (Style)FindResource("SheetTab"),
                GroupName = "Sheets" + GetHashCode().ToString(CultureInfo.InvariantCulture),
                Tag = i,
            };
            tab.Checked += (s, _) =>
            {
                if (!_syncing && s is RadioButton { Tag: int index }) ShowSheet(index);
            };
            Tabs.Children.Add(tab);
        }
        TabsTrack.Visibility = book.Sheets.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowSheet(int index)
    {
        if (_book is not { } book || (uint)index >= (uint)book.Sheets.Count) return;
        _work?.Cancel();
        _search?.Cancel();
        _sheetIndex = index;
        var sheet = book.Sheets[index];
        int pinned = _headerChoice.TryGetValue(sheet, out int chosen) ? chosen : TableView.GuessPinnedRows(sheet);
        _view = new TableView(sheet, pinned);
        _matches = null;

        _syncing = true;
        HeaderToggle.IsChecked = _view.PinHeader;
        HeaderToggle.IsEnabled = sheet.RowCount > 1;
        OnlyMatches.IsChecked = false;
        if (index < Tabs.Children.Count && Tabs.Children[index] is RadioButton tab) tab.IsChecked = true;
        _syncing = false;

        Table.SetView(_view, keepLayout: false);
        UpdateInfo();
        UpdateSearchUi();
        if (SearchBox.Text.Trim().Length > 0) StartSearch();
    }

    private void UpdateInfo()
    {
        if (_view is not { } view || _book is not { } book) return;
        var sheet = view.Sheet;
        var parts = new List<string>
        {
            RuText.Count(sheet.RowCount, "строка", "строки", "строк").Replace(sheet.RowCount.ToString(CultureInfo.InvariantCulture),
                sheet.RowCount.ToString("N0", CultureInfo.CurrentCulture)),
            RuText.Count(sheet.ColumnCount, "столбец", "столбца", "столбцов"),
        };
        parts.Add(book.Format switch
        {
            TableFormat.Excel => Path.GetExtension(book.Path).TrimStart('.').ToUpperInvariant(),
            TableFormat.OpenDocument => "ODS",
            _ => "CSV",
        });
        if (view.OnlyMatches) parts.Add($"показано {view.RowCount.ToString("N0", CultureInfo.CurrentCulture)}");
        if (view.SortColumn >= 0) parts.Add($"сортировка по {Sheet.ColumnName(view.SortColumn)}");
        if (sheet is CsvSheet { QuotesIgnored: true }) parts.Add("кавычки не закрыты, прочитано как простой текст");
        if (sheet.Hidden) parts.Add("скрытый лист");
        FileInfo.Text = string.Join("  ·  ", parts);
    }

    private void Tabs_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        TabsScroll.ScrollToHorizontalOffset(TabsScroll.HorizontalOffset - e.Delta / 2.0);
        e.Handled = true;
    }

    // ---- Header row ----

    private void HeaderToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncing || _view is not { } old) return;
        // On: the guessed rows down to the header (at least the first row); off: none.
        int pinned = HeaderToggle.IsChecked == true ? Math.Max(1, TableView.GuessPinnedRows(old.Sheet)) : 0;
        _headerChoice[old.Sheet] = pinned;
        _work?.Cancel();
        var view = new TableView(old.Sheet, pinned);
        view.SetMatches(_matches, old.MatchCount);
        _view = view;
        _syncing = true;
        OnlyMatches.IsChecked = false;
        _syncing = false;
        Table.SetView(view, keepLayout: false);
        UpdateInfo();
    }

    // ---- Sorting and filtering ----

    private void SortByHeader(int column)
    {
        if (_view is not { } view) return;
        // Click: A→Z, again: Z→A, a third time: back to the file's order.
        if (view.SortColumn != column) _ = ApplyOrderAsync(column, false, view.OnlyMatches);
        else if (!view.SortDescending) _ = ApplyOrderAsync(column, true, view.OnlyMatches);
        else _ = ApplyOrderAsync(-1, false, view.OnlyMatches);
    }

    private async Task ApplyOrderAsync(int column, bool descending, bool onlyMatches)
    {
        if (_view is not { } view) return;
        _work?.Cancel();
        var cancel = _work = new CancellationTokenSource();
        var matches = _matches;
        int keepRow = view.SheetRow(Math.Max(-view.PinnedRows, Math.Min(Table.Active.Row, view.RowCount - 1)));
        bool slow = column >= 0 && view.Sheet.RowCount > 100_000;
        if (slow) ShowOverlay("Сортирую…", $"Столбец {Sheet.ColumnName(column)}", buttons: false);

        int[]? order;
        try
        {
            order = await Task.Run(() => view.ComputeOrder(column, descending, onlyMatches, matches, cancel.Token), cancel.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (cancel == _work) ShowError(ex);
            return;
        }
        finally
        {
            if (slow && cancel == _work && _book is not null) HideOverlay();
        }
        if (cancel != _work || view != _view) return;

        bool newSort = column != view.SortColumn || descending != view.SortDescending;
        view.Apply(column, descending, onlyMatches, order);
        Table.SetView(view, keepLayout: true);
        // A new sort starts from the top; the search filter keeps the row the owner was on, if it is still shown.
        int row = newSort ? 0 : view.ViewRow(keepRow);
        Table.Select(row >= -view.PinnedRows ? row : 0, Table.Active.Column);
        UpdateInfo();
    }

    private void SortUp_Click(object sender, RoutedEventArgs e) => _ = ApplyOrderAsync(Table.Active.Column, false, _view?.OnlyMatches == true);

    private void SortDown_Click(object sender, RoutedEventArgs e) => _ = ApplyOrderAsync(Table.Active.Column, true, _view?.OnlyMatches == true);

    private void SortOff_Click(object sender, RoutedEventArgs e) => _ = ApplyOrderAsync(-1, false, _view?.OnlyMatches == true);

    private void OnlyMatches_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncing || _view is not { } view) return;
        _ = ApplyOrderAsync(view.SortColumn, view.SortDescending, OnlyMatches.IsChecked == true);
    }

    // ---- Search ----

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        // A short pause after typing, so a big table isn't searched for every letter. The timer stops itself.
        _searchDelay ??= new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) =>
        {
            _searchDelay!.Stop();
            StartSearch();
        }, Dispatcher);
        _searchDelay.Stop();
        _searchDelay.Start();
    }

    private void SearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                if (_searchDelay?.IsEnabled == true)
                {
                    _searchDelay.Stop();
                    StartSearch();
                }
                else GoToMatch(forward: (Keyboard.Modifiers & ModifierKeys.Shift) == 0);
                e.Handled = true;
                break;
            case Key.Escape:
                if (SearchBox.Text.Length > 0) SearchBox.Clear();
                else Table.Focus();
                e.Handled = true;
                break;
            case Key.Down:
                Table.Focus();
                e.Handled = true;
                break;
        }
    }

    private async void StartSearch()
    {
        if (_view is not { } view) return;
        _search?.Cancel();
        string query = SearchBox.Text.Trim();
        if (query.Length == 0)
        {
            _matches = null;
            view.SetMatches(null, 0);
            UpdateSearchUi();
            if (view.OnlyMatches)
            {
                _syncing = true;
                OnlyMatches.IsChecked = false;
                _syncing = false;
                await ApplyOrderAsync(view.SortColumn, view.SortDescending, false);
            }
            Table.Refresh();
            return;
        }

        var cancel = _search = new CancellationTokenSource();
        SearchInfo.Text = "ищу…";
        Dictionary<int, int[]> matches;
        int count;
        try
        {
            (matches, count) = await Task.Run(() => TableView.Find(view.Sheet, query, SearchLimit, cancel.Token), cancel.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (cancel == _search) SearchInfo.Text = "файл недоступен";
            return;
        }
        if (cancel != _search || view != _view) return;

        _matches = matches;
        view.SetMatches(matches, count);
        UpdateSearchUi();
        if (view.OnlyMatches) await ApplyOrderAsync(view.SortColumn, view.SortDescending, true);
        Table.Refresh();
        if (count > 0) GoToMatch(forward: true, includeCurrent: true);
    }

    private void UpdateSearchUi()
    {
        var view = _view;
        bool any = view?.HasMatches == true;
        PrevMatch.IsEnabled = NextMatch.IsEnabled = any;
        OnlyMatches.IsEnabled = any || OnlyMatches.IsChecked == true;
        if (view is null || SearchBox.Text.Trim().Length == 0) SearchInfo.Text = "";
        else if (!any) SearchInfo.Text = "не найдено";
        else
        {
            string count = RuText.Count(view.MatchCount, "ячейка", "ячейки", "ячеек");
            SearchInfo.Text = view.MatchCount >= SearchLimit ? "больше миллиона" : count;
        }
    }

    /// <summary>The next (or previous) cell with a hit, in the order shown, wrapping around.</summary>
    private void GoToMatch(bool forward, bool includeCurrent = false)
    {
        if (_view is not { HasMatches: true } view) return;
        int minRow = -view.PinnedRows;
        int total = view.RowCount - minRow;
        if (total <= 0) return;
        var (row, column) = Table.Active;
        int current = Math.Clamp(row, minRow, view.RowCount - 1) - minRow;
        for (int step = 0; step <= total; step++)
        {
            int index = forward ? (current + step) % total : ((current - step) % total + total) % total;
            int viewRow = index + minRow;
            if (view.MatchColumns(view.SheetRow(viewRow)) is not { Count: > 0 } columns) continue;
            int found = -1;
            if (forward)
            {
                foreach (int c in columns)
                {
                    bool ok = step == 0 ? (includeCurrent ? c >= column : c > column) : step == total ? c <= column : true;
                    if (ok) { found = c; break; }
                }
            }
            else
            {
                for (int i = columns.Count - 1; i >= 0; i--)
                {
                    int c = columns[i];
                    bool ok = step == 0 ? c < column : step == total ? c >= column : true;
                    if (ok) { found = c; break; }
                }
            }
            if (found >= 0)
            {
                Table.Select(viewRow, found);
                return;
            }
        }
    }

    private void PrevMatch_Click(object sender, RoutedEventArgs e) => GoToMatch(forward: false);

    private void NextMatch_Click(object sender, RoutedEventArgs e) => GoToMatch(forward: true);

    // ---- Active cell and selection ----

    private void ShowActiveCell()
    {
        if (_view is not { } view || view.ColumnCount == 0)
        {
            CellAddress.Text = "";
            CellText.Text = "";
            SelectionInfo.Text = "";
            return;
        }
        var (row, column) = Table.Active;
        int sheetRow = view.SheetRow(row);
        CellAddress.Text = Sheet.ColumnName(column) + (sheetRow + 1).ToString(CultureInfo.InvariantCulture);
        CellText.Text = view.Sheet.Text(sheetRow, column) ?? "";
        CellText.ScrollToHome();
        SelectionInfo.Text = SelectionStats(view);
    }

    /// <summary>"Выделено 12 · сумма 1 234 · среднее 102,8" for a selection of several cells, like Excel's status bar.</summary>
    private string SelectionStats(TableView view)
    {
        int cells = Table.SelectionCellCount;
        if (cells <= 1) return "";
        if (cells > StatsLimit) return $"Выделено {cells.ToString("N0", CultureInfo.CurrentCulture)} ячеек";
        var (top, left, bottom, right) = Table.Selection;
        int filled = 0, numbers = 0;
        double sum = 0;
        for (int r = top; r <= bottom; r++)
        {
            int sheetRow = view.SheetRow(r);
            for (int c = left; c <= right; c++)
            {
                if (view.Sheet.Text(sheetRow, c) is null) continue;
                filled++;
                if (r >= 0 && view.Sheet.TryNumber(sheetRow, c, out double value))
                {
                    numbers++;
                    sum += value;
                }
            }
        }
        var text = new StringBuilder($"Заполнено {filled.ToString("N0", CultureInfo.CurrentCulture)}");
        if (numbers > 0)
        {
            text.Append("  ·  сумма ").Append(sum.ToString("#,0.##", CultureInfo.CurrentCulture));
            text.Append("  ·  среднее ").Append((sum / numbers).ToString("#,0.##", CultureInfo.CurrentCulture));
        }
        return text.ToString();
    }

    private void Copy_Click(object sender, RoutedEventArgs e) => CopySelection();

    private void CopySelection()
    {
        string? text = Table.SelectionText();
        if (text is null)
        {
            SelectionInfo.Text = "Слишком много ячеек, чтобы скопировать за раз";
            return;
        }
        if (SetClipboard(text)) SelectionInfo.Text = Table.SelectionCellCount > 1 ? "Скопировано" : SelectionInfo.Text;
    }

    private void CopyRow_Click(object sender, RoutedEventArgs e)
    {
        if (_view is not { } view) return;
        int sheetRow = view.SheetRow(Table.Active.Row);
        var values = Enumerable.Range(0, view.ColumnCount).Select(c => view.Sheet.Text(sheetRow, c) ?? "");
        SetClipboard(string.Join('\t', values));
    }

    /// <summary>The clipboard may be busy for a moment (another program reads it): try a few times.</summary>
    private static bool SetClipboard(string text)
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                Clipboard.SetText(text.Length == 0 ? " " : text);
                return true;
            }
            catch (COMException)
            {
                Thread.Sleep(40);
            }
        }
        return false;
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e) => Table.SelectAll();

    private void TableMenu_Opened(object sender, RoutedEventArgs e)
    {
        bool ready = _view is { ColumnCount: > 0 };
        MenuCopy.IsEnabled = ready;
        MenuSortUp.IsEnabled = MenuSortDown.IsEnabled = ready;
        MenuSortOff.IsEnabled = _view?.SortColumn >= 0;
        string column = ready ? Sheet.ColumnName(Table.Active.Column) : "";
        MenuSortUp.Header = $"Сортировать {column} по возрастанию";
        MenuSortDown.Header = $"Сортировать {column} по убыванию";
    }

    // ---- Scroll bars ----

    private void SyncScrollBars()
    {
        if (_syncing) return;
        _syncing = true;
        int rows = _view?.RowCount ?? 0;
        int visible = Table.VisibleRows;
        VScroll.Maximum = Math.Max(0, rows - visible + 1);
        VScroll.ViewportSize = visible;
        VScroll.LargeChange = Math.Max(1, visible - 1);
        VScroll.Value = Table.FirstRow;
        VScroll.IsEnabled = VScroll.Maximum > 0;

        double data = Table.DataWidth;
        HScroll.Maximum = Math.Max(0, Table.TotalWidth - data + 40);
        HScroll.ViewportSize = Math.Max(1, data);
        HScroll.LargeChange = Math.Max(40, data * 0.8);
        HScroll.Value = Table.OffsetX;
        HScroll.IsEnabled = HScroll.Maximum > 0;
        _syncing = false;
    }

    private void VScroll_Scroll(object sender, ScrollEventArgs e) => Table.ScrollToRow(VScroll.Value);

    private void HScroll_Scroll(object sender, ScrollEventArgs e) => Table.ScrollToX(HScroll.Value);

    // ---- Zoom, full screen, reload, other files ----

    private void ZoomIn_Click(object sender, RoutedEventArgs e) => Table.Zoom += 0.1;

    private void ZoomOut_Click(object sender, RoutedEventArgs e) => Table.Zoom -= 0.1;

    private void ZoomReset_Click(object sender, RoutedEventArgs e) => Table.Zoom = 1;

    private void FullScreen_Click(object sender, RoutedEventArgs e) => ToggleFullScreen();

    private void ToggleFullScreen()
    {
        if (!_fullScreen)
        {
            _savedState = WindowState;
            _savedStyle = WindowStyle;
            _savedResize = ResizeMode;
            // A window without a frame, maximized, covers the taskbar too.
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            if (WindowState == WindowState.Maximized) WindowState = WindowState.Normal;
            WindowState = WindowState.Maximized;
            FullScreenButton.Content = "";
            FullScreenButton.ToolTip = "Выйти из полноэкранного режима   F11 или Esc";
            _fullScreen = true;
        }
        else
        {
            WindowStyle = _savedStyle;
            ResizeMode = _savedResize;
            WindowState = WindowState.Normal;
            WindowState = _savedState;
            FullScreenButton.Content = "";
            FullScreenButton.ToolTip = "Во весь экран   F11";
            _fullScreen = false;
        }
        Table.Focus();
    }

    private void Reload_Click(object sender, RoutedEventArgs e) => _ = LoadAsync(_book?.Csv);

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        if (App.PickFile(this) is not { } file) return;
        OpenHereOrNew(file);
    }

    /// <summary>Into this window when it shows nothing (an error), else a new window.</summary>
    private void OpenHereOrNew(string file)
    {
        if (_book is null)
        {
            _path = file;
            _ = LoadAsync(null);
        }
        else new TableWindow(file).Show();
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = DroppedFiles(e).Length > 0 ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        var files = DroppedFiles(e);
        e.Handled = true;
        foreach (var file in files.Take(12)) OpenHereOrNew(file);
    }

    private static string[] DroppedFiles(DragEventArgs e) =>
        e.Data.GetDataPresent(DataFormats.FileDrop) && e.Data.GetData(DataFormats.FileDrop) is string[] files
            ? Array.FindAll(files, f => File.Exists(f) && App.IsTable(f))
            : [];

    // ---- CSV: encoding and delimiter ----

    private static readonly int[] CodePages = [65001, 1251, 866, 20866, 1200, 1252];

    private static string EncodingName(Encoding encoding) => encoding.CodePage switch
    {
        65001 => "UTF-8",
        1251 => "Windows-1251",
        866 => "DOS-866",
        20866 => "KOI8-R",
        1200 => "UTF-16",
        1201 => "UTF-16 BE",
        1252 => "Windows-1252",
        _ => encoding.WebName,
    };

    private void UpdateCsvButtons()
    {
        if (_book?.Csv is not { } csv)
        {
            EncodingButton.Visibility = DelimiterButton.Visibility = Visibility.Collapsed;
            return;
        }
        EncodingButton.Content = $"Кодировка: {EncodingName(csv.Encoding)}";
        DelimiterButton.Content = $"Разделитель: {CsvFormat.DelimiterName(csv.Delimiter)}";
        EncodingButton.Visibility = DelimiterButton.Visibility = Visibility.Visible;
    }

    private void EncodingButton_Click(object sender, RoutedEventArgs e)
    {
        if (_book?.Csv is not { } csv) return;
        var menu = new ContextMenu { PlacementTarget = EncodingButton, Placement = PlacementMode.Top };
        foreach (int codePage in CodePages)
        {
            var encoding = codePage == 65001 ? new UTF8Encoding(false) : Encoding.GetEncoding(codePage);
            var item = new MenuItem
            {
                Header = codePage switch
                {
                    1251 => "Windows-1251 (кириллица)",
                    866 => "DOS-866 (старая кириллица)",
                    1252 => "Windows-1252 (западная)",
                    _ => EncodingName(encoding),
                },
                IsChecked = csv.Encoding.CodePage == codePage,
            };
            item.Click += (_, _) => _ = LoadAsync(csv with { Encoding = encoding });
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    private void DelimiterButton_Click(object sender, RoutedEventArgs e)
    {
        if (_book?.Csv is not { } csv) return;
        var menu = new ContextMenu { PlacementTarget = DelimiterButton, Placement = PlacementMode.Top };
        foreach (char delimiter in CsvFormat.Delimiters)
        {
            var item = new MenuItem
            {
                Header = CsvFormat.DelimiterName(delimiter) + (delimiter is ';' or ',' or '|' ? $"   {delimiter}" : ""),
                IsChecked = csv.Delimiter == delimiter,
            };
            item.Click += (_, _) => _ = LoadAsync(csv with { Delimiter = delimiter });
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    // ---- Keys ----

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        switch (e.Key)
        {
            case Key.F when ctrl:
                SearchBox.Focus();
                SearchBox.SelectAll();
                break;
            case Key.F3:
                GoToMatch(forward: !shift);
                break;
            case Key.F5:
                _ = LoadAsync(_book?.Csv);
                break;
            case Key.F11:
                ToggleFullScreen();
                break;
            case Key.Escape when _fullScreen:
                ToggleFullScreen();
                break;
            case Key.O when ctrl:
                Open_Click(this, e);
                break;
            case Key.W when ctrl:
                Close();
                break;
            case Key.C when ctrl && Table.IsKeyboardFocused:
                CopySelection();
                break;
            default:
                return;
        }
        e.Handled = true;
    }
}
