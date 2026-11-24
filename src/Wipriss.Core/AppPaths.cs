using System.IO;
namespace Wipriss.Core;

/// <summary>Where Wipriss keeps its files. Settings roam with the user; data and logs stay on this PC.</summary>
public static class AppPaths
{
    public static string RoamingDir { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Wipriss");

    public static string LocalDir { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Wipriss");

    public static string SettingsFile => Path.Combine(RoamingDir, "settings.json");

    public static string LogsDir => Path.Combine(LocalDir, "logs");
}
