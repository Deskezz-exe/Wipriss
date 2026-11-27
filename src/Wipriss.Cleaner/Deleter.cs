using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Wipriss.Cleaner;

/// <summary>
/// Deletes items found by a fresh search. Rules that hold whatever the item says:
/// red and Windows-only items are never deleted; symbolic links and junctions are never followed or
/// deleted; the protected roots (Windows, the profile and its main folders, Program Files, Wipriss's own
/// folders) are never deleted or entered; inside «Contents» folders files changed in the last hour are left
/// alone (a program may be using them). Locked or closed files are counted and skipped.
/// </summary>
internal static class Deleter
{
    public static bool CanDelete(CleanItem item) => item.Color != CleanColor.Red && item.Action != CleanAction.WindowsOnly;

    private static readonly TimeSpan Fresh = TimeSpan.FromHours(1);

    public static (long Freed, int Failed) Delete(IReadOnlyList<CleanItem> items, Action<int> progress, CancellationToken cancel)
    {
        long freed = 0;
        int failed = 0;
        for (int i = 0; i < items.Count; i++)
        {
            cancel.ThrowIfCancellationRequested();
            var item = items[i];
            progress(i * 100 / Math.Max(1, items.Count));
            if (!CanDelete(item)) continue;

            if (item.Action == CleanAction.RecycleBin)
            {
                long before = Places.RecycleBinSize(out _);
                SHEmptyRecycleBin(IntPtr.Zero, null, NoConfirmation | NoProgressUi | NoSound);
                long after = Places.RecycleBinSize(out _);
                if (before > 0 && after >= 0) freed += Math.Max(0, before - after);
                continue;
            }

            bool whole = item.Action == CleanAction.Whole;
            foreach (var path in item.Paths)
            {
                var normal = PathText.Normalize(path);
                // The one root whose contents may go: the temporary folder (the folder itself stays).
                bool tempContents = !whole && normal is not null && string.Equals(normal, PathText.Normalize(Places.Temp), StringComparison.OrdinalIgnoreCase);
                if (normal is null || (IsForbidden(normal) && !tempContents)) { failed++; continue; }
                DeletePath(normal, whole, ref freed, ref failed, cancel);
            }
        }
        progress(100);
        return (freed, failed);
    }

    private static void DeletePath(string path, bool whole, ref long freed, ref int failed, CancellationToken cancel)
    {
        FileAttributes attributes;
        try { attributes = File.GetAttributes(path); }
        catch (FileNotFoundException) { return; }
        catch (DirectoryNotFoundException) { return; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failed++; return; }
        if ((attributes & FileAttributes.ReparsePoint) != 0) return; // never a link

        if ((attributes & FileAttributes.Directory) == 0)
        {
            DeleteFile(new FileInfo(path), ref freed, ref failed);
            return;
        }
        DeleteContents(new DirectoryInfo(path), skipFresh: !whole, ref freed, ref failed, cancel);
        if (whole) TryRemoveFolder(path);
    }

    private static void DeleteContents(DirectoryInfo folder, bool skipFresh, ref long freed, ref int failed, CancellationToken cancel)
    {
        List<FileSystemInfo> entries;
        try { entries = folder.EnumerateFileSystemInfos("*", new EnumerationOptions { IgnoreInaccessible = false, AttributesToSkip = 0 }).ToList(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failed++; return; }

        var freshSince = DateTime.UtcNow - Fresh;
        foreach (var entry in entries)
        {
            cancel.ThrowIfCancellationRequested();
            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) continue; // a link: neither followed nor deleted
            if (entry is DirectoryInfo dir)
            {
                if (IsForbidden(dir.FullName)) continue;
                DeleteContents(dir, skipFresh, ref freed, ref failed, cancel);
                TryRemoveFolder(dir.FullName);
            }
            else if (entry is FileInfo file)
            {
                if (skipFresh && file.LastWriteTimeUtc > freshSince) continue;
                DeleteFile(file, ref freed, ref failed);
            }
        }
    }

    private static void DeleteFile(FileInfo file, ref long freed, ref int failed)
    {
        try
        {
            long length = file.Length;
            if (file.IsReadOnly) file.IsReadOnly = false;
            file.Delete();
            freed += length;
        }
        catch (FileNotFoundException) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failed++; }
    }

    /// <summary>Removes a folder only if it is empty now (a locked file inside keeps it).</summary>
    private static void TryRemoveFolder(string path)
    {
        try { Directory.Delete(path, recursive: false); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>
    /// Never deleted or entered, whatever a rule says: drive roots, the main folders of Windows and of the
    /// profile, Program Files, Wipriss's own program and data folders, and Windows except its temporary,
    /// log and crash-dump folders.
    /// </summary>
    internal static bool IsForbidden(string path)
    {
        var normal = PathText.Normalize(path);
        if (normal is null || normal.Length <= 3) return true;
        if (ExactRoots.Contains(normal)) return true;
        foreach (var inside in ForbiddenInside)
            if (PathText.IsInside(normal, inside)) return true;
        if (PathText.IsInside(normal, Places.Windows))
            return !WindowsAllowed.Any(allowed => PathText.IsInside(normal, allowed));
        return false;
    }

    private static readonly HashSet<string> ExactRoots = BuildExact();

    private static HashSet<string> BuildExact()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Environment.SpecialFolder folder in Enum.GetValues<Environment.SpecialFolder>())
        {
            if (folder is Environment.SpecialFolder.InternetCache) continue;
            var path = PathText.Normalize(Environment.GetFolderPath(folder));
            if (path is not null) set.Add(path);
        }
        foreach (var extra in new[] { Places.Local, Places.Roaming, Places.LocalLow, Places.ProgramData, Places.Profile, Places.Downloads,
                                      Places.Temp, Path.GetDirectoryName(Places.Local) ?? "", Path.GetDirectoryName(Places.Profile) ?? "",
                                      Path.Combine(Places.Local, "Programs"), Path.Combine(Places.Profile, ".cache") })
            if (PathText.Normalize(extra) is { } path) set.Add(path);
        foreach (var drive in Places.OtherDrives)
            foreach (var name in new[] { "Program Files", "Program Files (x86)", "Users", "ProgramData", "Windows" })
                if (PathText.Normalize(Path.Combine(drive, name)) is { } path) set.Add(path);
        return set;
    }

    private static readonly string[] ForbiddenInside = BuildInside();

    private static string[] BuildInside()
    {
        var list = new List<string?>
        {
            PathText.Normalize(AppContext.BaseDirectory),
            PathText.Normalize(Path.Combine(Places.Profile, "Wipriss")),
            PathText.Normalize(Environment.GetFolderPath(Environment.SpecialFolder.System)),
            PathText.Normalize(Path.Combine(Places.Windows, "WinSxS")),
            PathText.Normalize(Path.Combine(Places.Windows, "Installer")),
            PathText.Normalize(Path.Combine(Places.ProgramData, "Package Cache")),
            PathText.Normalize(Path.Combine(Places.Local, "Packages")),
            PathText.Normalize(Path.Combine(Places.ProgramFiles, "WindowsApps")),
        };
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Wipriss");
            list.Add(PathText.Normalize(key?.GetValue("Folder") as string)); // the moved Wipriss folder
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        return list.OfType<string>().Where(p => p.Length > 3).ToArray();
    }

    private static readonly string[] WindowsAllowed =
        new[] { "Temp", "Minidump", "MEMORY.DMP", "LiveKernelReports", @"Logs\CBS", @"Logs\DISM", "Panther", @"SoftwareDistribution\Download" }
            .Select(p => Path.Combine(Places.Windows, p)).ToArray();

    private const uint NoConfirmation = 0x1, NoProgressUi = 0x2, NoSound = 0x4;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHEmptyRecycleBin(IntPtr window, string? root, uint flags);
}
