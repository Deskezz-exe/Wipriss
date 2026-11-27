using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Wipriss.Reader.Data;

namespace Wipriss.Reader;

/// <summary>
/// The formatting bar under the cell's text: size, bold, italic, underline, colors, alignment, merging and how a
/// long text is shown. It changes the selected cells as one step (Ctrl+Z takes it back). Only XLSX/XLSM keep
/// formatting; for a CSV the bar is shown greyed out with a hint to export.
/// </summary>
public partial class TableWindow
{
    private const int MaxFormatCells = 1_000_000;
    private static readonly double[] Sizes = [8, 9, 10, 11, 12, 14, 16, 18, 20, 24, 28, 36, 48, 72];
    private static readonly uint[] Palette =
    [
        0xFFC00000, 0xFFFF0000, 0xFFFFC000, 0xFFFFFF00, 0xFF92D050, 0xFF00B050,
        0xFF00B0F0, 0xFF0070C0, 0xFF002060, 0xFF7030A0, 0xFFFFFFFF, 0xFF808080,
    ];

    private bool _pickingFill;

    private bool CanFormat => _editing && _book?.Format == TableFormat.Excel && Edited is not null;

    /// <summary>Shows the bar for editable tables and the look of the active cell on it.</summary>
    private void UpdateFormatBar()
    {
        bool shown = _editing && _book is not null && Edited is not null;
        FormatArea.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
        if (!shown) return;
        bool can = CanFormat;
        FormatBar.IsEnabled = can;
        FormatBar.Opacity = can ? 1 : 0.55;
        FormatBlocker.Visibility = can ? Visibility.Collapsed : Visibility.Visible;

        var sheet = Edited!;
        var format = CellFormat.Plain;
        bool merged = false;
        if (can && _view is { } view && view.ColumnCount > 0)
        {
            var (row, column) = Table.Active;
            var (sheetRow, sheetColumn) = Target(row, column);
            format = sheet.Format(sheetRow, sheetColumn);
            merged = (view.KeepsFileOrder || row < 0) && sheet.Merges.Any(m => m.Contains(sheetRow, sheetColumn));
        }
        double size = format.Size > 0 ? format.Size : sheet.DefaultFontSize;
        FontSizeButton.Content = size.ToString("0.#", CultureInfo.CurrentCulture);
        BoldToggle.IsChecked = format.Bold;
        ItalicToggle.IsChecked = format.Italic;
        UnderlineToggle.IsChecked = format.Underline;
        TextColorBar.Background = format.Color is { } color ? Brush(color) : (Brush)FindResource("TextBrush");
        FillColorBar.Background = format.Fill is { } fill ? Brush(fill) : Brushes.Transparent;
        AlignLeft.IsChecked = format.Horizontal == HAlign.Left;
        AlignCenter.IsChecked = format.Horizontal == HAlign.Center;
        AlignRight.IsChecked = format.Horizontal == HAlign.Right;
        AlignTop.IsChecked = format.Vertical == VAlign.Top;
        AlignMiddle.IsChecked = format.Vertical == VAlign.Center;
        AlignBottom.IsChecked = can && format.Vertical == VAlign.Bottom;
        MergeToggle.IsChecked = merged;
        MergeToggle.Content = merged ? "Разъединить" : "Объединить";
        TextModeButton.Content = (format.Mode switch
        {
            TextMode.Wrap => "Переносить",
            TextMode.Overflow => "Целиком",
            _ => "Обрезать",
        }) + "  ▾";
        ModeClip.IsChecked = format.Mode == TextMode.Clip;
        ModeWrap.IsChecked = format.Mode == TextMode.Wrap;
        ModeOverflow.IsChecked = format.Mode == TextMode.Overflow;
    }

    private static SolidColorBrush Brush(uint argb)
    {
        var brush = new SolidColorBrush(Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));
        brush.Freeze();
        return brush;
    }

    /// <summary>The look of the active cell, the one the bar shows (and toggles start from).</summary>
    private CellFormat ActiveFormat()
    {
        var (row, column) = Table.Active;
        var (sheetRow, sheetColumn) = Target(row, column);
        return Edited!.Format(sheetRow, sheetColumn);
    }

    /// <summary>Changes the look of every selected cell (a merged area through its first cell) as one step.</summary>
    private void Restyle(Func<CellFormat, CellFormat> change)
    {
        if (!CanFormat || _view is null) return;
        Commit();
        var (top, left, bottom, right) = Table.Selection;
        long count = (long)(bottom - top + 1) * (right - left + 1);
        if (count > MaxFormatCells)
        {
            SelectionInfo.Text = "Выделено слишком много ячеек: оформляйте частями (до миллиона)";
            return;
        }
        var sheet = Edited!;
        var cells = new List<(int Row, int Column)>((int)count);
        for (int r = top; r <= bottom; r++)
            for (int c = left; c <= right; c++) cells.Add(Target(r, c));
        int changed = sheet.SetFormat(cells, change);
        if (changed == 0) return;
        AfterChange();
        Table.Focus();
    }

    // ---- Size ----

    private void FontSize_Click(object sender, RoutedEventArgs e)
    {
        if (!CanFormat) return;
        double current = ActiveFormat().Size is var s && s > 0 ? s : Edited!.DefaultFontSize;
        var menu = new ContextMenu { PlacementTarget = FontSizeButton, Placement = PlacementMode.Bottom };
        foreach (double size in Sizes)
        {
            var item = new MenuItem { Header = size.ToString("0.#", CultureInfo.CurrentCulture), IsChecked = Math.Abs(size - current) < 0.01 };
            item.Click += (_, _) => SetSize(size);
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    private void FontSmaller_Click(object sender, RoutedEventArgs e) => StepSize(-1);

    private void FontBigger_Click(object sender, RoutedEventArgs e) => StepSize(+1);

    private void StepSize(int direction)
    {
        if (!CanFormat) return;
        double usual = Edited!.DefaultFontSize;
        // Each cell steps from its own size to the next one in the list.
        Restyle(f =>
        {
            double size = f.Size > 0 ? f.Size : usual;
            double next = direction > 0
                ? Sizes.FirstOrDefault(x => x > size + 0.01, Sizes[^1])
                : Sizes.LastOrDefault(x => x < size - 0.01, Sizes[0]);
            return f with { Size = Math.Abs(next - usual) < 0.01 ? 0 : next };
        });
    }

    private void SetSize(double size)
    {
        double usual = Edited!.DefaultFontSize;
        Restyle(f => f with { Size = Math.Abs(size - usual) < 0.01 ? 0 : size });
    }

    // ---- Bold, italic, underline ----

    private void Bold_Click(object sender, RoutedEventArgs e) => ToggleBold();

    private void Italic_Click(object sender, RoutedEventArgs e) => ToggleItalic();

    private void Underline_Click(object sender, RoutedEventArgs e) => ToggleUnderline();

    private void ToggleBold()
    {
        if (!CanFormat) return;
        bool on = !ActiveFormat().Bold;
        Restyle(f => f with { Bold = on });
    }

    private void ToggleItalic()
    {
        if (!CanFormat) return;
        bool on = !ActiveFormat().Italic;
        Restyle(f => f with { Italic = on });
    }

    private void ToggleUnderline()
    {
        if (!CanFormat) return;
        bool on = !ActiveFormat().Underline;
        Restyle(f => f with { Underline = on });
    }

    // ---- Colors ----

    private void TextColor_Click(object sender, RoutedEventArgs e) => OpenColors(fill: false, TextColorButton);

    private void FillColor_Click(object sender, RoutedEventArgs e) => OpenColors(fill: true, FillColorButton);

    private void OpenColors(bool fill, UIElement target)
    {
        if (!CanFormat) return;
        _pickingFill = fill;
        ColorAuto.Content = fill ? "Без заливки" : "Обычный цвет";
        Swatches.Children.Clear();
        foreach (uint color in Palette)
        {
            var swatch = new Button { Style = (Style)FindResource("Swatch"), Background = Brush(color), ToolTip = "#" + (color & 0xFFFFFF).ToString("X6", CultureInfo.InvariantCulture) };
            swatch.Click += (_, _) => PickColor(color);
            Swatches.Children.Add(swatch);
        }
        ColorPopup.PlacementTarget = target;
        ColorPopup.IsOpen = true;
    }

    private void ColorAuto_Click(object sender, RoutedEventArgs e) => PickColor(null);

    private void PickColor(uint? color)
    {
        ColorPopup.IsOpen = false;
        if (_pickingFill) Restyle(f => f with { Fill = color });
        else Restyle(f => f with { Color = color });
    }

    // ---- Alignment ----

    private void Horizontal_Click(object sender, RoutedEventArgs e)
    {
        var align = sender == AlignLeft ? HAlign.Left : sender == AlignCenter ? HAlign.Center : HAlign.Right;
        // Pressing the pressed one again goes back to the usual (text left, numbers right).
        if (CanFormat && ActiveFormat().Horizontal == align) align = HAlign.General;
        Restyle(f => f with { Horizontal = align });
        UpdateFormatBar();
    }

    private void Vertical_Click(object sender, RoutedEventArgs e)
    {
        var align = sender == AlignTop ? VAlign.Top : sender == AlignMiddle ? VAlign.Center : VAlign.Bottom;
        Restyle(f => f with { Vertical = align });
        UpdateFormatBar();
    }

    // ---- Long text ----

    private void TextMode_Click(object sender, RoutedEventArgs e)
    {
        if (TextModeButton.ContextMenu is not { } menu) return;
        menu.PlacementTarget = TextModeButton;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void ModeClip_Click(object sender, RoutedEventArgs e) => Restyle(f => f with { Mode = TextMode.Clip });

    private void ModeWrap_Click(object sender, RoutedEventArgs e) => Restyle(f => f with { Mode = TextMode.Wrap });

    private void ModeOverflow_Click(object sender, RoutedEventArgs e) => Restyle(f => f with { Mode = TextMode.Overflow });

    // ---- Merging ----

    private void Merge_Click(object sender, RoutedEventArgs e)
    {
        UpdateFormatBar(); // the toggle shows the state, not the click
        if (!CanFormat || _view is not { } view) return;
        Commit();
        var sheet = Edited!;
        var (top, left, bottom, right) = Table.Selection;
        if (!view.KeepsFileOrder && bottom >= 0)
        {
            SelectionInfo.Text = "Объединять можно, когда строки в порядке файла: уберите сортировку и «Только найденные»";
            return;
        }
        int fromRow = view.SheetRow(top), toRow = view.SheetRow(bottom);
        var range = new CellRange(Math.Min(fromRow, toRow), left, Math.Max(fromRow, toRow), right);

        if (sheet.Merges.Any(m => EditedSheet.Intersects(m, range)) && MergeToggle.IsChecked == true)
        {
            sheet.Unmerge(range);
            AfterChange();
            Table.Focus();
            return;
        }
        var area = sheet.MergeArea(range);
        if (area.FromRow == area.ToRow && area.FromColumn == area.ToColumn)
        {
            SelectionInfo.Text = "Выделите несколько ячеек, чтобы объединить их";
            return;
        }
        if ((long)(area.ToRow - area.FromRow + 1) * (area.ToColumn - area.FromColumn + 1) > MaxFormatCells)
        {
            SelectionInfo.Text = "Слишком большая область для объединения";
            return;
        }
        for (int r = area.FromRow; r <= area.ToRow; r++)
        {
            for (int c = area.FromColumn; c <= area.ToColumn; c++)
            {
                if (sheet.LockReason(r, c) is { } why)
                {
                    MessageBox.Show(this, $"Эти ячейки нельзя объединить: {char.ToLower(why[0], CultureInfo.CurrentCulture)}{why[1..]}", "Wipriss",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
            }
        }
        int lost = sheet.TextsLostByMerge(area);
        if (lost > 0)
        {
            var answer = MessageBox.Show(this,
                $"В объединённой ячейке останется только текст левой верхней. Текст ещё в {Core.Text.RuText.Count(lost, "ячейке", "ячейках", "ячейках")} пропадёт (Ctrl+Z вернёт). Объединить?",
                "Wipriss", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes) return;
        }
        if (sheet.Merge(area) is null) return;
        int viewTop = view.ViewRow(area.FromRow);
        Table.Select(viewTop, area.FromColumn);
        AfterChange();
        Table.Focus();
    }
}
