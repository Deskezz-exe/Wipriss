using System.Globalization;

namespace Wipriss.ProcessTools;

/// <summary>Everything known about one program's network use today.</summary>
public sealed record TrafficFacts
{
    /// <summary>Full path of the program file. Null for Store apps and for Windows' own traffic.</summary>
    public string? Path { get; init; }

    /// <summary>File name ("notepad.exe") or, for a Store app, its package family name.</summary>
    public required string FileName { get; init; }

    public bool IsStoreApp { get; init; }
    public bool InWindowsFolder { get; init; }
    public FileSignature? Signature { get; init; }

    public long ReceivedToday { get; init; }
    public long SentToday { get; init; }

    /// <summary>The most it used on any single day of the past week. 0 without history.</summary>
    public long UsualDailyMax { get; init; }

    /// <summary>Days of the past week it used the network at all.</summary>
    public int ActiveDays { get; init; }

    /// <summary>Share of its past traffic that was upload, 0..1.</summary>
    public double UsualSentShare { get; init; }

    /// <summary>Its connections to other computers right now.</summary>
    public IReadOnlyList<ConnectionEntry> Connections { get; init; } = [];

    /// <summary>CPU share like Task Manager (100 = all cores busy). Null when not measured.</summary>
    public double? CpuPercent { get; init; }

    /// <summary>File name of the program that started it, if it is running.</summary>
    public string? ParentName { get; init; }

    public long TotalToday => ReceivedToday + SentToday;
}

/// <summary>One reason, with how much it adds to suspicion (negative means it adds trust).</summary>
public sealed record Finding(int Weight, string Text);

public enum ThreatLevel { None, Suspicious, High }

public sealed record Verdict(int Score, IReadOnlyList<Finding> Findings)
{
    public const int SuspiciousFrom = 3, HighFrom = 6;

    public ThreatLevel Level => Score >= HighFrom ? ThreatLevel.High : Score >= SuspiciousFrom ? ThreatLevel.Suspicious : ThreatLevel.None;

    /// <summary>Only the reasons that raise suspicion, strongest first.</summary>
    public IEnumerable<Finding> Reasons => Findings.Where(f => f.Weight > 0).OrderByDescending(f => f.Weight);
}

/// <summary>
/// Decides whether a program's network use looks wrong. Every sign adds points with a reason in plain words;
/// a valid signature or a Store install takes points away. Nothing here is a verdict of "virus":
/// it is a list of things a careful person would want to look at.
/// </summary>
public static class TrafficCheck
{
    private const long MB = 1024 * 1024;

    // Programs that have no business moving lots of data. (Paint is left out: it has online AI features.)
    private static readonly Dictionary<string, string> OfflineApps = new(StringComparer.OrdinalIgnoreCase)
    {
        ["notepad.exe"] = "Блокнот",
        ["Microsoft.WindowsNotepad"] = "Блокнот",
        ["calc.exe"] = "Калькулятор",
        ["CalculatorApp.exe"] = "Калькулятор",
        ["Microsoft.WindowsCalculator"] = "Калькулятор",
        ["wordpad.exe"] = "WordPad",
        ["write.exe"] = "WordPad",
        ["charmap.exe"] = "Таблица символов",
        ["osk.exe"] = "Экранная клавиатура",
        ["magnify.exe"] = "Лупа",
    };

    // Core Windows programs that malware likes to borrow names from.
    private static readonly string[] WindowsNames =
    [
        "svchost", "csrss", "lsass", "winlogon", "services", "smss", "explorer", "wininit", "spoolsv",
        "taskhostw", "rundll32", "dllhost", "runtimebroker", "conhost", "ctfmon", "sihost", "dwm",
    ];

    // Windows tools that are often abused to download or run malware ("living off the land").
    private static readonly HashSet<string> AbusedTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "mshta.exe", "certutil.exe", "bitsadmin.exe", "regsvr32.exe", "rundll32.exe", "wscript.exe", "cscript.exe",
    };

    private static readonly HashSet<string> ScriptHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "powershell.exe", "pwsh.exe", "cmd.exe", "mshta.exe", "wscript.exe", "cscript.exe", "rundll32.exe",
        "regsvr32.exe", "python.exe", "pythonw.exe", "node.exe",
    };

    // Programs that should not start scripts or shells; when they do, it is often a malicious document.
    private static readonly HashSet<string> DocumentApps = new(StringComparer.OrdinalIgnoreCase)
    {
        "winword.exe", "excel.exe", "powerpnt.exe", "outlook.exe", "notepad.exe", "acrord32.exe", "acrobat.exe", "wordpad.exe",
    };

    // Stratum ports used by crypto-mining pools.
    private static readonly HashSet<int> MiningPorts = [3333, 4444, 5555, 6666, 7777, 9999, 14433, 14444, 3357, 5730, 10128, 20535, 45560, 45700];
    private static readonly HashSet<int> TorPorts = [9001, 9030, 9050, 9051, 9150];
    private static readonly HashSet<int> IrcPorts = [6667, 6697];

    public static Verdict Evaluate(TrafficFacts f, FolderHints? folders = null)
    {
        folders ??= FolderHints.Current;
        var findings = new List<Finding>();
        string name = System.IO.Path.GetFileName(f.FileName);
        string stem = System.IO.Path.GetFileNameWithoutExtension(name).ToLowerInvariant();

        // Who it is
        string? offlineKey = OfflineApps.Keys.FirstOrDefault(k => name.Equals(k, StringComparison.OrdinalIgnoreCase)
            || (f.IsStoreApp && f.FileName.StartsWith(k + "_", StringComparison.OrdinalIgnoreCase)));
        if (offlineKey is not null && f.TotalToday >= 20 * MB)
            findings.Add(new(5, $"«{OfflineApps[offlineKey]}» обычно почти не пользуется сетью, а сегодня {Bytes(f.TotalToday)}"));

        if (!f.IsStoreApp && f.Path is not null && !f.InWindowsFolder)
        {
            if (WindowsNames.Contains(stem))
                findings.Add(new(5, $"Называется как файл Windows ({name}), но лежит не в папке Windows"));
            else if (stem.Length >= 5 && WindowsNames.FirstOrDefault(w => w.Length >= 5 && OneEditApart(stem, w)) is { } lookalike)
                findings.Add(new(3, $"Имя похоже на файл Windows «{lookalike}.exe»"));

            if (folders.Place(f.Path) is { } place)
                findings.Add(new(place.Weight, $"Запущен из папки «{place.Name}»"));
        }

        switch (f.Signature?.State)
        {
            case SignatureState.Signed:
                findings.Add(new(-2, f.Signature.Publisher is { } p ? $"Подписан: {p}" : "Есть цифровая подпись"));
                break;
            case SignatureState.Unsigned when !f.InWindowsFolder && !f.IsStoreApp:
                findings.Add(new(2, "Нет цифровой подписи"));
                break;
            case SignatureState.Broken:
                findings.Add(new(4, "Подпись не сходится: файл изменили после подписания"));
                break;
        }

        if (f.IsStoreApp) findings.Add(new(-2, "Установлен из Microsoft Store"));

        if (AbusedTools.Contains(name) && (f.TotalToday >= MB || f.Connections.Count > 0))
            findings.Add(new(3, $"Системная утилита {name} ходит в интернет: так часто скачивают вирусы"));
        else if (stem is "powershell" or "pwsh" && f.TotalToday >= 200 * MB)
            findings.Add(new(2, $"PowerShell передал {Bytes(f.TotalToday)}"));

        if (f.ParentName is { } parent && DocumentApps.Contains(parent) && ScriptHosts.Contains(name))
            findings.Add(new(3, $"Запущен из {parent}: так часто действуют вредоносные документы"));

        // What it does
        if (f.ActiveDays >= 2 && f.UsualDailyMax > 0 && f.TotalToday >= 100 * MB && f.TotalToday > 5 * f.UsualDailyMax)
            findings.Add(new(2, $"Сегодня {Bytes(f.TotalToday)}, а обычно не больше {Bytes(f.UsualDailyMax)} в день"));
        else if (f.ActiveDays == 0 && f.TotalToday >= 200 * MB)
            findings.Add(new(1, $"Всю прошлую неделю не выходил в сеть, а сегодня {Bytes(f.TotalToday)}"));

        if (f.SentToday >= 100 * MB && f.SentToday >= 3 * f.ReceivedToday && (f.ActiveDays == 0 || f.UsualSentShare < 0.5))
            findings.Add(new(2, $"Отправил {Bytes(f.SentToday)}, а получил только {Bytes(f.ReceivedToday)}"));

        if (f.Connections.FirstOrDefault(c => MiningPorts.Contains(c.RemotePort)) is { } mining)
        {
            findings.Add(new(3, $"Соединение с портом {mining.RemotePort}: через такие порты работают майнинг-пулы"));
            if (f.CpuPercent >= 30)
                findings.Add(new(2, $"И при этом грузит процессор на {f.CpuPercent.Value.ToString("0", CultureInfo.InvariantCulture)} %"));
        }

        if (f.Connections.FirstOrDefault(c => TorPorts.Contains(c.RemotePort)) is { } tor)
            findings.Add(new(2, $"Соединение с портом {tor.RemotePort}, это сеть Tor"));
        if (f.Connections.FirstOrDefault(c => IrcPorts.Contains(c.RemotePort)) is { } irc)
            findings.Add(new(2, $"Соединение с портом {irc.RemotePort}, это IRC: через него часто управляют заражёнными компьютерами"));

        int addresses = f.Connections.Select(c => c.RemoteAddress).Distinct().Count();
        if (addresses >= 60 && f.Signature?.State != SignatureState.Signed && !f.IsStoreApp)
            findings.Add(new(2, $"Связан сразу с {addresses} адресами"));

        return new Verdict(findings.Sum(x => x.Weight), findings);
    }

    /// <summary>True when one letter is changed, added, removed or two neighbours are swapped ("svch0st" vs "svchost").</summary>
    public static bool OneEditApart(string a, string b)
    {
        if (a == b || Math.Abs(a.Length - b.Length) > 1) return false;
        if (a.Length == b.Length)
        {
            var diffs = Enumerable.Range(0, a.Length).Where(i => a[i] != b[i]).ToList();
            return diffs.Count == 1 || (diffs.Count == 2 && diffs[1] == diffs[0] + 1 && a[diffs[0]] == b[diffs[1]] && a[diffs[1]] == b[diffs[0]]);
        }

        string longer = a.Length > b.Length ? a : b, shorter = a.Length > b.Length ? b : a;
        for (int i = 0; i < longer.Length; i++)
            if (longer.Remove(i, 1) == shorter) return true;
        return false;
    }

    public static string Bytes(long bytes)
    {
        var ru = CultureInfo.GetCultureInfo("ru-RU");
        if (bytes < MB) return $"{Math.Max(1, bytes / 1024)} КБ";
        double mb = bytes / (double)MB;
        return mb < 1000 ? $"{mb:0} МБ" : (mb / 1024).ToString("0.0", ru) + " ГБ";
    }
}

/// <summary>Folders where programs rarely live legitimately. Built from this PC's real folders, nothing hard-coded.</summary>
public sealed class FolderHints
{
    private readonly List<(string Prefix, string Name, int Weight)> _places;

    public FolderHints(IEnumerable<(string Prefix, string Name, int Weight)> places) =>
        _places = places.Where(p => !string.IsNullOrEmpty(p.Prefix))
            .Select(p => (p.Prefix.TrimEnd('\\') + "\\", p.Name, p.Weight))
            .OrderByDescending(p => p.Item1.Length) // most specific first: Temp inside AppData wins
            .ToList();

    public static FolderHints Current { get; } = new(
    [
        (System.IO.Path.GetTempPath(), "Temp", 2),
        (System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp"), "Temp", 2),
        (System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"), "Загрузки", 2),
        (Environment.GetFolderPath(Environment.SpecialFolder.CommonDocuments) is { Length: > 0 } docs ? System.IO.Path.GetDirectoryName(docs)! : "", "Общие", 2),
        (Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AppData\\Roaming", 1),
    ]);

    public (string Name, int Weight)? Place(string path)
    {
        if (path.Contains("\\$Recycle.Bin\\", StringComparison.OrdinalIgnoreCase)) return ("Корзина", 3);
        foreach (var (prefix, name, weight) in _places)
            if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return (name, weight);
        return null;
    }
}
