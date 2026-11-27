using System.Diagnostics;
using System.IO;

namespace Wipriss.Cleaner;

/// <summary>
/// Folders of programs that are gone. Every first-level folder of Program Files, ProgramData, AppData and
/// the root of the system drive is matched against the inventory (paths, names, publishers, the programs
/// inside). What nothing points to is a leftover; folders of the same name in different places become one
/// group («Foo»: its Program Files folder, its settings in Roaming, its cache in Local).
/// Used folders of Chromium/Electron programs are reported as their caches instead.
/// </summary>
internal static class Leftovers
{
    private sealed record Root(string Path, LeftoverPlace Place, string Label, string[] Skip);

    private static readonly string[] AppDataSkip =
    [
        "Microsoft", "Packages", "Temp", "Programs", "Comms", "ConnectedDevicesPlatform", "CrashDumps", "D3DSCache", "VirtualStore",
        "PeerDistRepub", "PlaceholderTileLogoFolder", "Publishers", "Application Data", "History", "Temporary Internet Files",
        "IsolatedStorage", "ElevatedDiagnostics", "Microsoft Help", "assembly", "Microsoft_Corporation", "Desktop", "Documents",
        "Start Menu", "Templates", "Favorites", "SquirrelTemp", "Wipriss", "Windows", "Identities",
    ];

    private static readonly string[] RootSkip =
    [
        "Windows", "Program Files", "Program Files (x86)", "ProgramData", "Users", "PerfLogs", "Recovery", "Documents and Settings",
        "System Volume Information", "Config.Msi", "OneDriveTemp", "Boot", "EFI", "Intel", "drivers", "Windows10Upgrade", "ESD",
        "inetpub", "Windows.old", "MSOCache", "Wipriss", "SteamLibrary", "Games", "Program Files", "Program Files (x86)",
    ];

    private static IEnumerable<Root> Roots() => SystemRoots().Concat(Places.OtherDrives.SelectMany(OtherDriveRoots));

    /// <summary>Program Files folders and the root of another drive.</summary>
    private static IEnumerable<Root> OtherDriveRoots(string drive)
    {
        var letter = drive.TrimEnd('\\');
        yield return new(Path.Combine(drive, "Program Files"), LeftoverPlace.ProgramFiles, $"Папка программы ({letter}\\Program Files)", []);
        yield return new(Path.Combine(drive, "Program Files (x86)"), LeftoverPlace.ProgramFiles, $"Папка программы ({letter}\\Program Files (x86))", []);
        yield return new(drive, LeftoverPlace.DriveRoot, $"Папка в корне диска {letter}", RootSkip); // «D:\», never «D:» (that is D's current folder)
    }

    private static IEnumerable<Root> SystemRoots() =>
    [
        new(Places.ProgramFiles, LeftoverPlace.ProgramFiles, "Папка программы (Program Files)", ["Windows NT", "Windows Mail", "Windows Media Player", "Windows Photo Viewer", "Windows Sidebar", "Windows Portable Devices", "Windows Security", "Internet Explorer", "Uninstall Information", "MSBuild", "dotnet", "Microsoft.NET", "PackageManagement", "Microsoft Office", "Microsoft Office 15", "Microsoft OneDrive"]),
        new(Places.ProgramFilesX86, LeftoverPlace.ProgramFiles, "Папка программы (Program Files (x86))", ["Windows NT", "Windows Mail", "Windows Media Player", "Windows Photo Viewer", "Windows Sidebar", "Windows Portable Devices", "Internet Explorer", "Uninstall Information", "MSBuild", "dotnet", "Microsoft.NET", "Microsoft SDKs", "InstallShield Installation Information", "Temp", "Google", "Microsoft Office"]),
        new(Places.ProgramData, LeftoverPlace.ProgramData, "Общие данные (ProgramData)", [.. AppDataSkip, "Windows App Certification Kit", "WindowsHolographicDevices", "Microsoft OneDrive", "Epic", "chocolatey", "Oracle"]),
        new(Places.Roaming, LeftoverPlace.Roaming, @"Настройки (AppData\Roaming)", AppDataSkip),
        new(Places.Local, LeftoverPlace.Local, @"Данные и кэш (AppData\Local)", AppDataSkip),
        new(Places.LocalLow, LeftoverPlace.LocalLow, @"Данные (AppData\LocalLow)", [.. AppDataSkip, "Sun", "Oracle"]),
        new(Path.Combine(Places.Local, "Programs"), LeftoverPlace.LocalPrograms, @"Папка программы (AppData\Local\Programs)", ["Common"]),
        new(Places.SystemDrive, LeftoverPlace.DriveRoot, $"Папка в корне диска {Places.SystemDrive.TrimEnd('\\')}", RootSkip),
    ];

    private sealed record Candidate(string Path, Root Root, string NameKey, bool UnderUsedVendor);

    public static IEnumerable<CleanGroup> Find(ScanContext context, Action<int> progress, CancellationToken cancel)
    {
        var inventory = context.Inventory;
        var candidates = new List<Candidate>();
        var electron = new List<(string Path, InstalledProgram? Program)>();

        foreach (var root in Roots())
        {
            foreach (var folder in Finds.Children(root.Path))
            {
                var name = Path.GetFileName(folder);
                if (Skip(name, root) || Verdict.IsProtected(folder) || context.IsClaimed(folder)) continue;
                var key = PathText.NameKey(name);

                if (!IsUsed(folder, key, inventory, out var program))
                {
                    candidates.Add(new Candidate(folder, root, key, false));
                    continue;
                }
                if (root.Place is LeftoverPlace.Roaming or LeftoverPlace.Local && Apps.LooksLikeElectron(folder))
                {
                    electron.Add((folder, program));
                    continue;
                }

                // A vendor folder in use: old versions or removed products of the same vendor may still sit inside.
                if (root.Place is LeftoverPlace.DriveRoot or LeftoverPlace.LocalPrograms) continue;
                var children = Finds.Children(folder).ToList();
                if (children.Count is 0 or > 40) continue;
                foreach (var child in children)
                {
                    if (Skip(Path.GetFileName(child), root) || Verdict.IsProtected(child) || context.IsClaimed(child)) continue;
                    var childKey = PathText.NameKey(Path.GetFileName(child));
                    if (childKey.Length < 3 || inventory.UsedIn(child) is not null) continue;
                    if (inventory.Programs.Any(p => PathText.NamesMatch(childKey, p.NameKey))) continue;
                    if (root.Place is LeftoverPlace.Roaming or LeftoverPlace.Local && Apps.LooksLikeElectron(child))
                    {
                        electron.Add((child, program));
                        continue;
                    }
                    // Only folders that look like a product of their own (a version or a product name), not
                    // the vendor's shared data such as «Common», «Shared», «Settings».
                    if (!LooksLikeProduct(Path.GetFileName(child))) continue;
                    candidates.Add(new Candidate(child, root, childKey, true));
                }
            }
        }

        foreach (var (path, program) in electron)
        {
            if (cancel.IsCancellationRequested) yield break;
            context.Claim(path);
            var title = program?.Name ?? Path.GetFileName(path);
            var group = Apps.Electron(path, title, program?.Publisher, "установлена");
            if (group is not null) yield return group;
        }

        // Measure (in parallel: this is the slow part) and colour.
        var measured = new (Candidate Candidate, CleanItem? Item, string? Publisher)[candidates.Count];
        int done = 0;
        Parallel.For(0, candidates.Count, new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 2, 2, 4), CancellationToken = cancel },
            i =>
            {
                measured[i] = Measure(candidates[i], inventory);
                progress(Interlocked.Increment(ref done) * 100 / Math.Max(1, candidates.Count));
            });

        foreach (var byName in measured.Where(m => m.Item is not null).GroupBy(m => m.Candidate.NameKey.Length > 0 ? m.Candidate.NameKey : m.Candidate.Path.ToLowerInvariant()))
        {
            var first = byName.OrderBy(m => m.Candidate.Root.Place).First();
            yield return new CleanGroup
            {
                Id = "leftover:" + byName.Key,
                Title = Path.GetFileName(first.Candidate.Path),
                Category = CleanCategory.Leftovers,
                Status = byName.Any(m => m.Candidate.UnderUsedVendor)
                    ? $"нет среди установленных · издатель: {Path.GetFileName(Path.GetDirectoryName(first.Candidate.Path))}"
                    : "не установлена",
                Publisher = byName.Select(m => m.Publisher).FirstOrDefault(p => !string.IsNullOrWhiteSpace(p)),
                Items = byName.Select(m => m.Item!).ToList(),
            };
        }
    }

    private static bool Skip(string name, Root root) =>
        name.StartsWith('.') || name.StartsWith('$') || name.StartsWith("{", StringComparison.Ordinal)
        || name.StartsWith("Windows", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase)
        || name.Equals("SoftwareDistribution", StringComparison.OrdinalIgnoreCase) || name.Equals("Package Cache", StringComparison.OrdinalIgnoreCase)
        || root.Skip.Contains(name, StringComparer.OrdinalIgnoreCase);

    /// <summary>A proof that the folder is used: a path from the inventory, or the name of an installed
    /// program or its publisher.</summary>
    internal static bool IsUsed(string folder, string key, Inventory inventory, out InstalledProgram? program)
    {
        program = inventory.FindByName(key);
        if (program is not null) return true;
        return inventory.UsedIn(folder) is not null;
    }

    /// <summary>A product of its own («Tobii Interaction», «PyCharm2021.3»), not the vendor's shared data
    /// («plugins», «Statistics», «ComputeCache», «BT_backup»).</summary>
    internal static bool LooksLikeProduct(string name)
    {
        if (SharedNames.Contains(name, StringComparer.OrdinalIgnoreCase)) return false;
        foreach (var word in SharedWords)
            if (name.Contains(word, StringComparison.OrdinalIgnoreCase)) return false;
        return name.Any(char.IsDigit) || name.Length >= 6 && char.IsUpper(name[0]);
    }

    private static readonly string[] SharedWords = ["cache", "log", "temp", "backup", "update", "crash", "plugin", "theme", "setting", "config"];

    private static readonly string[] SharedNames =
        ["Common", "Shared", "Data", "Profiles", "Installer", "Licenses", "License", "Statistics", "Extensions", "Addons", "Skins", "Locales", "Languages", "Fonts", "Presets", "UserData", "Sessions", "History", "Templates", "Scripts", "Styles", "Workspace", "Engines", "Resources", "Assets", "Downloads", "Telemetry", "Diagnostics"];

    private static (Candidate, CleanItem?, string?) Measure(Candidate candidate, Inventory inventory)
    {
        var stats = FolderStats.Measure(candidate.Path);
        if (!stats.Exists) return (candidate, null, null);
        // In a drive root most folders are the owner's own (photos, games, projects): only a folder with a
        // program inside and no documents, photos or video can be a leftover of a program.
        if (candidate.Root.Place == LeftoverPlace.DriveRoot && (stats.Programs.Count == 0 || stats.PersonalFiles > 0))
            return (candidate, null, null);

        // Last chance: a program inside that belongs to an installed product («Valve» → Steam).
        string? publisher = null;
        foreach (var exe in stats.Programs)
        {
            try
            {
                var info = FileVersionInfo.GetVersionInfo(exe);
                publisher ??= string.IsNullOrWhiteSpace(info.CompanyName) ? null : info.CompanyName.Trim();
                var product = PathText.NameKey(info.ProductName);
                if (product.Length >= 4 && inventory.Programs.Any(p => PathText.NamesMatch(product, p.NameKey)))
                    return (candidate, null, null);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
        }

        var facts = new LeftoverFacts(candidate.Root.Place, stats.LastChanged, stats.PersonalFiles, stats.LooksLikeSaves,
            stats.Programs, stats.Denied, stats.Size, stats.Files, DateTime.Now);
        var (color, confidence, evidence) = Verdict.Leftover(facts);
        if (candidate.UnderUsedVendor)
        {
            // Inside the folder of a vendor that is still installed: the data may be shared, never green.
            evidence.Add("Другие программы этого издателя установлены: папка может быть им нужна");
            if (color == CleanColor.Green) { color = CleanColor.Yellow; confidence = "средняя"; }
        }

        var place = candidate.Root.Place;
        var item = new CleanItem
        {
            Title = candidate.UnderUsedVendor ? $"{Path.GetFileName(candidate.Path)} — {candidate.Root.Label}" : candidate.Root.Label,
            Paths = [candidate.Path],
            Action = CleanAction.Whole,
            Size = stats.Denied && stats.Size == 0 ? -1 : stats.Size,
            Files = stats.Files,
            Color = color,
            Confidence = confidence,
            Evidence = evidence,
            LastChanged = stats.LastChanged,
            Denied = stats.Denied,
            What = place switch
            {
                LeftoverPlace.ProgramFiles or LeftoverPlace.LocalPrograms => "Папка, куда была установлена программа. Самой программы среди установленных нет.",
                LeftoverPlace.Roaming => "Настройки программы, которой больше нет на компьютере.",
                LeftoverPlace.Local => "Данные и кэш программы, которой больше нет на компьютере.",
                LeftoverPlace.LocalLow => "Данные программы или игры, которой больше нет на компьютере.",
                LeftoverPlace.ProgramData => "Общие для всех пользователей данные программы, которой больше нет.",
                _ => "Папка в корне диска, которую не создавала Windows и не использует ни одна установленная программа.",
            },
            IfDeleted = color == CleanColor.Green
                ? "Программы нет, поэтому ничего не сломается. Если поставите её снова, она начнёт с чистого листа."
                : "Посмотрите, что внутри (кнопка «Показать в папке»): если ничего нужного нет, можно удалять.",
        };
        return (candidate, item, publisher);
    }
}
