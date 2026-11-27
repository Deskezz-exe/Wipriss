using System.IO;

namespace Wipriss.Cleaner;

/// <summary>Where a leftover folder lies: it changes what the folder usually holds.</summary>
internal enum LeftoverPlace { ProgramFiles, ProgramData, Roaming, Local, LocalLow, LocalPrograms, DriveRoot }

/// <summary>What was found inside a folder nothing uses.</summary>
internal sealed record LeftoverFacts(LeftoverPlace Place, DateTime? LastChanged, int PersonalFiles, bool LooksLikeSaves,
    IReadOnlyList<string> Programs, bool Denied, long Size, int Files, DateTime Now);

/// <summary>
/// The colour rules, without the file system (covered by tests). A leftover is green only when nothing
/// points to it, it has not changed for 30 days, and it holds no documents, photos, video or saves.
/// The protected list is checked before anything else and can never be green or yellow.
/// </summary>
internal static class Verdict
{
    public const int OldDays = 30;

    public static (CleanColor Color, string Confidence, List<string> Evidence) Leftover(LeftoverFacts facts)
    {
        var evidence = new List<string>
        {
            "Нет среди установленных программ и приложений Store",
            "Ни служба, ни задача планировщика, ни автозагрузка, ни ярлык не ведут сюда; папки нет в PATH",
        };
        var yellow = new List<string>();

        int? days = facts.LastChanged is { } changed ? (int)Math.Max(0, (facts.Now - changed).TotalDays) : null;
        if (days is { } d)
        {
            var line = $"Последнее изменение: {facts.LastChanged:dd.MM.yyyy} ({d} дн. назад)";
            if (d < OldDays) yellow.Add(line + ": недавно менялась, возможно, ей ещё пользуются");
            else evidence.Add(line);
        }

        if (facts.Denied) yellow.Add("Внутрь заглянуть не удалось: нет доступа");
        if (facts.PersonalFiles > 0) yellow.Add($"Внутри документы, фото или видео: {facts.PersonalFiles} шт.");
        else if (!facts.Denied && facts.Files > 0) evidence.Add("Внутри нет документов, фото и видео");
        if (facts.LooksLikeSaves) yellow.Add("Похоже на сохранения игры");

        if (facts.Programs.Count > 0 && facts.Place is not (LeftoverPlace.ProgramFiles or LeftoverPlace.LocalPrograms))
            yellow.Add($"Внутри есть программа ({string.Join(", ", facts.Programs.Take(3).Select(Path.GetFileName))}): возможно, она работает без установки");
        else if (facts.Programs.Count > 0)
            evidence.Add($"Внутри программа ({string.Join(", ", facts.Programs.Take(3).Select(Path.GetFileName))}), но Windows о ней не знает");

        switch (facts.Place)
        {
            case LeftoverPlace.DriveRoot:
                yellow.Add("Папка в корне диска: часто это ваши собственные файлы");
                break;
            case LeftoverPlace.LocalLow:
                yellow.Add("В LocalLow игры часто хранят сохранения");
                break;
            case LeftoverPlace.ProgramData when facts.Files > 0:
                yellow.Add("Сюда пишут и драйверы, о которых список программ не знает");
                break;
        }

        if (facts.Files == 0 && !facts.Denied) evidence.Add("Папка пустая");

        if (yellow.Count == 0)
            return (CleanColor.Green, "высокая", evidence);
        evidence.AddRange(yellow);
        return (CleanColor.Yellow, yellow.Count > 1 ? "низкая" : "средняя", evidence);
    }

    /// <summary>
    /// Folders that are never offered as leftovers, whatever the inventory says: Windows, its stores and
    /// caches, the common program folders. Checked by path, so a renamed parent cannot hide them.
    /// </summary>
    public static bool IsProtected(string path)
    {
        var normal = PathText.Normalize(path);
        if (normal is null) return true;
        foreach (var root in ProtectedRoots)
            if (PathText.IsInside(normal, root)) return true;
        return normal.Length <= 3; // a drive root
    }

    private static readonly string[] ProtectedRoots = BuildProtected();

    private static string[] BuildProtected()
    {
        var win = Places.Windows;
        string[] list =
        [
            win,
            Path.Combine(Places.ProgramData, "Microsoft"),
            Path.Combine(Places.ProgramData, "Package Cache"),
            Path.Combine(Places.ProgramData, "Packages"),
            Path.Combine(Places.ProgramData, "regid.1991-06.com.microsoft"),
            Path.Combine(Places.ProgramData, "USOShared"),
            Path.Combine(Places.ProgramData, "USOPrivate"),
            Path.Combine(Places.ProgramData, "ssh"),
            Path.Combine(Places.ProgramFiles, "WindowsApps"),
            Path.Combine(Places.ProgramFiles, "ModifiableWindowsApps"),
            Path.Combine(Places.ProgramFiles, "Common Files"),
            Path.Combine(Places.ProgramFilesX86, "Common Files"),
            Path.Combine(Places.ProgramFiles, "Windows Defender"),
            Path.Combine(Places.ProgramFiles, "Windows Defender Advanced Threat Protection"),
            Path.Combine(Places.ProgramFiles, "WindowsPowerShell"),
            Path.Combine(Places.ProgramFilesX86, "WindowsPowerShell"),
            Path.Combine(Places.ProgramFiles, "Microsoft Update Health Tools"),
            Path.Combine(Places.ProgramFilesX86, "Microsoft"),
            Path.Combine(Places.ProgramFilesX86, "Windows Kits"),
            Path.Combine(Places.ProgramFilesX86, "Reference Assemblies"),
            Path.Combine(Places.ProgramFiles, "Reference Assemblies"),
            Path.Combine(Places.Local, "Microsoft"),
            Path.Combine(Places.Local, "Packages"),
            Path.Combine(Places.Local, "Package Cache"),
            Path.Combine(Places.Roaming, "Microsoft"),
            Path.Combine(Places.SystemDrive, "Recovery"),
            Path.Combine(Places.SystemDrive, "System Volume Information"),
            Path.Combine(Places.SystemDrive, "$Recycle.Bin"),
            Path.Combine(Places.SystemDrive, "Boot"),
            Path.Combine(Places.SystemDrive, "EFI"),
        ];
        return list.Select(PathText.Normalize).OfType<string>().ToArray();
    }
}
