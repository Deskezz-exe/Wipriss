using System.IO;
using Microsoft.Win32;

namespace Wipriss.App.Shell;

/// <summary>"Start with Windows" through the per-user Run key. No admin rights needed.</summary>
internal static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Wipriss";

    private static string ExePath => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "Wipriss.exe");

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string command
                   && command.Contains(ExePath, StringComparison.OrdinalIgnoreCase);
        }
    }

    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue(ValueName, $"\"{ExePath}\" --autostart");
        else key.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
