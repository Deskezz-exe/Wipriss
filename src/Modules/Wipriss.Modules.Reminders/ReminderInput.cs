using System.Globalization;
using System.Text.RegularExpressions;

namespace Wipriss.Modules.Reminders;

/// <summary>Understands dates and times the way people type them, and describes them back in Russian.</summary>
public static partial class ReminderInput
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    /// <summary>"21:00", "21.00", "21 00", "2100", "21", "9:05".</summary>
    public static bool TryParseTime(string? text, out TimeSpan time)
    {
        time = default;
        var match = TimePattern().Match((text ?? "").Trim());
        if (!match.Success) return false;

        int hours = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        int minutes = match.Groups[2].Success ? int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture) : 0;
        if (hours > 23 || minutes > 59) return false;

        time = new TimeSpan(hours, minutes, 0);
        return true;
    }

    /// <summary>
    /// Empty or "сегодня", "завтра", "послезавтра", "30.11", "30.11.2026", "30/11/26".
    /// A day without a year that has already passed means next year.
    /// </summary>
    public static bool TryParseDate(string? text, DateTime today, out DateTime date)
    {
        date = default;
        today = today.Date;
        var t = (text ?? "").Trim().ToLowerInvariant();

        switch (t)
        {
            case "" or "сегодня" or "сег":
                date = today;
                return true;
            case "завтра" or "зав":
                date = today.AddDays(1);
                return true;
            case "послезавтра":
                date = today.AddDays(2);
                return true;
        }

        var match = DatePattern().Match(t);
        if (!match.Success) return false;

        int day = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        int month = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
        bool hasYear = match.Groups[3].Success;
        int year = today.Year;
        if (hasYear)
        {
            year = int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
            if (year < 100) year += 2000;
        }

        if (!IsValid(year, month, day)) return false;
        date = new DateTime(year, month, day);

        if (!hasYear && date < today)
        {
            if (!IsValid(year + 1, month, day)) return false;
            date = new DateTime(year + 1, month, day);
        }

        return true;
    }

    /// <summary>"сегодня, 21:00", "завтра, 09:00", "30 ноября, вс, 21:00".</summary>
    public static string Describe(DateTime at, DateTime now)
    {
        string day = at.Date == now.Date ? "сегодня"
            : at.Date == now.Date.AddDays(1) ? "завтра"
            : at.ToString(at.Year == now.Year ? "d MMMM, ddd" : "d MMMM yyyy, ddd", Ru);
        return $"{day}, {at.ToString("HH:mm", Ru)}";
    }

    /// <summary>Short form for the island tile, which fits about 13 characters: "сегодня 21:00", "30.11 21:00".</summary>
    public static string Short(DateTime at, DateTime now)
    {
        string day = at.Date == now.Date ? "сегодня"
            : at.Date == now.Date.AddDays(1) ? "завтра"
            : at.ToString("dd.MM", Ru);
        return $"{day} {at.ToString("HH:mm", Ru)}";
    }

    private static bool IsValid(int year, int month, int day) =>
        year is >= 2000 and <= 2999 && month is >= 1 and <= 12 && day >= 1 && day <= DateTime.DaysInMonth(year, month);

    [GeneratedRegex(@"^(\d{1,2})(?:[:.\s-]?(\d{2}))?$")]
    private static partial Regex TimePattern();

    [GeneratedRegex(@"^(\d{1,2})[.\s/-](\d{1,2})(?:[.\s/-](\d{4}|\d{2}))?$")]
    private static partial Regex DatePattern();
}
