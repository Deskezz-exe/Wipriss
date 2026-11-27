using System.IO;
using System.IO.Enumeration;

namespace Wipriss.Cleaner;

/// <summary>
/// What is inside a folder: size, number of files, the newest change, personal files (documents, photos,
/// video), things that look like game saves, and a few programs. Symbolic links and junctions are never
/// followed (they are skipped entirely), so a link cannot pull another folder into the count.
/// </summary>
internal sealed class FolderStats
{
    public long Size;
    public int Files;
    public DateTime? LastChanged;
    public int PersonalFiles;
    public bool LooksLikeSaves;
    public bool Denied;
    public bool Exists;
    /// <summary>Up to 5 programs (.exe) near the top, full paths.</summary>
    public readonly List<string> Programs = [];

    private static readonly HashSet<string> PersonalExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".doc", ".docx", ".odt", ".rtf", ".pdf", ".xls", ".xlsx", ".ods", ".ppt", ".pptx", ".odp",
        ".jpg", ".jpeg", ".png", ".heic", ".raw", ".cr2", ".nef", ".dng", ".psd", ".kra",
        ".mp4", ".mov", ".mkv", ".avi", ".m4v", ".mp3", ".flac", ".wav", ".m4a",
        ".kdbx", ".blend", ".fbx",
    };

    private static readonly HashSet<string> SaveExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".sav", ".save", ".savegame", ".sl2", ".ess", ".fos",
    };

    /// <summary>Caches hold thousands of thumbnails; looking for personal files there means nothing.</summary>
    public static FolderStats Measure(string path, bool lookForPersonal = true)
    {
        var stats = new FolderStats();
        if (File.Exists(path))
        {
            try
            {
                var info = new FileInfo(path);
                if ((info.Attributes & FileAttributes.ReparsePoint) != 0) return stats;
                stats.Exists = true;
                stats.Size = info.Length;
                stats.Files = 1;
                stats.LastChanged = info.LastWriteTime;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { stats.Exists = true; stats.Denied = true; }
            return stats;
        }
        if (!Directory.Exists(path)) return stats;
        stats.Exists = true;

        try
        {
            var root = new DirectoryInfo(path);
            if ((root.Attributes & FileAttributes.ReparsePoint) != 0) { stats.Exists = false; return stats; }
            stats.LastChanged = root.LastWriteTime;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { stats.Denied = true; return stats; }

        // Can we look inside at all? Without administrator rights some system folders are closed.
        try { using var probe = Directory.EnumerateFileSystemEntries(path).GetEnumerator(); probe.MoveNext(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { stats.Denied = true; return stats; }

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
            ReturnSpecialDirectories = false,
        };
        var entries = new FileSystemEnumerable<(long Size, DateTime Changed, bool IsDir, string Name, int Depth, string Dir)>(path,
            (ref FileSystemEntry e) => (e.IsDirectory ? 0 : e.Length, e.LastWriteTimeUtc.UtcDateTime, e.IsDirectory,
                e.FileName.ToString(), CountDepth(e.Directory.Length, path.Length, e.Directory),
                e.IsDirectory || !e.FileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? "" : e.Directory.ToString()),
            options);

        DateTime newest = stats.LastChanged?.ToUniversalTime() ?? DateTime.MinValue;
        try
        {
            foreach (var (size, changed, isDir, name, depth, dir) in entries)
            {
                if (changed > newest && changed.Year > 1990) newest = changed;
                if (isDir)
                {
                    if (lookForPersonal && LooksLikeSaveFolder(name)) stats.LooksLikeSaves = true;
                    continue;
                }
                stats.Size += size;
                stats.Files++;
                if (!lookForPersonal) continue;
                var ext = Path.GetExtension(name);
                if (PersonalExtensions.Contains(ext)) stats.PersonalFiles++;
                else if (SaveExtensions.Contains(ext)) stats.LooksLikeSaves = true;
                else if (depth <= 2 && stats.Programs.Count < 5 && ext.Equals(".exe", StringComparison.OrdinalIgnoreCase)
                         && !IsHelperExe(name))
                    stats.Programs.Add(Path.Combine(dir, name));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { stats.Denied = true; }
        if (newest > DateTime.MinValue) stats.LastChanged = newest.ToLocalTime();
        return stats;
    }

    /// <summary>Several paths as one item (a cache split over profiles).</summary>
    public static FolderStats MeasureAll(IEnumerable<string> paths, bool lookForPersonal = false)
    {
        var total = new FolderStats();
        foreach (var path in paths)
        {
            var one = Measure(path, lookForPersonal);
            if (!one.Exists) continue;
            total.Exists = true;
            total.Size += one.Size;
            total.Files += one.Files;
            total.PersonalFiles += one.PersonalFiles;
            total.LooksLikeSaves |= one.LooksLikeSaves;
            total.Denied |= one.Denied;
            if (one.LastChanged > (total.LastChanged ?? DateTime.MinValue)) total.LastChanged = one.LastChanged;
        }
        return total;
    }

    internal static bool LooksLikeSaveFolder(string name) =>
        name.Equals("saves", StringComparison.OrdinalIgnoreCase)
        || name.Equals("save", StringComparison.OrdinalIgnoreCase)
        || name.Equals("savegames", StringComparison.OrdinalIgnoreCase)
        || name.Equals("saved games", StringComparison.OrdinalIgnoreCase)
        || name.Equals("savedata", StringComparison.OrdinalIgnoreCase);

    private static bool IsHelperExe(string name) =>
        name.StartsWith("unins", StringComparison.OrdinalIgnoreCase)
        || name.Contains("crash", StringComparison.OrdinalIgnoreCase)
        || name.Contains("update", StringComparison.OrdinalIgnoreCase);

    private static int CountDepth(int dirLength, int rootLength, ReadOnlySpan<char> dir)
    {
        if (dirLength <= rootLength) return 1;
        int depth = 1;
        foreach (var c in dir[rootLength..]) if (c == '\\') depth++;
        return depth;
    }
}
