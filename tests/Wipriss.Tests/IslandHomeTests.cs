using Wipriss.Core.Input;
using Wipriss.Modules.Agents;
using Wipriss.Voice;

namespace Wipriss.Tests;

public class IslandHomeTests
{
    private static string Assistant(string content, string stop = "tool_use") =>
        "{\"type\":\"assistant\",\"cwd\":\"D:\\\\Work\\\\demo\",\"timestamp\":\"2026-11-27T10:00:00Z\",\"message\":{\"role\":\"assistant\",\"stop_reason\":\""
        + stop + "\",\"content\":[" + content + "]}}";

    private static string Tool(string name, string input) => "{\"type\":\"tool_use\",\"name\":\"" + name + "\",\"input\":" + input + "}";

    [Fact]
    public void Transcript_ReadsTodoPlanAndCurrentStep()
    {
        var t = new Transcript();
        t.Feed(Assistant(Tool("TodoWrite",
            "{\"todos\":[{\"content\":\"Прочитать\",\"status\":\"completed\",\"activeForm\":\"Читаю\"},{\"content\":\"Создать каркас\",\"status\":\"in_progress\",\"activeForm\":\"Создаю каркас\"},{\"content\":\"Движок\",\"status\":\"pending\",\"activeForm\":\"x\"}]}")));

        Assert.Equal("demo", t.Project);
        Assert.Equal(Phase.Working, t.Phase);
        Assert.Equal(3, t.Steps.Count);
        Assert.Equal("Создаю каркас", t.Headline);
        Assert.Equal(StepState.Pending, t.Steps[2].State);
    }

    [Fact]
    public void Transcript_FollowsDesktopPlanAdvanceAndWrapUp()
    {
        var t = new Transcript();
        t.Feed(Assistant(Tool("mcp__ccd_turn__update_plan",
            "{\"items\":[{\"content\":\"A\",\"status\":\"in_progress\"},{\"content\":\"B\",\"status\":\"pending\"}]}")));
        t.Feed(Assistant(Tool("mcp__ccd_turn__update_plan", "{\"advance\":true}")));
        Assert.Equal(StepState.Done, t.Steps[0].State);
        Assert.Equal("B", t.Headline);

        t.Feed(Assistant(Tool("mcp__ccd_turn__wrap_up", "{\"recap\":\"x\"}")));
        t.Feed(Assistant("{\"type\":\"text\",\"text\":\"done\"}", stop: "end_turn"));
        Assert.Equal(Phase.Done, t.Phase);
        Assert.All(t.Steps, s => Assert.Equal(StepState.Done, s.State));
    }

    [Fact]
    public void Transcript_DescribesLastToolAndSkipsHelpersAndBrokenLines()
    {
        var t = new Transcript();
        t.Feed(Assistant(Tool("Read", "{\"file_path\":\"D:\\\\Work\\\\demo\\\\App.cs\"}")));
        Assert.Equal("Читает App.cs", t.Headline);

        t.Feed(Assistant(Tool("Bash", "{\"description\":\"Run tests\"}")).Replace("\"type\":\"assistant\",", "\"type\":\"assistant\",\"isSidechain\":true,"));
        t.Feed("{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"tool_use\""); // cut in the middle
        Assert.Equal("Читает App.cs", t.Headline);
    }

    [Fact]
    public void Transcript_NewPromptClearsFinishedPlan()
    {
        var t = new Transcript();
        t.Feed(Assistant(Tool("TodoWrite", "{\"todos\":[{\"content\":\"A\",\"status\":\"completed\"}]}")));
        t.Feed(Assistant("{\"type\":\"text\",\"text\":\"ok\"}", stop: "end_turn"));
        t.Feed("{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":\"Ещё одно\"}}");
        Assert.Empty(t.Steps);
        Assert.Equal(Phase.Working, t.Phase);
    }

    [Theory]
    [InlineData("Win+Alt+N", Hotkey.Win | Hotkey.Alt, 'N')]
    [InlineData("Ctrl+Shift+F5", Hotkey.Ctrl | Hotkey.Shift, 0x74)]
    [InlineData("ctrl + space", Hotkey.Ctrl, 0x20)]
    public void Hotkey_ParsesAndRoundTrips(string text, int mods, int key)
    {
        var hk = Hotkey.Parse(text);
        Assert.Equal(new Hotkey(mods, key), hk);
        Assert.Equal(hk, Hotkey.Parse(hk.ToString()));
    }

    [Fact]
    public void Hotkey_NeedsARealModifier()
    {
        Assert.False(Hotkey.Parse("Shift+A").IsUsable);
        Assert.False(Hotkey.Parse("A").IsUsable);
        Assert.False(Hotkey.Parse("").IsUsable);
        Assert.True(Hotkey.Parse("Alt+A").IsUsable);
    }

    [Fact]
    public void VoiceModels_ArePinnedHttpsWithHashesAndUniqueFolders()
    {
        Assert.Equal(VoiceModels.DefaultId, VoiceModels.All[0].Id);
        Assert.Equal(VoiceModels.All.Length, VoiceModels.All.Select(m => m.Id).Distinct().Count());
        Assert.All(VoiceModels.All.SelectMany(m => m.Files), f =>
        {
            Assert.StartsWith("https://huggingface.co/", f.Url);
            Assert.Matches("/resolve/[0-9a-f]{40}/", f.Url);
            Assert.Matches("^[0-9a-f]{64}$", f.Sha256);
            Assert.True(f.Size > 0);
        });
        Assert.Equal(VoiceModels.All[0], VoiceModels.Get("unknown"));
    }
}
