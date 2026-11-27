using System.IO;

namespace Wipriss.Cleaner;

/// <summary>
/// Browsers, each with its kinds of data as separate items: cache (green), cookies, history, site data
/// and open tabs (yellow), passwords, autofill, bookmarks and extensions (red: only the browser itself
/// should delete them). Several profiles are summed into one item.
/// </summary>
internal static class Browsers
{
    private sealed record Chromium(string Id, string Title, string Publisher, string UserData, string? CacheRoot = null, bool SingleProfile = false, string? InstallName = null);

    private static IEnumerable<Chromium> ChromiumBrowsers() =>
    [
        new("chrome", "Google Chrome", "Google", Path.Combine(Places.Local, @"Google\Chrome\User Data")),
        new("edge", "Microsoft Edge", "Microsoft", Path.Combine(Places.Local, @"Microsoft\Edge\User Data")),
        new("yandex", "Яндекс Браузер", "Яндекс", Path.Combine(Places.Local, @"Yandex\YandexBrowser\User Data"), InstallName: "Yandex"),
        new("brave", "Brave", "Brave Software", Path.Combine(Places.Local, @"BraveSoftware\Brave-Browser\User Data")),
        new("vivaldi", "Vivaldi", "Vivaldi Technologies", Path.Combine(Places.Local, @"Vivaldi\User Data")),
        new("chromium", "Chromium", "The Chromium Authors", Path.Combine(Places.Local, @"Chromium\User Data")),
        new("opera", "Opera", "Opera Software", Path.Combine(Places.Roaming, @"Opera Software\Opera Stable"),
            Path.Combine(Places.Local, @"Opera Software\Opera Stable"), SingleProfile: true),
        new("operagx", "Opera GX", "Opera Software", Path.Combine(Places.Roaming, @"Opera Software\Opera GX Stable"),
            Path.Combine(Places.Local, @"Opera Software\Opera GX Stable"), SingleProfile: true),
    ];

    public static IEnumerable<CleanGroup> Find(ScanContext context)
    {
        foreach (var browser in ChromiumBrowsers())
        {
            if (!Directory.Exists(browser.UserData)) continue;
            context.Claim(browser.UserData);
            if (browser.CacheRoot is not null) context.Claim(browser.CacheRoot);
            var group = FindChromium(context, browser);
            if (group is not null) yield return group;
        }
        var firefox = FindFirefox(context);
        if (firefox is not null) yield return firefox;
    }

    private static CleanGroup? FindChromium(ScanContext context, Chromium browser)
    {
        var profiles = browser.SingleProfile
            ? [browser.UserData]
            : Finds.Children(browser.UserData)
                .Where(p => File.Exists(Path.Combine(p, "Preferences")))
                .ToList();
        if (profiles.Count == 0) return null;
        var cacheProfiles = browser.CacheRoot is null ? profiles : [browser.CacheRoot];

        IEnumerable<string> In(IEnumerable<string> roots, params string[] names) =>
            roots.SelectMany(r => names.Select(n => Path.Combine(r, n)));

        var name = browser.Title;
        var items = new List<CleanItem?>
        {
            Finds.Item("Кэш", CleanColor.Green,
                "Копии картинок, скриптов и страниц, чтобы сайты открывались быстрее.",
                $"Сайты в первый раз откроются чуть медленнее, {name} создаст кэш заново. Входы на сайтах и пароли останутся. Перед удалением закройте браузер.",
                In(cacheProfiles, "Cache", "Code Cache", "GPUCache", "DawnCache", "DawnGraphiteCache", "DawnWebGPUCache",
                    @"Service Worker\CacheStorage", @"Service Worker\ScriptCache", "Media Cache", "Application Cache")
                .Concat(In([browser.UserData], "ShaderCache", "GrShaderCache", "GraphiteDawnCache", "component_crx_cache"))),
            Finds.Item("Отчёты о сбоях", CleanColor.Green,
                "Отчёты, которые браузер собирает, когда падает.",
                "Ничего не изменится.",
                In([browser.UserData], @"Crashpad\reports", @"Crashpad\attachments")),
            Finds.Item("Cookies (входы на сайтах)", CleanColor.Yellow,
                "Файлы, по которым сайты узнают вас: входы в почту, соцсети, магазины, настройки сайтов.",
                "Вы выйдете со всех сайтов, придётся входить заново (иногда с кодом из СМС).",
                In(profiles, @"Network\Cookies", @"Network\Cookies-journal", "Cookies", "Cookies-journal")),
            Finds.Item("История", CleanColor.Yellow,
                "Какие сайты вы открывали и что скачивали.",
                "Исчезнет история посещений и загрузок, подсказки в адресной строке станут беднее. Закладки и пароли останутся.",
                In(profiles, "History", "History-journal", "Visited Links", "Top Sites", "Top Sites-journal", "Shortcuts", "Shortcuts-journal")),
            Finds.Item("Данные сайтов", CleanColor.Yellow,
                "То, что сайты хранят у вас: черновики, настройки, офлайн-копии, данные веб-приложений.",
                "Некоторые сайты забудут настройки и попросят войти снова; офлайн-данные (например, почты) скачаются заново.",
                In(profiles, "Local Storage", "IndexedDB", "Session Storage", "databases", "File System", @"Service Worker\Database")),
            Finds.Item("Открытые вкладки", CleanColor.Yellow,
                "Сохранённые вкладки и окна прошлого запуска.",
                "При следующем запуске браузер не восстановит открытые вкладки.",
                In(profiles, "Sessions", "Current Session", "Current Tabs", "Last Session", "Last Tabs")),
            Finds.Item("Пароли", CleanColor.Red,
                "Сохранённые пароли от сайтов. Они зашифрованы и привязаны к этому компьютеру.",
                "Пароли пропадут навсегда, если они не синхронизированы с аккаунтом. Удаляйте пароли только в настройках браузера.",
                In(profiles, "Login Data", "Login Data-journal", "Login Data For Account", "Login Data For Account-journal")),
            Finds.Item("Автозаполнение и карты", CleanColor.Red,
                "Адреса, телефоны и банковские карты для автозаполнения форм.",
                "Пропадут сохранённые адреса и карты. Чистить только в настройках браузера.",
                In(profiles, "Web Data", "Web Data-journal")),
            Finds.Item("Закладки", CleanColor.Red,
                "Ваши закладки.",
                "Закладки пропадут. Не трогать.",
                In(profiles, "Bookmarks", "Bookmarks.bak")),
            Finds.Item("Расширения", CleanColor.Red,
                "Установленные расширения браузера и их настройки.",
                "Расширения сломаются. Удаляйте их в самом браузере.",
                In(profiles, "Extensions", "Local Extension Settings")),
        };
        var status = Finds.Installed(context.Inventory, browser.InstallName ?? browser.Title);
        if (profiles.Count > 1) status += $" · профилей: {profiles.Count}";
        return Finds.Group("browser:" + browser.Id, browser.Title, CleanCategory.Browsers, status, browser.Publisher, items);
    }

    private static CleanGroup? FindFirefox(ScanContext context)
    {
        var roamingRoot = Path.Combine(Places.Roaming, @"Mozilla\Firefox");
        var localRoot = Path.Combine(Places.Local, @"Mozilla\Firefox");
        if (!Directory.Exists(roamingRoot) && !Directory.Exists(localRoot)) return null;
        context.Claim(roamingRoot);
        context.Claim(localRoot);
        var profiles = Finds.Children(Path.Combine(roamingRoot, "Profiles")).ToList();
        var localProfiles = Finds.Children(Path.Combine(localRoot, "Profiles")).ToList();

        IEnumerable<string> In(IEnumerable<string> roots, params string[] names) =>
            roots.SelectMany(r => names.Select(n => Path.Combine(r, n)));

        var items = new List<CleanItem?>
        {
            Finds.Item("Кэш", CleanColor.Green,
                "Копии картинок, скриптов и страниц, чтобы сайты открывались быстрее, и кэш запуска.",
                "Сайты в первый раз откроются чуть медленнее, Firefox создаст кэш заново. Входы и пароли останутся. Перед удалением закройте Firefox.",
                In(localProfiles, "cache2", "startupCache", "thumbnails", "jumpListCache", "OfflineCache")
                    .Concat(In(profiles, "shader-cache", "startupCache"))),
            Finds.Item("Отчёты о сбоях", CleanColor.Green,
                "Отчёты, которые Firefox собирает, когда падает.",
                "Ничего не изменится.",
                In([roamingRoot], @"Crash Reports\pending", @"Crash Reports\submitted").Concat(In(profiles, "minidumps", "crashes"))),
            Finds.Item("Cookies (входы на сайтах)", CleanColor.Yellow,
                "Файлы, по которым сайты узнают вас: входы в почту, соцсети, магазины.",
                "Вы выйдете со всех сайтов, придётся входить заново.",
                In(profiles, "cookies.sqlite", "cookies.sqlite-wal")),
            Finds.Item("Данные сайтов", CleanColor.Yellow,
                "То, что сайты хранят у вас: настройки, черновики, офлайн-данные.",
                "Некоторые сайты забудут настройки и попросят войти снова.",
                In(profiles, "storage", "webappsstore.sqlite", "webappsstore.sqlite-wal")),
            Finds.Item("Открытые вкладки", CleanColor.Yellow,
                "Сохранённые вкладки и окна прошлого запуска.",
                "Firefox не восстановит открытые вкладки.",
                In(profiles, "sessionstore.jsonlz4", "sessionstore-backups")),
            Finds.Item("Подсказки в формах", CleanColor.Yellow,
                "Что вы вводили в поля на сайтах (поиск, имена, адреса).",
                "Пропадут подсказки при вводе в формы.",
                In(profiles, "formhistory.sqlite")),
            Finds.Item("История и закладки", CleanColor.Red,
                "В Firefox история и закладки лежат в одном файле (places.sqlite).",
                "Удаление файла сотрёт и закладки. Историю чистите в самом Firefox: Настройки → Приватность.",
                In(profiles, "places.sqlite", "places.sqlite-wal", "favicons.sqlite", "favicons.sqlite-wal")),
            Finds.Item("Пароли", CleanColor.Red,
                "Сохранённые пароли и ключ, которым они зашифрованы.",
                "Пароли пропадут навсегда, если нет синхронизации. Удаляйте их только в Firefox.",
                In(profiles, "logins.json", "logins-backup.json", "key4.db")),
            Finds.Item("Расширения", CleanColor.Red,
                "Установленные дополнения и их настройки.",
                "Дополнения сломаются. Удаляйте их в Firefox.",
                In(profiles, "extensions", "extension-settings.json")),
        };
        var status = Finds.Installed(context.Inventory, "Firefox");
        if (profiles.Count > 1) status += $" · профилей: {profiles.Count}";
        return Finds.Group("browser:firefox", "Firefox", CleanCategory.Browsers, status, "Mozilla", items);
    }
}
