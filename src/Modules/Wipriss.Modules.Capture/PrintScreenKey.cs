using System.IO;
using System.Text.Json.Serialization;
using Microsoft.Win32;
using Wipriss.Core;
using Wipriss.Core.Input;
using Wipriss.Core.Storage;

namespace Wipriss.Modules.Capture;

public sealed class CaptureSettings
{
    /// <summary>Kept from older versions; the key below decides now.</summary>
    public bool OwnPrintScreen { get; set; }

    /// <summary>The screenshot key as text (e.g. "PrtSc", "Ctrl+Shift+S"). Empty or missing means PrtSc.</summary>
    public string? Key { get; set; }
}

[JsonSerializable(typeof(CaptureSettings))]
internal sealed partial class CaptureJson : JsonSerializerContext;

/// <summary>
/// The screenshot key, PrtSc by default, and the matching Windows 11 setting
/// "Use the Print screen key to open screen capture" (HKCU\Control Panel\Keyboard\PrintScreenKeyForSnippingEnabled).
/// Windows reads that setting only at sign-in, which is why <see cref="PrintScreenHook"/> does the immediate work;
/// the setting makes it last and covers windows running as administrator after the next sign-in.
/// While Wipriss has PrtSc, the setting is checked at every start, so a Windows update can't switch it back; with another
/// key (or the module off) PrtSc goes back to Snipping Tool.
/// </summary>
internal sealed class PrintScreenKey
{
    private const string KeyPath = @"Control Panel\Keyboard";
    private const string ValueName = "PrintScreenKeyForSnippingEnabled";
    private static readonly string FilePath = Path.Combine(AppPaths.DataDir, "capture.dat");

    private readonly CaptureSettings _settings;

    public PrintScreenKey()
    {
        _settings = SecureStore.ReadJson(FilePath, CaptureJson.Default.CaptureSettings) ?? new CaptureSettings();
    }

    /// <summary>The screenshot key; PrtSc unless the user chose another.</summary>
    public Hotkey Key
    {
        get
        {
            var key = Hotkey.Parse(_settings.Key);
            return key.IsUsableForHook ? key : Hotkey.PrtSc;
        }
    }

    /// <summary>Wipriss holds PrtSc itself (not just some other key).</summary>
    public bool OwnedByWipriss => Key == Hotkey.PrtSc;

    /// <summary>Makes the Windows setting match: Snipping Tool off while PrtSc is ours, on otherwise.</summary>
    public void Apply(bool moduleOn = true)
    {
        bool snippingWanted = !(moduleOn && OwnedByWipriss);
        if (SnippingSettingOn == snippingWanted) return;

        using var key = Registry.CurrentUser.CreateSubKey(KeyPath);
        key.SetValue(ValueName, snippingWanted ? 1 : 0, RegistryValueKind.DWord);
    }

    public void Choose(Hotkey key)
    {
        _settings.Key = key == Hotkey.PrtSc ? null : key.ToString();
        _settings.OwnPrintScreen = key == Hotkey.PrtSc;
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
