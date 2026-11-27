using System.IO;
using Microsoft.Win32;

namespace Wipriss.Cleaner;

/// <summary>
/// Programs with their own caches: a few known ones (Telegram, Spotify, Steam...) and any program built
/// on Chromium/Electron (Discord, VS Code, Slack, Teams...), recognized by its cache folders.
/// </summary>
internal static class Apps
{
    private static readonly string[] ElectronCaches =
    [
        "Cache", "Code Cache", "GPUCache", "DawnCache", "DawnGraphiteCache", "DawnWebGPUCache", "ShaderCache", "GrShaderCache",
        @"Service Worker\CacheStorage", @"Service Worker\ScriptCache", "CachedData", "CachedExtensionVSIXs", "CachedExtensions",
        "CachedProfilesData", "blob_storage",
    ];

    /// <summary>Is this the data folder of a Chromium/Electron program?</summary>
    public static bool LooksLikeElectron(string folder) =>
        Directory.Exists(Path.Combine(folder, "Code Cache")) || Directory.Exists(Path.Combine(folder, "GPUCache"))
        || (Directory.Exists(Path.Combine(folder, "Cache")) && Directory.Exists(Path.Combine(folder, "Local Storage")));

    /// <summary>The caches, logs and crash reports of a Chromium/Electron program; its settings and logins are
    /// not touched (they are not listed at all).</summary>
    public static CleanGroup? Electron(string folder, string title, string? publisher, string status)
    {
        var items = new List<CleanItem?>
        {
            Finds.Item("Кэш", CleanColor.Green,
                $"Копии картинок, скриптов и скомпилированный код, чтобы {title} запускался быстрее.",
                "Первый запуск будет чуть медленнее, кэш создастся заново. Входы и настройки останутся. Перед удалением закройте программу.",
                ElectronCaches.Select(n => Path.Combine(folder, n))),
            Finds.Item("Журналы", CleanColor.Green,
                "Записи о работе программы, нужны только для разбора ошибок.",
                "Ничего не изменится.",
                [Path.Combine(folder, "logs"), Path.Combine(folder, "Logs")]),
            Finds.Item("Отчёты о сбоях", CleanColor.Green,
                "Отчёты, которые программа собирает, когда падает.",
                "Ничего не изменится.",
                [Path.Combine(folder, @"Crashpad\reports"), Path.Combine(folder, @"Crashpad\attachments"), Path.Combine(folder, "crashes")]),
        };
        return Finds.Group("app:" + PathText.NameKey(folder), title, CleanCategory.Apps, status, publisher, items);
    }

    public static IEnumerable<CleanGroup> Known(ScanContext context)
    {
        var telegramRoot = Path.Combine(Places.Roaming, "Telegram Desktop");
        if (Directory.Exists(telegramRoot))
        {
            context.Claim(telegramRoot);
            var group = Finds.Group("app:telegram", "Telegram", CleanCategory.Apps, Finds.Installed(context.Inventory, "Telegram"), "Telegram",
            [
                Finds.Item("Медиа из чатов", CleanColor.Yellow,
                    "Фото, видео, голосовые и стикеры, которые Telegram сохранил, пока вы смотрели чаты.",
                    "Всё останется в Telegram, но при просмотре скачается заново. Сохранённые вами файлы в «Загрузках» не пострадают.",
                    [Path.Combine(telegramRoot, @"tdata\user_data"), Path.Combine(telegramRoot, @"tdata\user_data#2"), Path.Combine(telegramRoot, @"tdata\user_data#3")]),
                Finds.Item("Кэш эмодзи", CleanColor.Green, "Картинки эмодзи.", "Скачаются заново.", Path.Combine(telegramRoot, @"tdata\emoji")),
            ]);
            if (group is not null) yield return group;
        }

        var spotifyLocal = Path.Combine(Places.Local, "Spotify");
        if (Directory.Exists(spotifyLocal))
        {
            context.Claim(spotifyLocal);
            var group = Finds.Group("app:spotify", "Spotify", CleanCategory.Apps, Finds.Installed(context.Inventory, "Spotify"), "Spotify",
            [
                Finds.Item("Кэш музыки", CleanColor.Green, "Музыка, которую вы недавно слушали, чтобы она играла без задержки.",
                    "Песни начнут грузиться из интернета заново.", [Path.Combine(spotifyLocal, "Storage"), Path.Combine(spotifyLocal, "Browser")]),
                Finds.Item("Скачанное для офлайна", CleanColor.Yellow, "Плейлисты и альбомы, которые вы скачали для прослушивания без интернета.",
                    "Их придётся скачать заново.", Path.Combine(spotifyLocal, "Data")),
            ]);
            if (group is not null) yield return group;
        }

        var steam = Steam(context);
        if (steam is not null) yield return steam;

        var epicLocal = Path.Combine(Places.Local, "EpicGamesLauncher");
        if (Directory.Exists(epicLocal))
        {
            context.Claim(epicLocal);
            var saved = Path.Combine(epicLocal, "Saved");
            var group = Finds.Group("app:epic", "Epic Games Launcher", CleanCategory.Apps, Finds.Installed(context.Inventory, "Epic Games Launcher"), "Epic Games",
            [
                Finds.Item("Кэш", CleanColor.Green, "Кэш встроенного браузера магазина.", "Магазин создаст его заново.",
                    Finds.Children(saved).Where(p => Path.GetFileName(p).StartsWith("webcache", StringComparison.OrdinalIgnoreCase))),
                Finds.Item("Журналы и отчёты о сбоях", CleanColor.Green, "Записи о работе лаунчера.", "Ничего не изменится.",
                    [Path.Combine(saved, "Logs"), Path.Combine(saved, "Crashes")]),
            ]);
            if (group is not null) yield return group;
        }

        var adobeCache = Path.Combine(Places.Roaming, @"Adobe\Common");
        var adobe = Finds.Group("app:adobe-media", "Adobe (видео)", CleanCategory.Apps, "кэш Premiere Pro и After Effects", "Adobe",
        [
            Finds.Item("Медиакэш", CleanColor.Green,
                "Подготовленные копии звука и видео из ваших проектов, чтобы монтаж шёл плавно.",
                "Проекты не пострадают; при открытии Premiere заново подготовит файлы (это займёт время).",
                [Path.Combine(adobeCache, "Media Cache Files"), Path.Combine(adobeCache, "Media Cache"), Path.Combine(adobeCache, "Peak Files")]),
        ]);
        if (adobe is not null) yield return adobe;
    }

    private static CleanGroup? Steam(ScanContext context)
    {
        string? steamPath;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            steamPath = PathText.Normalize((key?.GetValue("SteamPath") as string)?.Replace('/', '\\'));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { steamPath = null; }
        var localSteam = Path.Combine(Places.Local, "Steam");
        if (steamPath is null && !Directory.Exists(localSteam)) return null;
        context.Claim(localSteam);

        var items = new List<CleanItem?>
        {
            Finds.Item("Кэш магазина", CleanColor.Green, "Страницы магазина и сообщества, картинки игр.", "Steam загрузит их заново.",
                [Path.Combine(localSteam, "htmlcache"), steamPath is null ? "" : Path.Combine(steamPath, @"appcache\httpcache")]),
        };
        if (steamPath is not null)
        {
            // Every library, also on other drives.
            var libraries = new List<string> { steamPath };
            var vdf = Path.Combine(steamPath, @"steamapps\libraryfolders.vdf");
            try
            {
                if (File.Exists(vdf))
                    foreach (var line in File.ReadLines(vdf))
                    {
                        var trimmed = line.Trim();
                        if (!trimmed.StartsWith("\"path\"", StringComparison.OrdinalIgnoreCase)) continue;
                        if (PathText.Normalize(trimmed[6..].Trim().Trim('"').Replace(@"\\", @"\")) is { } library
                            && !libraries.Contains(library, StringComparer.OrdinalIgnoreCase))
                            libraries.Add(library);
                    }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            items.Add(Finds.Item("Кэш шейдеров игр", CleanColor.Green, ShaderText, "Игры создадут его заново, первые минуты могут подтормаживать.",
                libraries.Select(l => Path.Combine(l, @"steamapps\shadercache"))));
            items.Add(Finds.Item("Журналы и отчёты о сбоях", CleanColor.Green, "Записи о работе Steam.", "Ничего не изменится.",
                [Path.Combine(steamPath, "logs"), Path.Combine(steamPath, "dumps")]));
            items.Add(Finds.Item("Недокачанные обновления", CleanColor.Yellow, "Части игр и обновлений, которые ещё качаются.",
                "Steam скачает их заново.", libraries.Select(l => Path.Combine(l, @"steamapps\downloading"))));
        }
        return Finds.Group("app:steam", "Steam", CleanCategory.Apps, Finds.Installed(context.Inventory, "Steam"), "Valve", items);
    }

    private const string ShaderText = "Готовые шейдеры игр, чтобы картинка не подтормаживала.";
}
