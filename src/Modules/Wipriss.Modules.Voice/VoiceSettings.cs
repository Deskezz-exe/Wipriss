using System.IO;
using System.Text.Json.Serialization;
using Wipriss.Core;
using Wipriss.Core.Storage;

namespace Wipriss.Modules.Voice;

public sealed class VoiceSettings
{
    /// <summary>Dictation by the hotkey (works only once the model is downloaded).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>The hotkey: 1 to 3 virtual-key codes pressed together.</summary>
    public List<int> Keys { get; set; } = [.. HotkeyKeys.Default];

    /// <summary>Keep the model in memory (instant) or unload it after <see cref="UnloadMinutes"/>.</summary>
    public bool KeepLoaded { get; set; }

    public int UnloadMinutes { get; set; } = 10;

    public static readonly int[] UnloadChoices = [5, 10, 30, 60];

    private static string FilePath => Path.Combine(AppPaths.DataDir, "voice.dat");

    public static VoiceSettings Load()
    {
        var settings = SecureStore.ReadJson(FilePath, VoiceJsonSettings.Default.VoiceSettings) ?? new VoiceSettings();
        if (!HotkeyKeys.IsValid(settings.Keys)) settings.Keys = [.. HotkeyKeys.Default];
        if (!UnloadChoices.Contains(settings.UnloadMinutes)) settings.UnloadMinutes = 10;
        return settings;
    }

    public void Save() => SecureStore.WriteJson(FilePath, this, VoiceJsonSettings.Default.VoiceSettings);
}

[JsonSerializable(typeof(VoiceSettings))]
internal sealed partial class VoiceJsonSettings : JsonSerializerContext;

/// <summary>Names and rules for hotkeys made of 1–3 keys.</summary>
public static class HotkeyKeys
{
    public const int Shift = 0x10, Ctrl = 0x11, Alt = 0x12, Win = 0x5B, Space = 0x20, Escape = 0x1B;
    public const int LShift = 0xA0, RShift = 0xA1, LCtrl = 0xA2, RCtrl = 0xA3, LAlt = 0xA4, RAlt = 0xA5, RWin = 0x5C;

    public static readonly int[] Default = [Ctrl, Alt, Space];

    public static bool IsModifier(int vk) => Generic(vk) is Shift or Ctrl or Alt or Win;

    /// <summary>Left and right Ctrl/Alt/Shift/Win become one key; everything else stays.</summary>
    public static int Generic(int vk) => vk switch
    {
        LShift or RShift => Shift,
        LCtrl or RCtrl => Ctrl,
        LAlt or RAlt => Alt,
        RWin => Win,
        _ => vk,
    };

    /// <summary>Does the pressed key <paramref name="down"/> count as the hotkey key <paramref name="wanted"/>?</summary>
    public static bool Matches(int wanted, int down) => wanted == down || wanted == Generic(down);

    /// <summary>
    /// What a captured set of keys becomes. One key keeps its side ("Правый Ctrl" is a popular dictation key);
    /// in a combination the side does not matter. Modifiers go first, as people write them.
    /// </summary>
    public static List<int> FromCaptured(IReadOnlyCollection<int> pressed)
    {
        if (pressed.Count == 1) return [.. pressed];
        return pressed.Select(Generic).Distinct().OrderBy(Order).ToList();
    }

    public static bool IsValid(IReadOnlyCollection<int>? keys) =>
        keys is { Count: >= 1 and <= 3 } && keys.All(k => k is > 0 and < 0xFF && k != Escape) && keys.Distinct().Count() == keys.Count;

    public static string Describe(IEnumerable<int> keys) => string.Join(" + ", keys.Select(Name));

    public static string Name(int vk) => vk switch
    {
        Ctrl => "Ctrl",
        Alt => "Alt",
        Shift => "Shift",
        Win => "Win",
        LCtrl => "Левый Ctrl",
        RCtrl => "Правый Ctrl",
        LAlt => "Левый Alt",
        RAlt => "Правый Alt",
        LShift => "Левый Shift",
        RShift => "Правый Shift",
        RWin => "Правый Win",
        Space => "Пробел",
        0x08 => "Backspace",
        0x09 => "Tab",
        0x0D => "Enter",
        0x13 => "Pause",
        0x14 => "Caps Lock",
        0x21 => "Page Up",
        0x22 => "Page Down",
        0x23 => "End",
        0x24 => "Home",
        0x25 => "←",
        0x26 => "↑",
        0x27 => "→",
        0x28 => "↓",
        0x2C => "PrtSc",
        0x2D => "Insert",
        0x2E => "Delete",
        0x5D => "Меню",
        0x91 => "Scroll Lock",
        0xC0 => "Ё",
        >= 0x30 and <= 0x39 => ((char)vk).ToString(),
        >= 0x41 and <= 0x5A => ((char)vk).ToString(),
        >= 0x60 and <= 0x69 => "Num " + (vk - 0x60),
        >= 0x70 and <= 0x87 => "F" + (vk - 0x6F),
        _ => $"Клавиша {vk:X2}",
    };

    private static int Order(int vk) => vk switch { Ctrl => 0, Win => 1, Alt => 2, Shift => 3, _ => 4 };
}
