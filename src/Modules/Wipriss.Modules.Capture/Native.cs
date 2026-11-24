using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;

namespace Wipriss.Modules.Capture;

internal static class Native
{
    public const int WM_HOTKEY = 0x0312;
    public const uint MOD_NOREPEAT = 0x4000;
    public const uint VK_SNAPSHOT = 0x2C, VK_ESCAPE = 0x1B;
    private const uint WDA_EXCLUDEFROMCAPTURE = 0x11;
    private const uint SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010;

    /// <summary>Keeps the window out of every screenshot and screen recording (Windows 10 2004 and later).</summary>
    public static void ExcludeFromCapture(Window window) =>
        SetWindowDisplayAffinity(new WindowInteropHelper(window).EnsureHandle(), WDA_EXCLUDEFROMCAPTURE);

    /// <summary>Places a window exactly over a rectangle in physical pixels, whatever the monitor's scale.</summary>
    public static void PlaceInPixels(Window window, int x, int y, int width, int height) =>
        SetWindowPos(new WindowInteropHelper(window).EnsureHandle(), IntPtr.Zero, x, y, width, height, SWP_NOZORDER | SWP_NOACTIVATE);

    public static void BringToFront(Window window) => SetForegroundWindow(new WindowInteropHelper(window).EnsureHandle());

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);

    [DllImport("user32.dll")]
    public static extern bool UnregisterHotKey(IntPtr hwnd, int id);

    [DllImport("user32.dll")] private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
}

/// <summary>
/// Windows 11 can open Snipping Tool on PrtSc ("Use the Print screen key to open screen capture").
/// While that is on, both would react. This reads and changes that one setting, only when the user presses the button.
/// </summary>
internal static class PrintScreenSetting
{
    private const string Key = @"Control Panel\Keyboard";
    private const string Value = "PrintScreenKeyForSnippingEnabled";

    /// <summary>True when Windows sends PrtSc to Snipping Tool. Missing value means on (the Windows 11 default).</summary>
    public static bool SnippingToolTakesKey
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(Key);
            return key?.GetValue(Value) is not int value || value != 0;
        }
    }

    public static void GiveKeyToWipriss(bool toWipriss)
    {
        using var key = Registry.CurrentUser.CreateSubKey(Key);
        key.SetValue(Value, toWipriss ? 0 : 1, RegistryValueKind.DWord);
    }
}
