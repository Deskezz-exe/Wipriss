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
/// The user's choice of who gets PrtSc, and the matching Windows 11 setting
/// "Use the Print screen key to open screen capture" (HKCU\Control Panel\Keyboard\PrintScreenKeyForSnippingEnabled).
/// Windows reads that setting only at sign-in, which is why <see cref="PrintScreenHook"/> does the immediate work;
/// the setting makes it last and covers windows running as administrator after the next sign-in.
/// While the choice is "Wipriss", the setting is checked at every start, so a Windows update can't switch it back.
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

    /// <summary>True when Windows sends PrtSc to Snipping Tool. A missing value means on (the Windows 11 default).</summary>
    private static bool SnippingSettingOn
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
            return key?.GetValue(ValueName) is not int value || value != 0;
        }
    }

    private void Save() => SecureStore.WriteJson(FilePath, _settings, CaptureJson.Default.CaptureSettings);
}
