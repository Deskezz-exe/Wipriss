using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;

namespace Wipriss.Reader.Data;

/// <summary>
/// Saves the owner's changes into the file itself, without anything showing up next to it:
/// <list type="number">
/// <item>the new version is built in full in Wipriss's own folder inside the system temporary folder;</item>
/// <item>a safety copy of the file as it is now is put there too;</item>
/// <item>the new content is written into the same file (same name, same place on the desktop, same creation time);</item>
/// <item>if that write fails half way, the file is written back from the safety copy.</item>
/// </list>
/// Both temporary files are Wipriss's own and are removed afterwards.
/// </summary>
internal static class TableSaver
{
    /// <summary>True when another program (Excel) holds the file so that it can't be written.</summary>
    public static bool IsBusy(string path)
    {
        try
        {
            using var probe = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read | FileShare.Delete);
            return false;
        }
        catch (IOException ex) when ((ex.HResult & 0xFFFF) is 32 or 33)
        {
            return true;
        }
    }

    /// <summary>Writes the workbook with its changes to <paramref name="target"/> (the file itself or a new file).</summary>
    public static void Save(Workbook book, string target, CultureInfo culture, CancellationToken cancel) =>
        SaveWith(target, work => WriteTo(book, work, culture, cancel), cancel);

    /// <summary>
    /// Builds the new content through <paramref name="write"/> (into a file in the temporary folder) and then puts it
    /// into <paramref name="target"/>: an existing file is overwritten in place, a new one is moved there.
    /// </summary>
    public static void SaveWith(string target, Action<string> write, CancellationToken cancel)
    {
        target = Path.GetFullPath(target);
        string work = Path.Combine(Path.GetTempPath(), "Wipriss", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        string fresh = Path.Combine(work, "new" + Path.GetExtension(target));
        bool keepWork = false;
        try
        {
            write(fresh);
            cancel.ThrowIfCancellationRequested();
            if (File.Exists(target))
            {
                keepWork = !OverwriteInPlace(fresh, target, Path.Combine(work, "old" + Path.GetExtension(target)), out var problem);
                if (problem is not null) throw problem;
                Notify(ShcneUpdateItem, target);
            }
            else
            {
                File.Move(fresh, target, overwrite: false);
                Notify(ShcneCreate, target);
            }
        }
        finally
        {
            // Only Wipriss's own folder in the temporary folder. It stays only if the safety copy is still needed.
            if (!keepWork)
            {
                try { Directory.Delete(work, recursive: true); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    /// <summary>
    /// Writes <paramref name="fresh"/> into <paramref name="target"/>. Returns false only when even writing the old
    /// content back failed: then the safety copy must stay where it is (the problem says where).
    /// </summary>
    private static bool OverwriteInPlace(string fresh, string target, string backup, out Exception? problem)
    {
        problem = null;
        using (var source = Workbook.OpenShared(target))
        using (var copy = new FileStream(backup, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16))
        {
            source.CopyTo(copy);
            copy.Flush(flushToDisk: true);
        }

        // Read sharing stays on: the table window itself may still read the old file.
        using var output = new FileStream(target, FileMode.Open, FileAccess.Write, FileShare.Read | FileShare.Delete, 1 << 16);
        try
        {
            using var input = new FileStream(fresh, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
            input.CopyTo(output);
            output.SetLength(input.Length);
            output.Flush(flushToDisk: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            problem = ex;
            try
            {
                using var old = new FileStream(backup, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
                output.Position = 0;
                old.CopyTo(output);
                output.SetLength(old.Length);
                output.Flush(flushToDisk: true);
                return true;
            }
            catch (Exception again) when (again is IOException or UnauthorizedAccessException)
            {
                problem = new IOException($"Файл не записался до конца. Его прежняя версия сохранена здесь: {backup}", ex);
                return false;
            }
        }
    }

    private const int ShcneCreate = 0x2, ShcneUpdateItem = 0x2000;

    /// <summary>Tells Explorer the file changed, so the desktop and open folders update at once.</summary>
    private static void Notify(int change, string path)
    {
        try { SHChangeNotify(change, 0x0005 /* SHCNF_PATHW */, path, 0); }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern void SHChangeNotify(int eventId, uint flags, string item1, nint item2);

    private static void WriteTo(Workbook book, string path, CultureInfo culture, CancellationToken cancel)
    {
        var sheets = book.Sheets.OfType<EditedSheet>().ToList();
        if (book.Format == TableFormat.Csv)
        {
            if (sheets.Count != 1 || book.Csv is not { } csv) throw new InvalidDataException("Таблицу нельзя сохранить.");
            using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16);
            CsvWriter.Write(sheets[0], csv, book.CsvDataStart, book.Path, output, cancel);
            output.Flush(flushToDisk: true);
            return;
        }
        using (var source = Workbook.OpenShared(book.Path))
        using (var copy = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16))
            source.CopyTo(copy);
        XlsxWriter.Write(book.Path, path, sheets, culture, cancel);
    }
}
