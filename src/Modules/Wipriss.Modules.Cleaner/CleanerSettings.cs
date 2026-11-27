using System.IO;
using System.Text.Json.Serialization;
using Wipriss.Core;
using Wipriss.Core.Storage;

namespace Wipriss.Modules.Cleaner;

public sealed class CleanerSettings
{
    /// <summary>Paths the owner asked never to show again (with everything under them).</summary>
    public List<string> Hidden { get; set; } = [];

    /// <summary>The last search: when, and how much was green (for the tile).</summary>
    public DateTime? LastScan { get; set; }
    public long LastGreenBytes { get; set; }

    private static string FilePath => Path.Combine(AppPaths.DataDir, "cleaner.dat");

    public static CleanerSettings Load() =>
        SecureStore.ReadJson(FilePath, CleanerJsonSettings.Default.CleanerSettings) ?? new CleanerSettings();

    public void Save() => SecureStore.WriteJson(FilePath, this, CleanerJsonSettings.Default.CleanerSettings);
}

[JsonSerializable(typeof(CleanerSettings))]
internal sealed partial class CleanerJsonSettings : JsonSerializerContext;
