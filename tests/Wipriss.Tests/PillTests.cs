using Wipriss.Core.Modules;
using Wipriss.Modules.Agents;
using Wipriss.Modules.Batteries;
using Wipriss.Modules.Load;

namespace Wipriss.Tests;

public class PillLayoutTests
{
    private static PillLayout.Item I(PillGroup g, int p, double w) => new(g, p, w);

    [Fact]
    public void Everything_fits_when_there_is_room()
    {
        var items = new[] { I(PillGroup.Agents, 100, 30), I(PillGroup.Load, 50, 60), I(PillGroup.Load, 40, 60) };
        var keep = PillLayout.Fit(items, 500, 10, 21);
        Assert.All(keep, Assert.True);
        // 30 + 21 (group) + 60 + 10 (same group) + 60
        Assert.Equal(181, PillLayout.Width(items, keep, 10, 21));
    }

    [Fact]
    public void Lowest_priority_goes_first_and_whole()
    {
        var items = new[]
        {
            I(PillGroup.Media, 60, 200), I(PillGroup.Load, 50, 60), I(PillGroup.Load, 35, 60), I(PillGroup.Load, 30, 110), I(PillGroup.Battery, 55, 80),
        };
        // 200 + 21 + 60 + 21 + 80 = 382 fits into 390 once the two lowest are gone.
        var keep = PillLayout.Fit(items, 390, 10, 21);
        Assert.Equal([true, true, false, false, true], keep);
        Assert.Equal(382, PillLayout.Width(items, keep, 10, 21));
    }

    [Fact]
    public void On_a_tie_the_one_further_right_goes()
    {
        var items = new[] { I(PillGroup.Battery, 55, 80), I(PillGroup.Battery, 55, 80) };
        Assert.Equal([true, false], PillLayout.Fit(items, 100, 10, 21));
    }

    [Fact]
    public void Nothing_fits_gives_an_empty_pill()
    {
        var items = new[] { I(PillGroup.Media, 60, 900) };
        Assert.Equal([false], PillLayout.Fit(items, 500, 10, 21));
        Assert.Equal(0, PillLayout.Width(items, [false], 10, 21));
    }
}

public class LoadMathTests
{
    [Fact]
    public void Cpu_share_counts_idle_inside_kernel_time()
    {
        // 1000 units passed: kernel 600 (of it idle 500), user 400 → busy 500 of 1000.
        Assert.Equal(50, LoadMath.CpuPercent(0, 0, 0, 500, 600, 400), 3);
        Assert.Equal(0, LoadMath.CpuPercent(10, 10, 10, 10, 10, 10));
    }

    [Fact]
    public void Gpu_is_the_busiest_engine_summed_over_processes()
    {
        var items = new[]
        {
            ("pid_1_luid_0x00000000_0x0000D1B6_phys_0_eng_0_engtype_3D", 20.0),
            ("pid_2_luid_0x00000000_0x0000D1B6_phys_0_eng_0_engtype_3D", 15.0),
            ("pid_2_luid_0x00000000_0x0000D1B6_phys_0_eng_3_engtype_VideoDecode", 30.0),
            ("pid_3_luid_0x00000000_0x0000D1B6_phys_0_eng_1_engtype_Copy", double.NaN),
        };
        Assert.Equal(35, LoadMath.GpuPercent(items), 3);
        Assert.Equal(0, LoadMath.GpuPercent([]));
        Assert.Equal(100, LoadMath.GpuPercent([("luid_0x1_phys_0_eng_0_engtype_3D", 160.0)]));
    }

    [Theory]
    [InlineData(0, "0K")]
    [InlineData(870_000, "850K")]
    [InlineData(1_258_291, "1.2M")]
    [InlineData(12_582_912, "12M")]
    [InlineData(1_181_116_006, "1.1G")]
    public void Speed_is_short(double bytes, string text) => Assert.Equal(text, LoadMath.Speed(bytes));

    [Fact]
    public void Percent_is_rounded_and_capped() => Assert.Equal(["0%", "13%", "100%"], new[] { -3, 12.6, 140 }.Select(LoadMath.Percent));
}

public class HookProtocolTests
{
    [Fact]
    public void Claude_permission_prompt_means_waiting()
    {
        var m = HookProtocol.FromClaude("\uFEFF{\"hook_event_name\":\"Notification\",\"notification_type\":\"permission_prompt\",\"session_id\":\"abc\",\"transcript_path\":\"D:\\\\Profile\\\\.claude\\\\projects\\\\p\\\\abc.jsonl\",\"cwd\":\"D:\\\\Work\\\\demo\"}");
        Assert.NotNull(m);
        Assert.Equal(("claude", HookKind.Waiting, "abc", "demo"), (m!.Agent, m.Kind, m.Session, m.Project));
    }

    [Theory]
    [InlineData("{\"hook_event_name\":\"Notification\",\"notification_type\":\"idle_prompt\"}", HookKind.Done)]
    [InlineData("{\"hook_event_name\":\"Notification\",\"message\":\"Claude needs your permission to use Bash\"}", HookKind.Waiting)]
    [InlineData("{\"hook_event_name\":\"PreToolUse\"}", HookKind.Other)]
    public void Claude_events(string json, HookKind kind) => Assert.Equal(kind, HookProtocol.FromClaude(json)!.Kind);

    [Fact]
    public void Broken_input_is_ignored()
    {
        Assert.Null(HookProtocol.FromClaude("not json"));
        Assert.Null(HookProtocol.FromCodex("[1,2]"));
        Assert.Null(HookProtocol.Read("{\"agent\":\"other\",\"kind\":\"Waiting\"}"));
        Assert.Null(HookProtocol.Read("{\"agent\":\"claude\",\"kind\":\"Explode\"}"));
    }

    [Fact]
    public void Codex_turn_complete_means_done()
    {
        var m = HookProtocol.FromCodex("{\"type\":\"agent-turn-complete\",\"thread-id\":\"t1\",\"cwd\":\"/work/proj\"}")!;
        Assert.Equal(("codex", HookKind.Done, "t1", "proj"), (m.Agent, m.Kind, m.Session, m.Project));
        Assert.Equal(HookKind.Waiting, HookProtocol.FromCodex("{\"type\":\"approval-requested\"}")!.Kind);
    }

    [Fact]
    public void A_message_survives_the_pipe_and_long_text_is_cut()
    {
        var m = new HookMessage("claude", HookKind.Waiting, new string('s', 1000), "", "proj");
        var back = HookProtocol.Read(HookProtocol.Write(m))!;
        Assert.Equal(HookKind.Waiting, back.Kind);
        Assert.True(back.Session.Length <= 400);
    }
}

public class HookSetupTests
{
    private const string Hook = @"D:\Apps\Wipriss\Wipriss.AgentHook.exe";

    [Fact]
    public void Claude_hook_is_added_next_to_the_users_own_and_removed_cleanly()
    {
        const string original = """
            {
              "model": "opus",
              "hooks": {
                "Notification": [ { "hooks": [ { "type": "command", "command": "my-notify.sh" } ] } ],
                "Stop": [ { "hooks": [ { "type": "command", "command": "beep" } ] } ]
              }
            }
            """;
        string added = HookSetup.AddClaudeHook(original, Hook);
        Assert.True(HookSetup.HasClaudeHook(added));
        Assert.Contains("my-notify.sh", added);
        Assert.Contains("\"D:/Apps/Wipriss/Wipriss.AgentHook.exe\" claude", added.Replace("\\\"", "\""));
        // Adding twice keeps one copy.
        string twice = HookSetup.AddClaudeHook(added, Hook);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(twice, "AgentHook"));

        string removed = HookSetup.RemoveClaudeHook(twice);
        Assert.False(HookSetup.HasClaudeHook(removed));
        Assert.Contains("my-notify.sh", removed);
        Assert.Contains("beep", removed);
        Assert.Contains("opus", removed);
    }

    [Fact]
    public void Claude_hook_in_an_empty_file_and_tidy_removal()
    {
        string added = HookSetup.AddClaudeHook(null, Hook);
        Assert.True(HookSetup.HasClaudeHook(added));
        string removed = HookSetup.RemoveClaudeHook(added);
        Assert.DoesNotContain("hooks", removed);
    }

    [Fact]
    public void Broken_claude_settings_are_not_touched()
    {
        Assert.Throws<FormatException>(() => HookSetup.AddClaudeHook("{ \"hooks\": ", Hook));
        Assert.Throws<FormatException>(() => HookSetup.AddClaudeHook("{ \"hooks\": [1] }", Hook));
        Assert.False(HookSetup.HasClaudeHook("{ broken"));
    }

    [Fact]
    public void Codex_notify_goes_before_the_first_table_and_comes_out_again()
    {
        const string original = "model = \"o4\"\n\n[profiles.fast]\nmodel = \"mini\"\n";
        string added = HookSetup.AddCodexNotify(original, Hook);
        Assert.True(HookSetup.HasCodexNotify(added));
        Assert.True(added.IndexOf("notify", StringComparison.Ordinal) < added.IndexOf("[profiles", StringComparison.Ordinal));
        Assert.Contains($"notify = ['{Hook}', 'codex']", added);
        Assert.Equal(original, HookSetup.RemoveCodexNotify(added));
        // Adding twice keeps one line.
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(HookSetup.AddCodexNotify(added, Hook), "notify ="));
    }

    [Fact]
    public void Codex_notify_in_an_empty_or_missing_file()
    {
        string added = HookSetup.AddCodexNotify(null, Hook);
        Assert.StartsWith("# Wipriss", added);
        Assert.Equal("", HookSetup.RemoveCodexNotify(added));
    }

    [Fact]
    public void Users_own_codex_notify_is_left_alone()
    {
        Assert.Throws<InvalidOperationException>(() => HookSetup.AddCodexNotify("notify = [\"my-tool\"]\n", Hook));
        // A key in a table is not the top-level notify.
        Assert.True(HookSetup.HasCodexNotify(HookSetup.AddCodexNotify("[tui]\nnotifications = true\n", Hook)));
    }
}

public class BatteryProtocolTests
{
    [Fact]
    public void Logitech_request_and_reply()
    {
        var r = LogitechHidpp.Request(2, 0x06, 1, 0x0B);
        Assert.Equal(20, r.Length);
        Assert.Equal(new byte[] { 0x11, 0x02, 0x06, 0x1B }, r[..4]);
        Assert.Equal(new byte[] { 0x11, 0x01, 0x00, 0x0A, 0x10, 0x04 }, LogitechHidpp.FindFeature(1, 0x1004, 0x0A)[..6]);

        byte[] reply = [0x11, 0x02, 0x06, 0x1B, 85, 0x04, 0x01, 0x00];
        Assert.True(LogitechHidpp.IsReply(reply, 2, 0x06, 1, 0x0B));
        Assert.False(LogitechHidpp.IsReply(reply, 3, 0x06, 1, 0x0B));
        Assert.True(LogitechHidpp.IsError([0x10, 0x02, 0x8F, 0x06, 0x1B, 0x09, 0x00], 2));
    }

    [Fact]
    public void Logitech_battery_features()
    {
        Assert.Equal(new Charge(85, true), LogitechHidpp.ParseUnified([85, 0x04, 0x01, 0x00]));
        Assert.Equal(new Charge(50, false), LogitechHidpp.ParseUnified([0, 0x04, 0x00, 0x00])); // level flags only
        Assert.Null(LogitechHidpp.ParseUnified([0, 0, 0]));
        Assert.Equal(new Charge(40, false), LogitechHidpp.ParseStatus([40, 20, 0]));
        Assert.Equal(new Charge(100, true), LogitechHidpp.ParseVoltage([0x10, 0x68, 0x80])); // 4200 mV
        Assert.Equal(0, LogitechHidpp.VoltageToPercent(3300));
        Assert.Equal(48, LogitechHidpp.VoltageToPercent(3800));
        Assert.Equal(DeviceKind.Mouse, LogitechHidpp.KindOf(3));
        Assert.Equal(DeviceKind.Keyboard, LogitechHidpp.KindOf(0));
        Assert.Equal(DeviceKind.Headset, LogitechHidpp.KindOf(8));
    }

    [Fact]
    public void Razer_request_has_its_checksum_and_reply_gives_the_level()
    {
        var r = RazerProtocol.Request(0x1F, RazerProtocol.LevelCommand);
        Assert.Equal(90, r.Length);
        Assert.Equal((byte)(0x02 ^ 0x07 ^ 0x80), r[88]);
        var reply = new byte[90];
        reply[0] = 0x02; reply[6] = 0x07; reply[7] = 0x80; reply[9] = 255;
        Assert.Equal((byte)255, RazerProtocol.Value(reply, 0x80));
        Assert.Null(RazerProtocol.Value(reply, 0x84));
        Assert.Equal(100, RazerProtocol.LevelToPercent(255));
        Assert.Equal(50, RazerProtocol.LevelToPercent(128));
    }

    [Fact]
    public void Headset_protocols()
    {
        Assert.Equal(new byte[] { 0x06, 0xFF, 0xBB, 0x02 }, HyperXProtocol.Request(2)[..4]);
        byte[] level = [0x06, 0xFF, 0xBB, 0x02, 0x00, 0x0F, 0xA0, 77];
        Assert.Equal(77, HyperXProtocol.Percent(level));
        Assert.True(HyperXProtocol.Charging([0x06, 0xFF, 0xBB, 0x03, 0x01, 0, 0, 0]));

        Assert.Equal(new Charge(64, true), SteelSeriesProtocol.Parse([0xB0, 0x03, 64, 0x01]));
        Assert.Null(SteelSeriesProtocol.Parse([0xB1, 0x03, 64, 0x01]));

        Assert.Equal(87, CorsairProtocol.Percent([0x02, 0x09, 0x02, 0x0F, 0x66, 0x03])); // 870 tenths
        Assert.Null(CorsairProtocol.Percent([0x02, 0x09, 0x02, 0x0F, 0xFF, 0xFF]));
    }

    [Fact]
    public void Bluetooth_kinds()
    {
        Assert.Equal(DeviceKind.Mouse, BluetoothKinds.FromClassOfDevice(0x002580));
        Assert.Equal(DeviceKind.Keyboard, BluetoothKinds.FromClassOfDevice(0x002540));
        Assert.Equal(DeviceKind.Headset, BluetoothKinds.FromClassOfDevice(0x240404));
        Assert.Null(BluetoothKinds.FromClassOfDevice(0x5A020C)); // a phone
        Assert.Equal(DeviceKind.Mouse, BluetoothKinds.FromAppearance(0x03C2));
        Assert.Equal(DeviceKind.Keyboard, BluetoothKinds.FromAppearance(0x03C1));
    }
}
