using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using Wipriss.Reader.Data;

namespace Wipriss.Reader;

/// <summary>
/// Editing: the "Правка" switch, the text box over a cell, Delete, paste, undo, and saving. The file changes only
/// when the owner saves; the old version then goes to the Recycle Bin.
/// </summary>
public partial class TableWindow
{
    private const int MaxPasteCells = 1_000_000;
    private const int MaxClearCells = 2_000_000;

    private bool _editing;
    private bool _saving;
    /// <summary>The cell in the text box: its sheet row and column, and its row in the view (where the box is).</summary>
    private (int SheetRow, int Column, int ViewRow)? _editCell;
    /// <summary>Editing started by typing: arrows then finish it and move, like in Excel.</summary>
    private bool _typedEdit;
    private (int SheetRow, int Column)? _barCell;
    private CancellationTokenSource? _locks;

    private EditedSheet? Edited => _view?.Sheet as EditedSheet;

    private int TotalChanges => _book?.Sheets.OfType<EditedSheet>().Sum(s => s.ChangeCount) ?? 0;

    /// <summary>The sheet as it was read (under the layer of changes).</summary>
    private static Sheet Source(Sheet sheet) => sheet is EditedSheet edited ? edited.Inner : sheet;

    // ---- Editing is on for the formats Wipriss can save ----

    private void SetEditing(bool on)
    {
        if (!on) Commit();
        _editing = on;
        Table.CanEdit = on;
        if (_book is { } book)
        {
            foreach (var sheet in book.Sheets.OfType<EditedSheet>()) sheet.Editing = on;
            if (on && book.Format == TableFormat.Excel) _ = FindLocksAsync(book);
        }
        UpdateSaveButton();
    }

    /// <summary>Which cells of an Excel file can't be changed safely (array formulas, smart table headers).</summary>
    private async Task FindLocksAsync(Workbook book)
    {
        var sheets = book.Sheets.OfType<EditedSheet>().Where(s => s.Locks is null).ToList();
        if (sheets.Count == 0) return;
        _locks?.Cancel();
        var cancel = _locks = new CancellationTokenSource();
        try
        {
            var found = await Task.Run(() =>
            {
                using var zip = new ZipArchive(Workbook.OpenShared(book.Path), ZipArchiveMode.Read);
                var parts = new XlsxParts(zip);
                var result = new Dictionary<EditedSheet, Dictionary<(int Row, int Column), string>>();
                foreach (var sheet in sheets)
                {
                    cancel.Token.ThrowIfCancellationRequested();
                    result[sheet] = parts.SheetPaths.TryGetValue(sheet.Name, out var path) ? parts.Locks(path) : [];
                }
                return result;
            }, cancel.Token);
            foreach (var (sheet, locks) in found) sheet.Locks = locks;
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or InvalidDataException or System.Xml.XmlException
                                   or UnauthorizedAccessException)
        {
            // Without the list the check still happens when saving.
        }
    }

    /// <summary>After any change: the table may have grown or shrunk, the counts and the active cell are redrawn.</summary>
    private void AfterChange()
    {
        _view?.SyncRows();
        Table.SyncColumns();
        Table.Refresh();
        UpdateInfo();
        UpdateSaveButton();
        ShowActiveCell();
    }

    private void UpdateSaveButton()
    {
        int changes = TotalChanges;
        SaveButton.Visibility = changes > 0 ? Visibility.Visible : Visibility.Collapsed;
        SaveButton.IsEnabled = changes > 0 && !_saving;
        SaveButton.Content = changes > 0 ? $"Сохранить ({changes.ToString("N0", CultureInfo.CurrentCulture)})" : "Сохранить";
        Title = (changes > 0 ? "● " : "") + $"{Path.GetFileName(_path)} · Wipriss";
    }

    // ---- The text box over a cell ----

    /// <summary>The cell where a change lands: the first cell of a merged area, or the cell itself.</summary>
    private (int SheetRow, int Column) Target(int viewRow, int column)
    {
        var view = _view!;
        int sheetRow = view.SheetRow(viewRow);
        if (view.KeepsFileOrder || viewRow < 0)
        {
            foreach (var m in view.Sheet.Merges)
            {
                if (m.Contains(sheetRow, column)) return (m.FromRow, m.FromColumn);
            }
        }
        return (sheetRow, column);
    }

    private void BeginEdit(string? typed)
    {
        if (!_editing || _saving || Edited is not { } sheet || _view is not { } view) return;
        Commit();
        var (row, column) = Table.Active;
        var (sheetRow, targetColumn) = Target(row, column);
        if (sheet.LockReason(sheetRow, targetColumn) is { } why)
        {
            SelectionInfo.Text = why;
            return;
        }
        if (Table.CellRect(row, column) is not { } rect) return;
        _editCell = (sheetRow, targetColumn, row);
        _typedEdit = typed is not null;
        CellEditor.FontSize = Table.CellFontSize;
        PlaceEditor(rect);
        CellEditor.Text = typed ?? sheet.Text(sheetRow, targetColumn) ?? "";
        CellEditor.Visibility = Visibility.Visible;
        CellEditor.Focus();
        CellEditor.CaretIndex = CellEditor.Text.Length;
        CellText.Text = CellEditor.Text;
    }

    private void PlaceEditor(Rect rect)
    {
        Canvas.SetLeft(CellEditor, rect.X - 1);
        Canvas.SetTop(CellEditor, rect.Y - 1);
        CellEditor.Width = Math.Max(rect.Width + 2, 120);
        CellEditor.Height = rect.Height + 2;
    }

    /// <summary>Keeps the box on its cell while the table scrolls; finishes editing when the cell leaves the screen.</summary>
    private void FollowEditor()
    {
        if (_editCell is not { } cell) return;
        if (Table.CellRect(cell.ViewRow, cell.Column) is { } rect) PlaceEditor(rect);
        else Commit();
    }

    /// <summary>Saves the box's text into the cell (not to the file) and closes the box.</summary>
    private void Commit()
    {
        if (_editCell is not { } cell) return;
        _editCell = null;
        string text = CellEditor.Text;
        CellEditor.Visibility = Visibility.Collapsed;
        if (Edited is { } sheet)
        {
            sheet.Set([(cell.SheetRow, cell.Column, text)], out _);
            AfterChange();
        }
        if (CellEditor.IsKeyboardFocusWithin) Table.Focus();
    }

    private void CancelEdit()
    {
        if (_editCell is null) return;
        _editCell = null;
        CellEditor.Visibility = Visibility.Collapsed;
        Table.Focus();
        ShowActiveCell();
    }

    private void CellEditor_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var modifiers = Keyboard.Modifiers;
        bool shift = (modifiers & ModifierKeys.Shift) != 0;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var (row, column) = Table.Active;
        switch (key)
        {
            case Key.Enter when (modifiers & ModifierKeys.Alt) != 0:
                // Alt+Enter: a line break inside the cell, like in Excel.
                int caret = CellEditor.CaretIndex;
                CellEditor.Text = CellEditor.Text.Insert(caret, "\n");
                CellEditor.CaretIndex = caret + 1;
                break;
            case Key.Enter:
                Commit();
                Table.Select(row + (shift ? -1 : 1), column);
                break;
            case Key.Tab:
                Commit();
                Table.Select(row, column + (shift ? -1 : 1));
                break;
            case Key.Escape:
                CancelEdit();
                break;
            case Key.Up or Key.Down or Key.Left or Key.Right when _typedEdit && (modifiers & ModifierKeys.Control) == 0:
                Commit();
                Table.Select(row + (key == Key.Down ? 1 : key == Key.Up ? -1 : 0), column + (key == Key.Right ? 1 : key == Key.Left ? -1 : 0));
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    private void CellEditor_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_editCell is not null) CellText.Text = CellEditor.Text;
    }

    private void CellEditor_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        // A context menu or the bar above takes the focus for a moment: the text box keeps it as typed.
        if (e.NewFocus is System.Windows.Controls.ContextMenu or MenuItem) return;
        Commit();
    }

    // ---- The bar above the table: the whole text of the active cell, editable too ----

    private void CellText_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (_editCell is not null) return;
        _barCell = _editing && _view is not null && !CellText.IsReadOnly ? Target(Table.Active.Row, Table.Active.Column) : null;
    }

    private void CellText_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => CommitBar();

    private void CommitBar()
    {
        if (_barCell is not { } cell || Edited is not { } sheet) return;
        _barCell = null;
        if ((sheet.Text(cell.SheetRow, cell.Column) ?? "") == CellText.Text) return;
        sheet.Set([(cell.SheetRow, cell.Column, CellText.Text)], out _);
        AfterChange();
    }

    private void CellText_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_barCell is null) return;
        var modifiers = Keyboard.Modifiers;
        if (e.Key == Key.Enter && (modifiers & (ModifierKeys.Alt | ModifierKeys.Shift)) == 0)
        {
            CommitBar();
            Table.Focus();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            _barCell = null;
            Table.Focus();
            ShowActiveCell();
            e.Handled = true;
        }
    }

    // ---- Delete, paste, undo ----

    private void Clear_Click(object sender, RoutedEventArgs e) => ClearSelection();

    private void ClearSelection()
    {
        if (!_editing || _saving || Edited is not { } sheet || _view is not { } view) return;
        if (Table.SelectionCellCount > MaxClearCells)
        {
            SelectionInfo.Text = "Слишком много ячеек, чтобы очистить за раз";
            return;
        }
        var (top, left, bottom, right) = Table.Selection;
        right = Math.Min(right, sheet.DataColumnCount - 1);
        var cells = new List<(int, int, string?)>();
        for (int r = top; r <= bottom; r++)
        {
            int sheetRow = view.SheetRow(r);
            if (sheetRow >= sheet.DataRowCount) continue;
            for (int c = left; c <= right; c++)
            {
                if (sheet.Text(sheetRow, c) is not null) cells.Add((sheetRow, c, null));
            }
        }
        Apply(sheet, cells);
    }

    private void Paste_Click(object sender, RoutedEventArgs e) => Paste();

    /// <summary>Pastes a table from the clipboard (tab-separated, as Excel and Google Sheets copy it).</summary>
    private void Paste()
    {
        if (!_editing || _saving || Edited is not { } sheet || _view is not { } view) return;
        string text;
        try
        {
            if (!Clipboard.ContainsText()) return;
            text = Clipboard.GetText();
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            SelectionInfo.Text = "Буфер обмена занят другой программой, попробуйте ещё раз";
            return;
        }
        if (text.Length == 0) return;
        var rows = CsvFormat.ReadRows(new StringReader(text.TrimEnd('\r', '\n')), '\t').Take(MaxPasteCells + 1).ToList();
        if (rows.Count == 0) return;
        long size = rows.Sum(r => (long)Math.Max(1, r.Length));
        if (size > MaxPasteCells)
        {
            SelectionInfo.Text = "Слишком большая таблица для вставки за раз";
            return;
        }

        var (top, left, bottom, right) = Table.Selection;
        var cells = new List<(int, int, string?)>();
        if (rows.Count == 1 && rows[0].Length <= 1 && Table.SelectionCellCount > 1 && Table.SelectionCellCount <= MaxPasteCells)
        {
            // One value onto a selection fills all of it, like in Excel.
            string? value = rows[0].Length == 1 ? rows[0][0] : null;
            for (int r = top; r <= bottom; r++)
                for (int c = left; c <= right; c++) cells.Add((SheetRowFor(r), c, value));
        }
        else
        {
            for (int i = 0; i < rows.Count; i++)
            {
                int sheetRow = SheetRowFor(top + i);
                var fields = rows[i];
                for (int j = 0; j < Math.Max(1, fields.Length); j++)
                    cells.Add((sheetRow, left + j, j < fields.Length ? fields[j] : null));
            }
        }
        Apply(sheet, cells);

        // Rows past the end of the view are new rows after the table.
        int SheetRowFor(int viewRow) => viewRow < view.RowCount ? view.SheetRow(viewRow) : sheet.RowCount + (viewRow - view.RowCount);
    }

    private void Apply(EditedSheet sheet, List<(int, int, string?)> cells)
    {
        Commit();
        int changed = sheet.Set(cells, out int locked);
        AfterChange();
        if (locked > 0)
            SelectionInfo.Text = $"{RuTextCells(locked)} можно изменить только в Excel: они пропущены";
        else if (changed > 1)
            SelectionInfo.Text = $"Изменено ячеек: {changed.ToString("N0", CultureInfo.CurrentCulture)}";
    }

    private static string RuTextCells(int count) => Core.Text.RuText.Count(count, "ячейку", "ячейки", "ячеек");

    private void Undo_Click(object sender, RoutedEventArgs e) => UndoRedo(undo: true);

    private void UndoRedo(bool undo)
    {
        if (!_editing || _saving || Edited is not { } sheet || _view is not { } view) return;
        Commit();
        var cell = undo ? sheet.Undo() : sheet.Redo();
        AfterChange();
        if (cell is { } at && view.ViewRow(at.Row) is var row and not int.MinValue) Table.Select(row, at.Column);
    }

    // ---- Saving ----

    private void Save_Click(object sender, RoutedEventArgs e) => _ = SaveAsync(copy: false);

    // ---- Export: the table as a new file, in the same or another format ----

    private void Export_Click(object sender, RoutedEventArgs e) => _ = ExportAsync();

    private async Task ExportAsync()
    {
        if (_book is not { } book || _view is not { } view || _saving) return;
        Commit();
        CommitBar();
        string ext = Path.GetExtension(book.Path).ToLowerInvariant();
        bool macros = ext == ".xlsm";
        // The first choice is the other kind of file: CSV becomes a workbook, a workbook becomes CSV.
        var choices = new List<(string Filter, string Ext, char Delimiter)>();
        var excel = macros ? ("Книга Excel с макросами (*.xlsm)|*.xlsm", ".xlsm", ' ') : ("Книга Excel (*.xlsx)|*.xlsx", ".xlsx", ' ');
        var semicolon = ("CSV, точка с запятой — для русского Excel (*.csv)|*.csv", ".csv", ';');
        var comma = ("CSV, запятая (*.csv)|*.csv", ".csv", ',');
        var tab = ("TSV, табуляция (*.tsv)|*.tsv", ".tsv", '	');
        if (book.Format == TableFormat.Csv) choices.AddRange([excel, semicolon, comma, tab]);
        else choices.AddRange([semicolon, comma, tab, excel]);
        if (macros) choices.Add(("Книга Excel без макросов (*.xlsx)|*.xlsx", ".xlsx", ' '));

        var dialog = new SaveFileDialog
        {
            Title = book.Sheets.Count > 1 ? "Экспорт таблицы (в CSV попадёт только открытый лист)" : "Экспорт таблицы",
            FileName = Path.GetFileNameWithoutExtension(book.Path),
            InitialDirectory = Path.GetDirectoryName(book.Path),
            Filter = string.Join("|", choices.Select(c => c.Filter)),
            FilterIndex = 1,
            AddExtension = true,
            OverwritePrompt = true,
        };
        if (dialog.ShowDialog(this) != true) return;
        var choice = choices[Math.Clamp(dialog.FilterIndex - 1, 0, choices.Count - 1)];
        string target = Path.ChangeExtension(dialog.FileName, choice.Ext);
        if (string.Equals(Path.GetFullPath(target), Path.GetFullPath(book.Path), StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(this, "Это тот же файл. Чтобы записать изменения в него, нажмите «Сохранить».", "Wipriss",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // A workbook into a workbook of the same kind keeps everything (formatting, formulas, charts) plus the changes.
        bool sameWorkbook = book.Format == TableFormat.Excel && book.CanEdit && choice.Ext == ext;
        var sheet = view.Sheet;
        var sheets = book.Sheets;
        _saving = true;
        UpdateSaveButton();
        ShowOverlay("Экспортирую…", Path.GetFileName(target), buttons: false);
        var culture = CultureInfo.CurrentCulture;
        try
        {
            await Task.Run(() =>
            {
                if (sameWorkbook)
                {
                    TableSaver.Save(book, target, culture, CancellationToken.None);
                    return;
                }
                TableSaver.SaveWith(target, temp =>
                {
                    using var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16);
                    if (choice.Delimiter == ' ') TableExport.WriteXlsx(sheets, output, CancellationToken.None);
                    else TableExport.WriteCsv(sheet, output, choice.Delimiter, CancellationToken.None);
                    output.Flush(flushToDisk: true);
                }, CancellationToken.None);
            });
            SelectionInfo.Text = $"Экспорт готов: {Path.GetFileName(target)}";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, SaveError(ex, book), "Не получилось экспортировать", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _saving = false;
            HideOverlay();
            UpdateSaveButton();
        }
    }

    private void Discard_Click(object sender, RoutedEventArgs e)
    {
        if (TotalChanges == 0) return;
        var answer = MessageBox.Show(this, "Отменить все несохранённые изменения? Файл останется как был.", "Wipriss",
            MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;
        CancelEdit();
        foreach (var sheet in _book!.Sheets.OfType<EditedSheet>()) sheet.Clear();
        AfterChange();
    }

    /// <summary>
    /// Saves to the file itself or, with <paramref name="copy"/>, to a file
    /// the owner picks; the window then shows that file. Returns true when saved.
    /// </summary>
    private async Task<bool> SaveAsync(bool copy)
    {
        if (_book is not { } book || _saving) return false;
        Commit();
        CommitBar();
        if (TotalChanges == 0) return true;

        string target = book.Path;
        if (copy)
        {
            if (PickCopy(book) is not { } picked) return false;
            target = picked;
        }
        else if (TableSaver.IsBusy(target))
        {
            var answer = MessageBox.Show(this,
                "Файл открыт в другой программе (например, в Excel), поэтому его нельзя заменить.\n\nЗакройте его там и нажмите «Сохранить» снова. Или сохранить изменения в копию?",
                "Wipriss", MessageBoxButton.YesNo, MessageBoxImage.Information, MessageBoxResult.No);
            return answer == MessageBoxResult.Yes && await SaveAsync(copy: true);
        }

        _saving = true;
        UpdateSaveButton();
        ShowOverlay("Сохраняю…", Path.GetFileName(target), buttons: false);
        var culture = CultureInfo.CurrentCulture;
        bool formulas = book.Format == TableFormat.Excel
            && book.Sheets.OfType<EditedSheet>().Any(s => s.Edits.Values.Any(t => t is { Length: > 1 } && t[0] == '='));
        try
        {
            await Task.Run(() => TableSaver.Save(book, target, culture, CancellationToken.None));
        }
        catch (Exception ex)
        {
            _saving = false;
            HideOverlay();
            UpdateSaveButton();
            MessageBox.Show(this, SaveError(ex, book), "Не получилось сохранить", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        _saving = false;
        HideOverlay();

        // Show what is in the file now; editing stays on, at the same place.
        foreach (var sheet in book.Sheets.OfType<EditedSheet>()) sheet.Clear();
        var keep = (Table.FirstRow, Table.Active.Row, Table.Active.Column);
        _path = target;
        await LoadAsync(book.Csv, keep);
        SelectionInfo.Text = formulas ? "Сохранено. Формулы посчитает Excel, когда откроет файл" : "Сохранено";
        return true;
    }

    private string? PickCopy(Workbook book)
    {
        string ext = Path.GetExtension(book.Path);
        var dialog = new SaveFileDialog
        {
            Title = "Сохранить копию таблицы",
            FileName = Path.GetFileNameWithoutExtension(book.Path) + " (копия)" + ext,
            InitialDirectory = Path.GetDirectoryName(book.Path),
            Filter = $"Таблица ({ext})|*{ext}",
            DefaultExt = ext,
            AddExtension = true,
            OverwritePrompt = true,
        };
        return dialog.ShowDialog(this) == true ? dialog.FileName : null;
    }

    private static string SaveError(Exception ex, Workbook book)
    {
        switch (ex)
        {
            case LockedCellsException locked:
                return locked.Message;
            case EncoderFallbackException encoder:
                string sign = encoder.CharUnknown != '\0' ? encoder.CharUnknown.ToString()
                    : encoder.CharUnknownHigh != '\0' ? new string([encoder.CharUnknownHigh, encoder.CharUnknownLow]) : "?";
                return $"Знак «{sign}» нельзя записать в кодировке этого файла ({book.Csv?.Encoding.WebName}). Уберите или замените его и сохраните снова.";
            case UnauthorizedAccessException:
                return "Windows не даёт записать в эту папку. Сохраните копию в другое место.";
            case IOException io when (io.HResult & 0xFFFF) is 32 or 33:
                return "Файл занят другой программой. Закройте её и сохраните снова.";
            case IOException io when (io.HResult & 0xFFFF) is 112 or 39:
                return "На диске не хватает места.";
            case IOException io:
                return $"Файл остался как был. {io.Message}";
            case InvalidDataException or System.Xml.XmlException:
                return "Файл устроен необычно, и Wipriss не смог безопасно записать в него изменения. Файл остался как был.";
        }
        Core.ErrorLog.Write(ex);
        return "Что-то пошло не так. Файл остался как был.";
    }

    /// <summary>Before something would throw the changes away: true when it may go on.</summary>
    private bool MayDropChanges(string what)
    {
        Commit();
        CommitBar();
        if (TotalChanges == 0) return true;
        var answer = MessageBox.Show(this, $"Изменения не сохранены. {what} и потерять их?", "Wipriss",
            MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        return answer == MessageBoxResult.Yes;
    }

    private bool _closeAfterSave;

    private async void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_closeAfterSave) return;
        if (_saving)
        {
            e.Cancel = true;
            return;
        }
        Commit();
        CommitBar();
        if (TotalChanges == 0) return;
        var answer = MessageBox.Show(this, $"Сохранить изменения в «{Path.GetFileName(_path)}»?", "Wipriss",
            MessageBoxButton.YesNoCancel, MessageBoxImage.Question, MessageBoxResult.Yes);
        if (answer == MessageBoxResult.No) return;
        e.Cancel = true;
        if (answer != MessageBoxResult.Yes) return;
        if (await SaveAsync(copy: false))
        {
            _closeAfterSave = true;
            Close();
        }
    }
}
