using System.IO;
using Microsoft.Win32;

namespace Wipriss.Core;

/// <summary>
/// Everything Wipriss keeps lives in one folder, by default <c>%USERPROFILE%\Wipriss</c>:
/// <list type="bullet">
/// <item><c>Скриншоты</c>: only screenshots you saved yourself;</item>
/// <item><c>Записи</c>: screen recordings (later);</item>
/// <item><c>Служебное</c>: encrypted notes, clipboard and reminders, settings, the error log.</item>
/// </list>
/// The profile root is not synced by OneDrive's folder backup, unlike Documents and Pictures.
/// The folder can be moved in settings; its place is remembered in <c>HKCU\Software\Wipriss</c>.
/// </summary>
public static class AppPaths
{
    internal const string RegistryKey = @"Software\Wipriss";
    internal const string RegistryValue = "Folder";

    public static string DefaultRoot { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Wipriss");

    public static string Root { get; } = ReadRoot();

    public static string DataDir => Path.Combine(Root, "Служебное");

    public static string SettingsFile => Path.Combine(DataDir, "settings.json");

    public static string LogsDir => Path.Combine(DataDir, "logs");

    public static string ScreenshotsDir => Path.Combine(Root, "Скриншоты");

    public static string RecordingsDir => Path.Combine(Root, "Записи");

    // Where versions before 0.2 kept things: settings roamed, data stayed in LocalAppData.
    internal static string LegacySettingsFile { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Wipriss", "settings.json");

    internal static string LegacyDataDir { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Wipriss");

    private static string ReadRoot()
    {
        // For checks of the UI on test data only: the owner's notes and clipboard are never shown or touched.
        if (Environment.GetEnvironmentVariable("WIPRISS_TEST_ROOT") is { Length: > 0 } test && Path.IsPathFullyQualified(test))
            return test;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryKey);
            if (key?.GetValue(RegistryValue) is string path && Path.IsPathFullyQualified(path)) return path;
        }
        catch (Exception)
        {
            // Unreadable value: fall back to the default place.
        }

        return DefaultRoot;
    }
}
