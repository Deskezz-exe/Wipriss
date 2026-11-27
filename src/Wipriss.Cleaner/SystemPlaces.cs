using System.IO;

namespace Wipriss.Cleaner;

/// <summary>What the scan knows while it runs: the inventory and the folders already described by a rule
/// (so the leftover search does not report them a second time).</summary>
internal sealed class ScanContext(Inventory inventory)
{
    public Inventory Inventory { get; } = inventory;

    private readonly HashSet<string> _claimed = new(StringComparer.OrdinalIgnoreCase);

    public void Claim(string path)
    {
        var normal = PathText.Normalize(path);
        if (normal is not null) lock (_claimed) _claimed.Add(normal);
    }

    public bool IsClaimed(string path)
    {
        lock (_claimed)
            return _claimed.Any(c => PathText.IsInside(path, c) || PathText.IsInside(c, path));
    }
}

/// <summary>Windows itself and the graphics drivers: what may be cleaned, and what should not be.</summary>
internal static class SystemPlaces
{
    public static CleanGroup? Windows(ScanContext context)
    {
        var win = Places.Windows;
        var drive = Places.SystemDrive;
        foreach (var path in new[] { Path.Combine(drive, "Windows.old"), Path.Combine(drive, "$Windows.~BT"), Path.Combine(drive, "$Windows.~WS"),
                                     Path.Combine(drive, "$WinREAgent") })
            context.Claim(path);

        long recycleBytes = Places.RecycleBinSize(out long recycleItems);
        CleanItem? recycle = recycleBytes > 0
            ? new CleanItem
            {
                Title = "Корзина", Size = recycleBytes, Files = (int)Math.Min(int.MaxValue, recycleItems), Color = CleanColor.Yellow,
                What = "Файлы, которые вы удалили, но ещё можете вернуть.",
                IfDeleted = "Вернуть эти файлы будет нельзя.",
                Action = CleanAction.RecycleBin,
                Confidence = "высокая",
            }
            : null;

        var items = new List<CleanItem?>
        {
            Finds.Item("Временные файлы", CleanColor.Green,
                "Файлы, которые программы создают на время работы и часто забывают удалить.",
                "Ничего не сломается. Файлы, которыми программы пользуются прямо сейчас, будут пропущены.",
                Places.Temp),
            Finds.Item("Временные файлы Windows", CleanColor.Green,
                "Временные файлы самой Windows и установщиков.",
                "Ничего не сломается. Занятые файлы будут пропущены.",
                Path.Combine(win, "Temp")),
            Finds.Item("Отчёты об ошибках", CleanColor.Green,
                "Отчёты, которые Windows собирает, когда программа зависает или падает.",
                "Ничего не изменится, только пропадут сведения о прошлых сбоях.",
                [Path.Combine(Places.Local, @"Microsoft\Windows\WER"), Path.Combine(Places.ProgramData, @"Microsoft\Windows\WER\ReportArchive"),
                 Path.Combine(Places.ProgramData, @"Microsoft\Windows\WER\ReportQueue")]),
            Finds.Item("Дампы памяти после сбоев", CleanColor.Green,
                "Снимки памяти упавших программ и синих экранов. Нужны только разработчикам для разбора.",
                "Ничего не изменится.",
                [Path.Combine(Places.Local, "CrashDumps"), Path.Combine(win, "Minidump"), Path.Combine(win, "MEMORY.DMP"),
                 Path.Combine(win, "LiveKernelReports")]),
            Finds.Item("Кэш эскизов и значков", CleanColor.Green,
                "Уменьшенные копии фото и значки, чтобы Проводник показывал их быстрее.",
                "Проводник создаст их заново; первое открытие папок с фото будет медленнее.",
                Finds.FilesIn(Path.Combine(Places.Local, @"Microsoft\Windows\Explorer"), "thumbcache_*.db")
                    .Concat(Finds.FilesIn(Path.Combine(Places.Local, @"Microsoft\Windows\Explorer"), "iconcache_*.db"))),
            Finds.Item("Кэш обновлений Windows", CleanColor.Green,
                "Скачанные файлы обновлений, которые уже установлены.",
                "Ничего не сломается. Если обновление ещё не установлено, Windows скачает его заново.",
                Path.Combine(win, @"SoftwareDistribution\Download"),
                "Средствами Windows: «Очистка диска» → «Очистить системные файлы» → «Очистка обновлений Windows»."),
            Finds.Item("Оптимизация доставки", CleanColor.Green,
                "Части обновлений, которыми компьютер делится с другими по сети.",
                "Ничего не сломается.",
                Path.Combine(win, @"ServiceProfiles\NetworkService\AppData\Local\Microsoft\Windows\DeliveryOptimization\Cache"),
                "Средствами Windows: Параметры → Система → Память → Временные файлы → «Файлы оптимизации доставки»."),
            recycle,
            Finds.Item("Предыдущая версия Windows", CleanColor.Yellow,
                "Копия старой Windows после большого обновления (Windows.old).",
                "Откатиться на прошлую версию Windows будет нельзя. Обычно Windows сама удаляет её через 10 дней.",
                [Path.Combine(drive, "Windows.old"), Path.Combine(drive, "$Windows.~BT"), Path.Combine(drive, "$Windows.~WS")],
                "Средствами Windows: Параметры → Система → Память → Временные файлы → «Предыдущие установки Windows»."),
            Finds.Item("Журналы установки", CleanColor.Yellow,
                "Журналы обновлений и установки компонентов Windows.",
                "Ничего не сломается, но при проблемах с обновлением их нечем будет разобрать.",
                [Path.Combine(win, @"Logs\CBS"), Path.Combine(win, @"Logs\DISM"), Path.Combine(win, @"Panther")]),
            Finds.Item("Предвыборка (Prefetch)", CleanColor.Red,
                "Подсказки, по которым Windows быстрее запускает программы.",
                "Места почти не освободится, а программы первое время будут запускаться медленнее. Не советуем.",
                Path.Combine(win, "Prefetch")),
            Finds.Item("Установщики программ (Installer)", CleanColor.Red,
                "Выглядит как мусор, но без этих файлов программы нельзя удалить или обновить.",
                "Программы перестанут удаляться и обновляться. Не трогать.",
                Path.Combine(win, "Installer")),
            Finds.Item("Кэш пакетов (Package Cache)", CleanColor.Red,
                "Установщики Visual C++, .NET и других компонентов, нужные для их удаления и починки.",
                "Компоненты перестанут удаляться и обновляться. Не трогать.",
                Path.Combine(Places.ProgramData, "Package Cache")),
            new CleanItem
            {
                Title = "Хранилище компонентов (WinSxS)", Size = -1, Color = CleanColor.Red, Paths = [Path.Combine(win, "WinSxS")],
                What = "Сердце Windows: все версии системных файлов. Проводник показывает его больше, чем он есть (одни и те же файлы считаются дважды).",
                IfDeleted = "Удаление файлов отсюда ломает Windows. Не трогать.",
                HowToClean = "Только средством Windows: в командной строке от администратора «DISM /Online /Cleanup-Image /StartComponentCleanup».",
                Action = CleanAction.WindowsOnly,
                Confidence = "высокая",
            },
            SystemFile("Файл гибернации (hiberfil.sys)", Path.Combine(drive, "hiberfil.sys"), CleanColor.Red,
                "Здесь Windows сохраняет память при гибернации и для быстрого запуска.",
                "Удалить файл нельзя, только выключить гибернацию: пропадут гибернация и «быстрый запуск».",
                "Только средством Windows: в командной строке от администратора «powercfg /hibernate off»."),
            SystemFile("Файл подкачки (pagefile.sys)", Path.Combine(drive, "pagefile.sys"), CleanColor.Red,
                "Продолжение оперативной памяти на диске.",
                "Без него программы падают, когда памяти не хватает. Не трогать.",
                "Размер меняется в «Дополнительных параметрах системы» → Быстродействие → Виртуальная память."),
        };
        return Finds.Group("windows", "Windows", CleanCategory.Windows, "часть Windows", "Microsoft", items);
    }

    /// <summary>hiberfil.sys and pagefile.sys are locked: the size comes from the folder listing.</summary>
    private static CleanItem? SystemFile(string title, string path, CleanColor color, string what, string ifDeleted, string howTo)
    {
        long size;
        try
        {
            var info = new DirectoryInfo(Path.GetDirectoryName(path)!).EnumerateFiles(Path.GetFileName(path)).FirstOrDefault();
            if (info is null) return null;
            size = info.Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
        return new CleanItem { Title = title, Paths = [path], Size = size, Files = 1, Color = color, What = what, IfDeleted = ifDeleted, HowToClean = howTo, Confidence = "высокая", Action = CleanAction.WindowsOnly };
    }

    private const string ShaderWhat = "Готовые шейдеры для игр и программ, чтобы картинка не подтормаживала при первом запуске.";
    private const string ShaderIfDeleted = "Ничего не сломается. Игры первые минуты могут чуть подтормаживать, пока кэш создаётся заново.";

    public static IEnumerable<CleanGroup> Graphics(ScanContext context)
    {
        string drive = Places.SystemDrive;
        foreach (var path in new[] { Path.Combine(Places.Local, "NVIDIA"), Path.Combine(Places.LocalLow, "NVIDIA"),
                                     Path.Combine(Places.ProgramData, "NVIDIA Corporation"), Path.Combine(Places.Local, "AMD"),
                                     Path.Combine(Places.LocalLow, "AMD"), Path.Combine(Places.LocalLow, "Intel"),
                                     Path.Combine(Places.Local, "D3DSCache"), Path.Combine(drive, "NVIDIA"), Path.Combine(drive, "AMD") })
            context.Claim(path);

        var nvidia = Finds.Group("gpu:nvidia", "NVIDIA", CleanCategory.Graphics, Finds.Installed(context.Inventory, "NVIDIA"), "NVIDIA",
        [
            Finds.Item("Кэш шейдеров", CleanColor.Green, ShaderWhat, ShaderIfDeleted,
                [Path.Combine(Places.Local, @"NVIDIA\DXCache"), Path.Combine(Places.Local, @"NVIDIA\GLCache"), Path.Combine(Places.Local, @"NVIDIA\OptixCache"),
                 Path.Combine(Places.LocalLow, @"NVIDIA\PerDriverVersion\DXCache"), Path.Combine(Places.LocalLow, @"NVIDIA\PerDriverVersion\GLCache"),
                 Path.Combine(Places.ProgramData, @"NVIDIA Corporation\NV_Cache")]),
            Finds.Item("Распакованный установщик драйвера", CleanColor.Green,
                "Файлы, которые установщик драйвера NVIDIA распаковал и оставил.",
                "Драйвер уже установлен, ничего не изменится.",
                Path.Combine(drive, "NVIDIA")),
            Finds.Item("Скачанные установщики драйверов", CleanColor.Green,
                "Установщики драйверов, которые скачало приложение NVIDIA.",
                "Ничего не изменится, новые драйверы скачаются при обновлении.",
                Path.Combine(Places.ProgramData, @"NVIDIA Corporation\Downloader")),
        ]);
        if (nvidia is not null) yield return nvidia;

        var amd = Finds.Group("gpu:amd", "AMD", CleanCategory.Graphics, Finds.Installed(context.Inventory, "AMD"), "AMD",
        [
            Finds.Item("Кэш шейдеров", CleanColor.Green, ShaderWhat, ShaderIfDeleted,
                new[] { "DxCache", "DxcCache", "Dx9Cache", "GLCache", "VkCache" }.Select(n => Path.Combine(Places.Local, "AMD", n))),
            Finds.Item("Распакованный установщик драйвера", CleanColor.Green,
                "Файлы, которые установщик драйвера AMD распаковал и оставил.",
                "Драйвер уже установлен, ничего не изменится.",
                Path.Combine(drive, "AMD")),
        ]);
        if (amd is not null) yield return amd;

        var intel = Finds.Group("gpu:intel", "Intel", CleanCategory.Graphics, "драйвер видеокарты", "Intel",
        [
            Finds.Item("Кэш шейдеров", CleanColor.Green, ShaderWhat, ShaderIfDeleted, Path.Combine(Places.LocalLow, @"Intel\ShaderCache")),
        ]);
        if (intel is not null) yield return intel;

        var directx = Finds.Group("gpu:directx", "DirectX", CleanCategory.Graphics, "часть Windows", "Microsoft",
        [
            Finds.Item("Кэш шейдеров", CleanColor.Green, ShaderWhat, ShaderIfDeleted, Path.Combine(Places.Local, "D3DSCache")),
        ]);
        if (directx is not null) yield return directx;
    }
}
