using System.Text.Json.Serialization;

namespace Wipriss.Core.Settings;

public sealed class AppSettings
{
    /// <summary>Hide the island while a game or video runs full screen.</summary>
    public bool HideInFullscreen { get; set; } = true;

    /// <summary>Module id → switched on. Missing ids are on by default.</summary>
    public Dictionary<string, bool> Modules { get; set; } = [];

    public bool IsModuleEnabled(string id) => !Modules.TryGetValue(id, out var on) || on;
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext;
