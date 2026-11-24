using Wipriss.Modules.Reminders;

namespace Wipriss.Tests;

public class ReminderInputTests
{
    private static readonly DateTime Today = new(2026, 11, 24, 15, 30, 0);

    [Theory]
    [InlineData("21:00", 21, 0)]
    [InlineData("21.00", 21, 0)]
    [InlineData("21 00", 21, 0)]
    [InlineData("2100", 21, 0)]
    [InlineData("21", 21, 0)]
    [InlineData("9:05", 9, 5)]
    [InlineData("905", 9, 5)]
    [InlineData(" 0:00 ", 0, 0)]
    [InlineData("23:59", 23, 59)]
    public void Time_is_understood(string text, int hours, int minutes)
    {
        Assert.True(ReminderInput.TryParseTime(text, out var time));
        Assert.Equal(new TimeSpan(hours, minutes, 0), time);
    }

    [Theory]
    [InlineData("")]
    [InlineData("24:00")]
    [InlineData("12:60")]
    [InlineData("abc")]
    [InlineData("12:5")]
    [InlineData("123456")]
    public void Wrong_time_is_rejected(string text) => Assert.False(ReminderInput.TryParseTime(text, out _));

    [Theory]
    [InlineData("", 2026, 11, 24)]
    [InlineData("сегодня", 2026, 11, 24)]
    [InlineData("Завтра", 2026, 11, 25)]
    [InlineData("послезавтра", 2026, 11, 26)]
    [InlineData("30.11", 2026, 11, 30)]
    [InlineData("30/11", 2026, 11, 30)]
    [InlineData("30.11.2026", 2026, 11, 30)]
    [InlineData("1.12.26", 2026, 12, 1)]
    [InlineData("5.01", 2027, 1, 5)] // already passed this year: next year
    public void Date_is_understood(string text, int year, int month, int day)
    {
        Assert.True(ReminderInput.TryParseDate(text, Today, out var date));
        Assert.Equal(new DateTime(year, month, day), date);
    }

    [Theory]
    [InlineData("31.02")]
    [InlineData("32.01")]
    [InlineData("10.13")]
    [InlineData("в пятницу")]
    public void Wrong_date_is_rejected(string text) => Assert.False(ReminderInput.TryParseDate(text, Today, out _));

    [Fact]
    public void Describes_today_tomorrow_and_later()
    {
        Assert.Equal("сегодня, 21:00", ReminderInput.Describe(Today.Date.AddHours(21), Today));
        Assert.Equal("завтра, 09:00", ReminderInput.Describe(Today.Date.AddDays(1).AddHours(9), Today));
        Assert.Equal("30 ноября, пн, 21:00", ReminderInput.Describe(new DateTime(2026, 11, 30, 21, 0, 0), Today));
    }
}
