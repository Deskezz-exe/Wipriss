using System.Diagnostics;
using System.IO;
using System.Text.Json.Serialization;
using Microsoft.Win32;
using Wipriss.Core;
using Wipriss.Core.Storage;

namespace Wipriss.Modules.Capture;

public sealed class CaptureSettings
{
    /// <summary>The user wants PrtSc to open Wipriss instead of Snipping Tool.</summary>
    public bool OwnPrintScreen { get; set; }
}

[JsonSerializable(typeof(CaptureSettings))]
internal sealed partial class CaptureJson : JsonSerializerContext;

/// <summary>
/// Who gets the PrtSc key: Wipriss or Windows' Snipping Tool. Windows 11 sends PrtSc to Snipping Tool while
/// "Use the Print screen key to open screen capture" is on (HKCU\Control Panel\Keyboard\PrintScreenKeyForSnippingEnabled).
/// Explorer reads that setting when it starts, so a change applies after restarting Explorer or the PC.
/// While the user's choice is "Wipriss", it is checked at every start, so a Windows update can't quietly switch it back.
/// </summary>
internal sealed class PrintScreenKey
{
    private const string KeyPath = @"Control Panel\Keyboard";
    private const string ValueName = "PrintScreenKeyForSnippingEnabled";
    private static readonly string FilePath = Path.Combine(AppPaths.DataDir, "capture.dat");

    private CaptureSettings _settings;

    public PrintScreenKey()
    {
        _settings = SecureStore.ReadJson(FilePath, CaptureJson.Default.CaptureSettings)
            // First run: follow what Windows does now.
            ?? new CaptureSettings { OwnPrintScreen = !SnippingSettingOn };
    }

    /// <summary>The user's choice.</summary>
    public bool OwnedByWipriss => _settings.OwnPrintScreen;

    /// <summary>
    /// The Windows setting changed after Explorer started, so Explorer still works with the old one.
    /// The registry key's own last-write time tells when, whoever changed it.
    /// </summary>
    public bool WaitingForExplorer => SettingChangedUtc is { } changed && ExplorerStartedUtc is { } started && started < changed;

    /// <summary>Makes the Windows setting match the user's choice. Called at start and when the switch moves.</summary>
    public void Apply()
    {
        bool snippingWanted = !_settings.OwnPrintScreen;
        if (SnippingSettingOn == snippingWanted) return;

        using (var key = Registry.CurrentUser.CreateSubKey(KeyPath))
            key.SetValue(ValueName, snippingWanted ? 1 : 0, RegistryValueKind.DWord);
    }

    public void Choose(bool wipriss)
    {
        _settings.OwnPrintScreen = wipriss;
        Save();
        Apply();
    }

    /// <summary>
    /// Restarts Windows Explorer so it reads the new setting now. Only on the user's click: the taskbar disappears
    /// for a moment and open folder windows close. Windows usually restarts Explorer by itself; if not, we start it.
    /// </summary>
    public static async Task RestartExplorerAsync()
    {
        int session = Process.GetCurrentProcess().SessionId;
        foreach (var explorer in Process.GetProcessesByName("explorer").Where(p => p.SessionId == session))
        {
            using (explorer)
            {
                try
                {
                    explorer.Kill();
                    await explorer.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch (Exception ex)
                {
                    ErrorLog.Write(ex);
                }
            }
        }

        for (int i = 0; i < 30; i++)
        {
            await Task.Delay(100);
            if (Process.GetProcessesByName("explorer").Any(p => p.SessionId == session)) return;
        }

        Process.Start(new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"))
        {
            UseShellExecute = true,
        });
    }

    /// <summary>True when Windows sends PrtSc to Snipping Tool. A missing value means on (the Windows 11 default).</summary>
    private static bool SnippingSettingOn
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
            return key?.GetValue(ValueName) is not int value || value != 0;
        }
    }

    private static DateTime? ExplorerStartedUtc
    {
        get
        {
            int session = Process.GetCurrentProcess().SessionId;
            DateTime? shell = null;
            foreach (var explorer in Process.GetProcessesByName("explorer"))
            {
                using (explorer)
                {
                    try
                    {
                        if (explorer.SessionId != session) continue;
                        var started = explorer.StartTime.ToUniversalTime();
                        if (shell is null || started < shell) shell = started; // the shell is the oldest one
                    }
                    catch (Exception)
                    {
                        // gone or not readable
                    }
                }
            }

            return shell;
        }
    }

    private static DateTime? SettingChangedUtc
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
            if (key is null) return null;
            int error = RegQueryInfoKeyW(key.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, out long written);
            return error == 0 ? DateTime.FromFileTimeUtc(written) : null;
        }
    }

    [System.Runtime.InteropServices.DllImport("advapi32.dll")]
    private static extern int RegQueryInfoKeyW(Microsoft.Win32.SafeHandles.SafeRegistryHandle key, IntPtr className, IntPtr classLength,
        IntPtr reserved, IntPtr subKeys, IntPtr maxSubKeyLength, IntPtr maxClassLength, IntPtr values, IntPtr maxValueNameLength,
        IntPtr maxValueLength, IntPtr securityDescriptor, out long lastWriteTime);

    private void Save() => SecureStore.WriteJson(FilePath, _settings, CaptureJson.Default.CaptureSettings);
}
