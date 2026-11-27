using System.IO;

namespace Wipriss.Cleaner;

/// <summary>Package caches of developer tools, and node_modules / venv inside the owner's projects.</summary>
internal static class Developer
{
    private const string Redownload = "Скачается заново, когда понадобится (нужен интернет, проекты при сборке подтянут своё сами).";

    public static IEnumerable<CleanGroup> Caches(ScanContext context)
    {
        var profile = Places.Profile;
        var cache = Path.Combine(profile, ".cache");
        CleanGroup? One(string id, string title, string publisher, CleanItem? item, params string[] claims)
        {
            foreach (var c in claims) context.Claim(c);
            return Finds.Group("dev:" + id, title, CleanCategory.Developer, DevStatus(context, title), publisher, [item]);
        }
        CleanItem? Packages(string what, params string[] paths) =>
            Finds.Item("Кэш пакетов", CleanColor.Green, what, Redownload, paths);

        context.Claim(Path.Combine(Places.Roaming, "NuGet")); // NuGet.Config: settings of dotnet, not a leftover
        var list = new List<CleanGroup?>
        {
            One("pip", "Python (pip)", "Python", Packages("Скачанные пакеты Python.", Path.Combine(Places.Local, @"pip\cache")), Path.Combine(Places.Local, "pip")),
            One("uv", "uv", "Astral", Packages("Скачанные пакеты Python для uv.", Path.Combine(Places.Local, @"uv\cache")), Path.Combine(Places.Local, "uv")),
            One("npm", "Node.js (npm)", "npm", Packages("Скачанные пакеты npm.", Path.Combine(Places.Local, "npm-cache")), Path.Combine(Places.Local, "npm-cache")),
            One("yarn", "Yarn", "Yarn", Packages("Скачанные пакеты Yarn.", Path.Combine(Places.Local, @"Yarn\Cache")), Path.Combine(Places.Local, "Yarn")),
            One("pnpm", "pnpm", "pnpm", Packages("Хранилище пакетов pnpm.", Path.Combine(Places.Local, @"pnpm\store"), Path.Combine(Places.Local, "pnpm-cache")),
                Path.Combine(Places.Local, "pnpm"), Path.Combine(Places.Local, "pnpm-cache")),
            One("nuget", "NuGet (.NET)", "Microsoft", Packages("Скачанные пакеты .NET.", Path.Combine(profile, @".nuget\packages"), Path.Combine(Places.Local, @"NuGet\v3-cache"),
                Path.Combine(Places.Local, @"NuGet\plugins-cache")), Path.Combine(Places.Local, "NuGet")),
            One("go", "Go", "Google", Finds.Item("Кэш сборки", CleanColor.Green, "Промежуточные файлы сборки Go.", "Следующая сборка будет дольше.",
                Path.Combine(Places.Local, "go-build")), Path.Combine(Places.Local, "go-build")),
            One("cargo", "Rust (cargo)", "Rust", Packages("Скачанные пакеты Rust.", Path.Combine(profile, @".cargo\registry\cache"), Path.Combine(profile, @".cargo\registry\src"))),
            One("gradle", "Gradle", "Gradle", Packages("Скачанные библиотеки Java/Android.", Path.Combine(profile, @".gradle\caches"))),
            One("electron", "Electron", "Electron", Packages("Скачанные сборки Electron.", Path.Combine(Places.Local, @"electron\Cache"), Path.Combine(Places.Local, "electron-builder")),
                Path.Combine(Places.Local, "electron"), Path.Combine(Places.Local, "electron-builder")),
            One("cypress", "Cypress", "Cypress", Packages("Скачанные версии Cypress.", Path.Combine(Places.Local, @"Cypress\Cache")), Path.Combine(Places.Local, "Cypress")),
            One("playwright", "Playwright", "Microsoft", Finds.Item("Браузеры для тестов", CleanColor.Yellow,
                "Chromium, Firefox и WebKit, которые скачал Playwright для автотестов.",
                "Скачаются заново командой «playwright install» (несколько сотен мегабайт, это долго).",
                Path.Combine(Places.Local, "ms-playwright")), Path.Combine(Places.Local, "ms-playwright")),
        };
        foreach (var group in list) if (group is not null) yield return group;

        // JetBrains: caches and logs of each IDE version.
        var jetbrains = Path.Combine(Places.Local, "JetBrains");
        if (Directory.Exists(jetbrains))
        {
            context.Claim(jetbrains);
            var versions = Finds.Children(jetbrains).ToList();
            var group = Finds.Group("dev:jetbrains", "JetBrains", CleanCategory.Developer, Finds.Installed(context.Inventory, "JetBrains"), "JetBrains",
            [
                Finds.Item("Кэш и индексы", CleanColor.Green, "Индексы проектов и кэш среды разработки.",
                    "Проекты при открытии проиндексируются заново (несколько минут).", versions.Select(v => Path.Combine(v, "caches"))),
                Finds.Item("Журналы", CleanColor.Green, "Записи о работе среды.", "Ничего не изменится.", versions.Select(v => Path.Combine(v, "log"))),
            ]);
            if (group is not null) yield return group;
        }

        // Docker and WSL: whole virtual disks, never deleted as files.
        var docker = Path.Combine(Places.Local, "Docker");
        if (Directory.Exists(docker))
        {
            context.Claim(docker);
            var group = Finds.Group("dev:docker", "Docker Desktop", CleanCategory.Developer, Finds.Installed(context.Inventory, "Docker Desktop"), "Docker",
            [
                Finds.Item("Диск с образами и контейнерами", CleanColor.Red,
                    "Все образы, контейнеры и тома Docker в одном виртуальном диске.",
                    "Пропадут все контейнеры и их данные. Удалять файлом нельзя.",
                    Path.Combine(docker, "wsl"),
                    "Средствами Docker: «docker system prune» или Docker Desktop → Troubleshoot → Clean / Purge data."),
            ]);
            if (group is not null) yield return group;
        }

        var wslDisks = Finds.Children(Path.Combine(Places.Local, "Packages"))
            .Where(p => Path.GetFileName(p).Contains("Canonical", StringComparison.OrdinalIgnoreCase)
                     || Path.GetFileName(p).Contains("Debian", StringComparison.OrdinalIgnoreCase)
                     || Path.GetFileName(p).Contains("Linux", StringComparison.OrdinalIgnoreCase))
            .Select(p => Path.Combine(p, @"LocalState\ext4.vhdx"))
            .Where(File.Exists).ToList();
        var wsl = Finds.Group("dev:wsl", "Linux (WSL)", CleanCategory.Developer, "установлен", "Microsoft",
        [
            Finds.Item("Диск Linux", CleanColor.Red,
                "Весь ваш Linux в одном файле: система, программы и ваши файлы внутри.",
                "Пропадёт всё, что было в Linux. Удалять файлом нельзя.",
                wslDisks,
                "Только средствами WSL: «wsl --unregister <имя>» удаляет дистрибутив целиком."),
        ]);
        if (wsl is not null) yield return wsl;
    }

    private static string DevStatus(ScanContext context, string title) =>
        context.Inventory.IsInstalled(PathText.NameKey(title)) ? "установлен" : "инструмент разработчика";

    /// <summary>node_modules and Python virtual environments in the owner's folders (AppData and hidden
    /// folders are skipped). One group per project.</summary>
    public static IEnumerable<CleanGroup> Projects(ScanContext context, CancellationToken cancel)
    {
        var found = new List<(string Project, string Path, bool Node)>();
        var roots = Finds.Children(Places.Profile).Concat(Places.OtherDrives.SelectMany(Finds.Children));
        foreach (var root in roots)
        {
            var name = Path.GetFileName(root);
            if (name.StartsWith('.') || name.StartsWith('$') || name.Equals("AppData", StringComparison.OrdinalIgnoreCase)
                || name.Equals("System Volume Information", StringComparison.OrdinalIgnoreCase)
                || name.Equals("SteamLibrary", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Windows", StringComparison.OrdinalIgnoreCase))
                continue;
            try { if ((File.GetAttributes(root) & (FileAttributes.System | FileAttributes.Hidden)) != 0) continue; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            Walk(root, 1, found, cancel);
        }

        foreach (var byProject in found.GroupBy(f => f.Project, StringComparer.OrdinalIgnoreCase))
        {
            var items = new List<CleanItem?>();
            foreach (var (_, path, node) in byProject)
            {
                var item = node
                    ? Finds.Item("node_modules", CleanColor.Yellow,
                        "Библиотеки JavaScript, которые проект скачал командой npm install.",
                        "Проект не запустится, пока вы не выполните «npm install» (скачает всё заново).", path)
                    : Finds.Item(Path.GetFileName(path) + " (Python)", CleanColor.Yellow,
                        "Виртуальное окружение Python с установленными библиотеками.",
                        "Проект не запустится, пока окружение не создать заново и не установить библиотеки.", path);
                if (item is null) continue;
                item.Confidence = "средняя";
                item.Action = CleanAction.Whole;
                var age = ProjectAge(byProject.Key);
                if (age is not null)
                {
                    var days = (int)(DateTime.Now - age.Value).TotalDays;
                    item.Evidence.Add(days > 90 ? $"В проекте ничего не меняли {days} дн.: возможно, он больше не нужен"
                                                : $"Проект менялся {days} дн. назад: похоже, вы им пользуетесь");
                }
                items.Add(item);
            }
            var group = Finds.Group("project:" + byProject.Key.ToLowerInvariant(), ShortName(byProject.Key), CleanCategory.Projects,
                byProject.Key, null, items);
            if (group is not null) yield return group;
        }
    }

    /// <summary>«...\Site\video» → «Site\video»: many projects are called «video».</summary>
    private static string ShortName(string project)
    {
        var parent = Path.GetFileName(Path.GetDirectoryName(project));
        var name = Path.GetFileName(project);
        return string.IsNullOrEmpty(parent) ? name : parent + "\\" + name;
    }

    private static void Walk(string folder, int depth, List<(string, string, bool)> found, CancellationToken cancel)
    {
        if (depth > 6 || cancel.IsCancellationRequested) return;
        foreach (var child in Finds.Children(folder))
        {
            var name = Path.GetFileName(child);
            if (name.Equals("node_modules", StringComparison.OrdinalIgnoreCase)) { found.Add((folder, child, true)); continue; }
            if (File.Exists(Path.Combine(child, "pyvenv.cfg"))) { found.Add((folder, child, false)); continue; }
            if (name.StartsWith('.') || name.StartsWith('$') || name.Equals("AppData", StringComparison.OrdinalIgnoreCase)
                || name.Equals("steamapps", StringComparison.OrdinalIgnoreCase)) continue;
            try { if ((File.GetAttributes(child) & (FileAttributes.System | FileAttributes.Hidden)) != 0) continue; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            Walk(child, depth + 1, found, cancel);
        }
    }

    /// <summary>The newest change among the project's own files (not inside node_modules / venv).</summary>
    private static DateTime? ProjectAge(string project)
    {
        try
        {
            DateTime newest = DateTime.MinValue;
            foreach (var entry in new DirectoryInfo(project).EnumerateFileSystemInfos())
            {
                if (entry.Name.Equals("node_modules", StringComparison.OrdinalIgnoreCase) || entry.Name.StartsWith('.')) continue;
                if (File.Exists(Path.Combine(entry.FullName, "pyvenv.cfg"))) continue;
                if (entry.LastWriteTime > newest) newest = entry.LastWriteTime;
            }
            return newest > DateTime.MinValue ? newest : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }
}
