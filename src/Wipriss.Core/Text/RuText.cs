namespace Wipriss.Core.Text;

public static class RuText
{
    /// <summary>Russian plural: 1 заметка, 2 заметки, 5 заметок, 21 заметка, 11 заметок.</summary>
    public static string Plural(int n, string one, string few, string many)
    {
        int lastTwo = Math.Abs(n) % 100;
        int last = lastTwo % 10;
        if (lastTwo is > 10 and < 20) return many;
        if (last == 1) return one;
        if (last is >= 2 and <= 4) return few;
        return many;
    }

    public static string Count(int n, string one, string few, string many) => $"{n} {Plural(n, one, few, many)}";

    /// <summary>How long ago, short: "только что", "5 мин", "3 ч", "2 дн".</summary>
    public static string Age(TimeSpan span)
    {
        if (span.TotalMinutes < 1) return "только что";
        if (span.TotalHours < 1) return $"{(int)span.TotalMinutes} мин";
        if (span.TotalDays < 1) return $"{(int)span.TotalHours} ч";
        return $"{(int)span.TotalDays} дн";
    }

    /// <summary>Memory size like Task Manager shows it: "84 МБ", "1,2 ГБ".</summary>
    public static string Memory(long bytes)
    {
        double mb = bytes / (1024.0 * 1024.0);
        if (mb < 1) return "<1 МБ";
        if (mb < 1000) return $"{mb:0} МБ";
        return (mb / 1024).ToString("0.0", Ru) + " ГБ";
    }

    private static readonly System.Globalization.CultureInfo Ru = System.Globalization.CultureInfo.GetCultureInfo("ru-RU");
}
