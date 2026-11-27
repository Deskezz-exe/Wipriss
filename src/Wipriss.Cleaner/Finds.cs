using System.IO;

namespace Wipriss.Cleaner;

/// <summary>Building blocks of the known places: measure some paths and describe them.</summary>
internal static class Finds
{
    /// <summary>An item made of <paramref name="paths"/>; null when none of them exists.</summary>
    public static CleanItem? Item(string title, CleanColor color, string what, string ifDeleted,
        IEnumerable<string> paths, string? howTo = null, string? confidence = null, bool keepEmpty = false)
    {
        var list = paths.Where(p => !string.IsNullOrEmpty(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var stats = FolderStats.MeasureAll(list);
        if (!stats.Exists) return null;
        if (!keepEmpty && stats.Size == 0 && !stats.Denied) return null;
        var existing = list.Where(p => File.Exists(p) || Directory.Exists(p)).ToList();
        var item = new CleanItem
        {
            Title = title,
            Paths = existing,
            Size = stats.Denied && stats.Size == 0 ? -1 : stats.Size,
            Files = stats.Files,
            Color = color,
            What = what,
            IfDeleted = ifDeleted,
            HowToClean = howTo,
            Action = howTo is null ? CleanAction.Contents : CleanAction.WindowsOnly,
            LastChanged = stats.LastChanged,
            Denied = stats.Denied,
            Confidence = confidence ?? (color == CleanColor.Yellow ? "средняя" : "высокая"),
        };
        if (stats.Denied)
            item.Evidence.Add("Часть файлов видна только с правами администратора: размер может быть больше");
        return item;
    }

    public static CleanItem? Item(string title, CleanColor color, string what, string ifDeleted, string path,
        string? howTo = null) => Item(title, color, what, ifDeleted, [path], howTo);

    /// <summary>Sub-folders of <paramref name="folder"/> (none if it does not exist), links skipped.</summary>
    public static IEnumerable<string> Children(string folder)
    {
        if (!Directory.Exists(folder)) return [];
        try
        {
            return new DirectoryInfo(folder).EnumerateDirectories("*", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint })
                .Select(d => d.FullName).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    /// <summary>Files matching a pattern in a folder (thumbnail caches).</summary>
    public static IEnumerable<string> FilesIn(string folder, string pattern)
    {
        if (!Directory.Exists(folder)) return [];
        try { return Directory.EnumerateFiles(folder, pattern, new EnumerationOptions { IgnoreInaccessible = true }).ToList(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    public static CleanGroup? Group(string id, string title, CleanCategory category, string status, string? publisher,
        IEnumerable<CleanItem?> items)
    {
        var list = items.OfType<CleanItem>().ToList();
        if (list.Count == 0) return null;
        return new CleanGroup { Id = id, Title = title, Category = category, Status = status, Publisher = publisher, Items = list };
    }

    public static string Installed(Inventory inventory, string name, bool female = false) =>
        inventory.IsInstalled(PathText.NameKey(name)) ? (female ? "установлена" : "установлен")
        : (female ? "не найдена среди установленных" : "не найден среди установленных");
}
