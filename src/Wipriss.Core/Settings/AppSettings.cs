using System.Text.Json.Serialization;

namespace Wipriss.Core.Settings;

public sealed class AppSettings
{
    /// <summary>Hide the island while a game or video runs full screen.</summary>
    public bool HideInFullscreen { get; set; } = true;

    /// <summary>Module id → switched on. Missing ids are on by default.</summary>
    public Dictionary<string, bool> Modules { get; set; } = [];

    /// <summary>Module id → hotkey text like "Win+Alt+N". Missing = the module's default, "" = no hotkey.</summary>
    public Dictionary<string, string> Hotkeys { get; set; } = [];

    /// <summary>Kinds of pill blocks the user switched off (<see cref="Modules.PillSlot.Kind"/>, e.g. "load.gpu").</summary>
    public List<string> HiddenInPill { get; set; } = [];

    public bool IsModuleEnabled(string id) => !Modules.TryGetValue(id, out var on) || on;

    public bool IsShownInPill(string kind) => !HiddenInPill.Contains(kind);

    public string HotkeyFor(string id, string fallback) => Hotkeys.TryGetValue(id, out var key) ? key : fallback;
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext;
