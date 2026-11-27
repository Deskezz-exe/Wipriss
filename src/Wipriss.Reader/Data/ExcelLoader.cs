using System.Globalization;
using System.IO;
using System.Text;
using ExcelDataReader;
using ExcelNumberFormat;

namespace Wipriss.Reader.Data;

/// <summary>
/// XLSX, XLSM, XLSB and old XLS through ExcelDataReader (MIT). It only reads the values a file has saved: formulas
/// are not recalculated, macros (VBA) and external links are never touched. Numbers and dates are shown with the
/// file's own number format (ExcelNumberFormat, MIT) in the owner's language.
/// </summary>
internal static class ExcelLoader
{
    /// <summary>Old XLS files keep text in Windows code pages.</summary>
    static ExcelLoader() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    /// <summary>Columns of one sheet beyond this are not read (Excel itself stops at 16 384).</summary>
    private const int MaxColumns = 16_384;

    public static Workbook Load(string path, IProgress<long>? progress, CancellationToken cancel)
    {
        var culture = CultureInfo.CurrentCulture;
        var formats = new Dictionary<string, NumberFormat?>(StringComparer.Ordinal);
        var sheets = new List<Sheet>();
        long total = 0;

        // Formatting (XLSX only) is read from the package at the same time, on another thread.
        var styles = Path.GetExtension(path).ToLowerInvariant() is ".xlsx" or ".xlsm" ? Task.Run(() => ReadStyles(path, cancel), cancel) : null;
        using var stream = Workbook.OpenShared(path);
        using var reader = ExcelReaderFactory.CreateReader(stream, new ExcelReaderConfiguration
        {
            FallbackEncoding = Encoding.GetEncoding(1251),
            LeaveOpen = false,
        });
        do
        {
            cancel.ThrowIfCancellationRequested();
            var sheet = new MemorySheet(reader.Name ?? $"Лист {sheets.Count + 1}")
            {
                Hidden = reader.VisibleState is { } state && !string.Equals(state, "visible", StringComparison.OrdinalIgnoreCase),
            };
            int fields = Math.Min(reader.FieldCount, MaxColumns);

            while (reader.Read())
            {
                cancel.ThrowIfCancellationRequested();
                int count = Math.Min(reader.FieldCount, MaxColumns);
                var texts = new string?[count];
                double[]? numbers = null;
                for (int c = 0; c < count; c++)
                {
                    object? value = reader.GetValue(c);
                    if (value is null)
                    {
                        if (reader.GetCellError(c) is { } error) texts[c] = ErrorText(error);
                        continue;
                    }
                    double number = double.NaN;
                    switch (value)
                    {
                        case string s:
                            texts[c] = s.Length == 0 ? null : s;
                            break;
                        case bool b:
                            texts[c] = BoolText(b, culture);
                            break;
                        case DateTime date:
                            number = date.ToOADate();
                            // Excel's built-in short date (14) and date with time (22) follow the owner's
                            // regional settings rather than their literal "m/d/yyyy".
                            texts[c] = reader.GetNumberFormatIndex(c) switch
                            {
                                14 => date.ToString("d", culture),
                                22 => date.ToString("g", culture),
                                _ => Format(value, reader.GetNumberFormatString(c), formats, culture)
                                     ?? date.ToString(date.TimeOfDay == TimeSpan.Zero ? "d" : "g", culture),
                            };
                            break;
                        case TimeSpan time:
                            number = time.TotalDays;
                            texts[c] = Format(value, reader.GetNumberFormatString(c), formats, culture) ?? time.ToString("c", culture);
                            break;
                        case double d:
                            number = d;
                            texts[c] = Format(d, reader.GetNumberFormatString(c), formats, culture) ?? d.ToString("G15", culture);
                            break;
                        default:
                            if (value is IConvertible convertible && value is int or long or float or decimal or short or byte)
                            {
                                number = convertible.ToDouble(CultureInfo.InvariantCulture);
                                texts[c] = Format(number, reader.GetNumberFormatString(c), formats, culture)
                                           ?? number.ToString("G15", culture);
                            }
                            else texts[c] = Convert.ToString(value, culture);
                            break;
                    }
                    if (!double.IsNaN(number))
                    {
                        numbers ??= CreateNaN(count);
                        numbers[c] = number;
                    }
                }
                sheet.AddRow(texts, numbers);
                if ((++total & 0x3FF) == 0) progress?.Report(total);
            }

            if (reader.MergeCells is { } merges)
            {
                foreach (var m in merges)
                {
                    if (m.ToRow > m.FromRow || m.ToColumn > m.FromColumn)
                        sheet.AddMerge(new CellRange(m.FromRow, m.FromColumn, m.ToRow, m.ToColumn));
                }
            }

            var widths = new double?[fields];
            for (int c = 0; c < fields; c++)
            {
                try { widths[c] = reader.GetColumnWidth(c) is var w && w > 0 ? w : null; }
                catch (Exception ex) when (ex is ArgumentException or IndexOutOfRangeException or InvalidOperationException) { }
            }
            sheet.SetWidthHints(widths);
            sheet.TrimEnd();
            sheets.Add(sheet);
        }
        while (reader.NextResult());

        progress?.Report(total);
        if (styles is not null)
        {
            var found = styles.GetAwaiter().GetResult();
            foreach (var sheet in sheets.OfType<MemorySheet>())
            {
                if (found.TryGetValue(sheet.Name, out var looks)) sheet.Formats = looks;
            }
        }
        return new Workbook(path, TableFormat.Excel, sheets);
    }

    /// <summary>Formatting of every sheet; a damaged styles part only leaves the table plain.</summary>
    private static Dictionary<string, SheetFormats> ReadStyles(string path, CancellationToken cancel)
    {
        try
        {
            return XlsxStyles.Read(path, null, cancel);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or System.Xml.XmlException or UnauthorizedAccessException
                                   or FormatException or OverflowException)
        {
            return [];
        }
    }

    private static double[] CreateNaN(int count)
    {
        var numbers = new double[count];
        Array.Fill(numbers, double.NaN);
        return numbers;
    }

    /// <summary>The value in the cell's own number format; null when the format is unknown or broken.</summary>
    private static string? Format(object value, string? format, Dictionary<string, NumberFormat?> cache, CultureInfo culture)
    {
        if (string.IsNullOrEmpty(format)) return null;
        if (!cache.TryGetValue(format, out var parsed))
        {
            try
            {
                parsed = new NumberFormat(format);
                if (!parsed.IsValid) parsed = null;
            }
            catch (Exception ex) when (ex is ArgumentException or FormatException or InvalidOperationException)
            {
                parsed = null;
            }
            cache[format] = parsed;
        }
        if (parsed is null) return null;
        try
        {
            string text = parsed.Format(value, culture);
            return text.Length == 0 && value is not string ? null : text;
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or InvalidCastException or OverflowException)
        {
            return null;
        }
    }

    private static string BoolText(bool value, CultureInfo culture) =>
        culture.TwoLetterISOLanguageName == "ru" ? (value ? "ИСТИНА" : "ЛОЖЬ") : (value ? "TRUE" : "FALSE");

    private static string ErrorText(CellError error) => error switch
    {
        CellError.DIV0 => "#DIV/0!",
        CellError.NA => "#N/A",
        CellError.NAME => "#NAME?",
        CellError.NULL => "#NULL!",
        CellError.NUM => "#NUM!",
        CellError.REF => "#REF!",
        CellError.VALUE => "#VALUE!",
        _ => "#ERROR",
    };
}
