using Wipriss.Core.Input;
using Wipriss.Modules.Agents;
using Wipriss.Modules.Media;
using Wipriss.Modules.Timer;

namespace Wipriss.Tests;

public class TimerClockTests
{
    private static readonly DateTime T0 = new(2026, 11, 28, 12, 0, 0);

    [Fact]
    public void Preset_starts_at_once_and_counts_down()
    {
        var c = new TimerClock();
        c.Preset(TimeSpan.FromMinutes(5), T0);
        Assert.True(c.Running);
        Assert.Equal(T0.AddMinutes(5), c.EndsAt);
        Assert.Equal(TimeSpan.FromMinutes(3), c.Left(T0.AddMinutes(2)));
        Assert.Equal(TimeSpan.Zero, c.Left(T0.AddMinutes(9)));
    }

    [Fact]
    public void Pause_keeps_the_time_left_and_start_goes_on_from_it()
    {
        var c = new TimerClock();
        c.Preset(TimeSpan.FromMinutes(15), T0);
        c.Pause(T0.AddMinutes(5));
        Assert.False(c.Running);
        Assert.True(c.Paused);
        Assert.Equal(TimeSpan.FromMinutes(10), c.Left(T0.AddHours(1)));
        c.Start(T0.AddHours(1));
        Assert.Equal(T0.AddHours(1).AddMinutes(10), c.EndsAt);
    }

    [Fact]
    public void Reset_and_finish_go_back_to_the_full_length()
    {
        var c = new TimerClock();
        c.Preset(TimeSpan.FromMinutes(25), T0);
        c.Reset();
        Assert.False(c.Running);
        Assert.False(c.Paused);
        Assert.Equal(TimeSpan.FromMinutes(25), c.Left(T0));
        c.Start(T0);
        c.Finish();
        Assert.False(c.Running);
        Assert.Equal(TimeSpan.FromMinutes(25), c.Left(T0.AddDays(1)));
    }

    [Theory]
    [InlineData(1500, "25:00")]
    [InlineData(1499.4, "25:00")]
    [InlineData(245, "4:05")]
    [InlineData(0, "0:00")]
    [InlineData(3720, "1:02:00")]
    public void Format_shows_minutes_and_seconds(double seconds, string expected) =>
        Assert.Equal(expected, TimerClock.Format(TimeSpan.FromSeconds(seconds)));

    [Theory]
    [InlineData(1500, "25 мин")]
    [InlineData(1441, "25 мин")]
    [InlineData(1440, "24 мин")]
    [InlineData(61, "2 мин")]
    [InlineData(59, "<1 мин")]
    public void Short_counts_whole_minutes_up(double seconds, string expected) =>
        Assert.Equal(expected, TimerClock.Short(TimeSpan.FromSeconds(seconds)));
}

public class TodoListTests
{
    [Fact]
    public void Add_keeps_one_trimmed_line_and_skips_empty_text()
    {
        var list = new Wipriss.Modules.Todo.TodoList(new Wipriss.Modules.Todo.TodoData());
        Assert.True(list.Add("  Купить\r\nмолоко\t "));
        Assert.False(list.Add("   "));
        Assert.Single(list.Items);
        Assert.Equal("Купить молоко", list.Items[0].Text);
        Assert.True(list.Add(new string('x', 1000)));
        Assert.Equal(Wipriss.Modules.Todo.TodoList.MaxLength, list.Items[1].Text.Length);
    }

    [Fact]
    public void Toggle_crosses_out_and_back_and_remove_drops_the_item()
    {
        var list = new Wipriss.Modules.Todo.TodoList(new Wipriss.Modules.Todo.TodoData());
        list.Add("a");
        list.Add("b");
        list.Toggle(0);
        Assert.True(list.Items[0].Done);
        Assert.Equal(1, list.DoneCount);
        list.Toggle(0);
        Assert.False(list.Items[0].Done);
        list.Remove(0);
        Assert.Equal("b", Assert.Single(list.Items).Text);
        list.Remove(5); // out of range: nothing happens
        list.Toggle(-1);
        Assert.Single(list.Items);
    }
}

public class CoverColorTests
{
    private static byte[] Pixels(params (byte R, byte G, byte B, int Count)[] runs)
    {
        var list = new List<byte>();
        foreach (var (r, g, b, n) in runs)
            for (int i = 0; i < n; i++) list.AddRange([b, g, r, 255]);
        return [.. list];
    }

    [Fact]
    public void A_vivid_spot_on_a_dark_cover_wins()
    {
        // Mostly near-black, a tenth bright pink: the card should feel pink, not black.
        var c = CoverColor.Average(Pixels((10, 10, 12, 90), (230, 40, 140, 10)))!.Value;
        Assert.True(c.R > c.G * 2 && c.R > 60, $"got {c}");
    }

    [Fact]
    public void A_grey_cover_stays_grey_and_is_not_too_bright()
    {
        var c = CoverColor.Average(Pixels((200, 200, 200, 50)))!.Value;
        Assert.Equal(c.R, c.G);
        Assert.Equal(c.G, c.B);
        Assert.True(c.R <= 150);
    }

    [Fact]
    public void An_empty_picture_has_no_colour() =>
        Assert.Null(CoverColor.Average([]));
}

public class ScreenshotKeyTests
{
    [Theory]
    [InlineData("PrtSc", true)]
    [InlineData("F9", true)]
    [InlineData("Pause", true)]
    [InlineData("Ctrl+Shift+S", true)]
    [InlineData("S", false)]
    [InlineData("Shift+S", false)]
    [InlineData("Insert", false)]
    public void Only_keys_that_do_not_disturb_typing_are_allowed(string text, bool ok) =>
        Assert.Equal(ok, Hotkey.Parse(text).IsUsableForHook);

    [Fact]
    public void Named_keys_round_trip()
    {
        foreach (var name in new[] { "PrtSc", "Ctrl+PrtSc", "Alt+Pause", "Win+ScrollLock", "Ctrl+Insert", "F24" })
            Assert.Equal(name, Hotkey.Parse(name).ToString());
        Assert.Equal(Hotkey.PrtSc, Hotkey.Parse("0x2C"));
    }
}

public class HookAutoConnectTests
{
    private const string Hook = @"D:\Apps\Wipriss\Wipriss.AgentHook.exe";
    private const string Moved = @"E:\Tools\Wipriss\Wipriss.AgentHook.exe";

    [Fact]
    public void Claude_hook_with_the_same_path_needs_no_write()
    {
        string added = HookSetup.AddClaudeHook("{ \"theme\": \"dark\" }", Hook);
        Assert.True(HookSetup.ClaudeHookIsCurrent(added, Hook));
        Assert.False(HookSetup.ClaudeHookIsCurrent(added, Moved));
        Assert.False(HookSetup.ClaudeHookIsCurrent("{ \"theme\": \"dark\" }", Hook));
        Assert.False(HookSetup.ClaudeHookIsCurrent(null, Hook));
        Assert.False(HookSetup.ClaudeHookIsCurrent("{ broken", Hook));
    }

    [Fact]
    public void Moved_exe_replaces_the_old_claude_entry()
    {
        string old = HookSetup.AddClaudeHook("{ \"theme\": \"dark\" }", Hook);
        string moved = HookSetup.AddClaudeHook(old, Moved);
        Assert.True(HookSetup.ClaudeHookIsCurrent(moved, Moved));
        Assert.DoesNotContain("D:/Apps", moved);
        Assert.Contains("\"theme\": \"dark\"", moved);
    }

    [Fact]
    public void Codex_notify_with_the_same_path_needs_no_write()
    {
        string added = HookSetup.AddCodexNotify("model = \"gpt\"\n", Hook);
        Assert.True(HookSetup.CodexNotifyIsCurrent(added, Hook));
        Assert.False(HookSetup.CodexNotifyIsCurrent(added, Moved));
        Assert.True(HookSetup.CodexNotifyIsCurrent(HookSetup.AddCodexNotify(added, Moved), Moved));
        Assert.False(HookSetup.CodexNotifyIsCurrent(null, Hook));
    }
}
