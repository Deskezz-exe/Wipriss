using Wipriss.Modules.Voice;
using Xunit;
using V = Wipriss.Modules.Voice.HotkeyKeys;

namespace Wipriss.Tests;

public sealed class VoiceHotkeyTests
{
    private const int KeyV = 0x56, F9 = 0x78;

    private readonly HashSet<int> _held = [];
    private readonly HotkeyMatcher _matcher;
    private int _pressed, _cancelled;
    private List<int>? _captured;
    private bool _captureEnded;

    public VoiceHotkeyTests()
    {
        _matcher = new HotkeyMatcher(vk => _held.Contains(vk));
        _matcher.Pressed += () => _pressed++;
        _matcher.Cancelled += () => _cancelled++;
        _matcher.Captured += keys => { _captured = keys; _captureEnded = true; };
    }

    private HotkeyMatcher.Verdict Down(int vk)
    {
        _held.Add(vk);
        return _matcher.Down(vk);
    }

    private bool Up(int vk)
    {
        _held.Remove(vk);
        return _matcher.Up(vk);
    }

    [Fact]
    public void Combination_FiresOnceAndKeepsOnlyTheLastKeyFromPrograms()
    {
        _matcher.Keys = V.Default; // Ctrl + Alt + Space
        Assert.Equal(HotkeyMatcher.Verdict.Pass, Down(V.LCtrl));
        Assert.Equal(HotkeyMatcher.Verdict.Pass, Down(V.RAlt)); // either side counts in a combination
        Assert.Equal(HotkeyMatcher.Verdict.Swallow, Down(V.Space));
        Assert.Equal(1, _pressed);

        Assert.Equal(HotkeyMatcher.Verdict.Swallow, Down(V.Space)); // auto-repeat: no second press
        Assert.Equal(1, _pressed);
        Assert.True(Up(V.Space));
        Assert.False(Up(V.RAlt));
        Assert.False(Up(V.LCtrl));
    }

    [Fact]
    public void Combination_WithExtraKeysOrMissingKeysDoesNotFire()
    {
        _matcher.Keys = V.Default;
        Assert.Equal(HotkeyMatcher.Verdict.Pass, Down(V.Space)); // plain Space types as usual
        Up(V.Space);
        Down(V.LCtrl); Down(V.LShift); Down(V.LAlt);
        Assert.Equal(HotkeyMatcher.Verdict.Pass, Down(V.Space)); // Ctrl+Shift+Alt+Space is another shortcut
        Assert.Equal(0, _pressed);
    }

    [Fact]
    public void SingleKey_KeepsItsSide()
    {
        _matcher.Keys = [V.RCtrl];
        Assert.Equal(HotkeyMatcher.Verdict.Pass, Down(V.LCtrl));
        Up(V.LCtrl);
        Assert.Equal(0, _pressed);

        Assert.Equal(HotkeyMatcher.Verdict.Swallow, Down(V.RCtrl));
        Assert.True(Up(V.RCtrl));
        Assert.Equal(1, _pressed);

        // Left Ctrl + C still copies.
        Assert.Equal(HotkeyMatcher.Verdict.Pass, Down(V.LCtrl));
        Assert.Equal(HotkeyMatcher.Verdict.Pass, Down(0x43));
    }

    [Fact]
    public void TwoKeys_Work()
    {
        _matcher.Keys = [V.Win, F9];
        Down(V.Win);
        Assert.Equal(HotkeyMatcher.Verdict.Swallow, Down(F9));
        Assert.Equal(1, _pressed);
        Assert.True(_matcher.AltOrWinDown); // the hook masks Win so Start does not open
    }

    [Fact]
    public void LostKeyUp_IsForgotten()
    {
        _matcher.Keys = [F9];
        Down(V.LCtrl);
        _held.Remove(V.LCtrl); // its key-up never came (lock screen)
        Assert.Equal(HotkeyMatcher.Verdict.Swallow, Down(F9));
        Assert.Equal(1, _pressed);
    }

    [Fact]
    public void Escape_CancelsOnlyWhileListening()
    {
        _matcher.Keys = [F9];
        Assert.Equal(HotkeyMatcher.Verdict.Pass, Down(V.Escape));
        Assert.False(Up(V.Escape));

        _matcher.Listening = true;
        Assert.Equal(HotkeyMatcher.Verdict.Swallow, Down(V.Escape));
        Assert.True(Up(V.Escape));
        Assert.Equal(1, _cancelled);
    }

    [Fact]
    public void Capture_TakesUpToThreeKeysAndSortsModifiersFirst()
    {
        _matcher.BeginCapture();
        Assert.Equal(HotkeyMatcher.Verdict.Swallow, Down(V.Space));
        Assert.Equal(HotkeyMatcher.Verdict.Swallow, Down(V.RShift));
        Assert.Equal(HotkeyMatcher.Verdict.Swallow, Down(V.LCtrl));
        Down(KeyV); // a fourth key is ignored
        Assert.True(Up(V.Space));
        Assert.False(_captureEnded);
        Up(V.RShift); Up(KeyV);
        Assert.True(Up(V.LCtrl));

        Assert.Equal([V.Ctrl, V.Shift, V.Space], _captured);
        Assert.False(_matcher.Capturing);
        Assert.Equal("Ctrl + Shift + Пробел", V.Describe(_captured!));
    }

    [Fact]
    public void Capture_OneKeyKeepsItsSide_EscCancels()
    {
        _matcher.BeginCapture();
        Down(V.RCtrl); Up(V.RCtrl);
        Assert.Equal([V.RCtrl], _captured);
        Assert.Equal("Правый Ctrl", V.Describe(_captured!));

        _captureEnded = false;
        _matcher.BeginCapture();
        Assert.Equal(HotkeyMatcher.Verdict.Swallow, Down(V.Escape));
        Assert.True(_captureEnded);
        Assert.Null(_captured);
        Assert.True(Up(V.Escape)); // its key-up does not reach the program either
    }

    [Theory]
    [InlineData(new int[0], false)]
    [InlineData(new[] { 0x11, 0x12, 0x20, 0x41 }, false)]
    [InlineData(new[] { 0x1B }, false)]
    [InlineData(new[] { 0x11, 0x11 }, false)]
    [InlineData(new[] { 0xA3 }, true)]
    [InlineData(new[] { 0x11, 0x12, 0x20 }, true)]
    public void Keys_AreValidated(int[] keys, bool valid) => Assert.Equal(valid, V.IsValid(keys));

    [Fact]
    public void Keys_HaveRussianNames()
    {
        Assert.Equal("Ctrl + Alt + Пробел", V.Describe(V.Default));
        Assert.Equal("F9", V.Name(F9));
        Assert.Equal("Правый Alt", V.Name(V.RAlt));
    }
}
