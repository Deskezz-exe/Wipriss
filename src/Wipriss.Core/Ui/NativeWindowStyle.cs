using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Wipriss.Core.Ui;

/// <summary>Small window tweaks shared by the app and modules. Call from OnSourceInitialized.</summary>
public static class NativeWindowStyle
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;

    /// <summary>Windows 11 rounded corners and border. Ignored on Windows 10.</summary>
    public static void RoundCorners(Window window)
    {
        int value = DWMWCP_ROUND;
        DwmSetWindowAttribute(Handle(window), DWMWA_WINDOW_CORNER_PREFERENCE, ref value, sizeof(int));
    }

    public static void DarkTitleBar(Window window)
    {
        int on = 1;
        DwmSetWindowAttribute(Handle(window), DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, sizeof(int));
    }

    /// <summary>Not in Alt+Tab, and clicking it never takes focus away from the app you work in.</summary>
    public static void NoActivateToolWindow(Window window)
    {
        var hwnd = Handle(window);
        nint style = GetWindowLongPtr(hwnd, GWL_EXSTYLE);
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, style | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
    }

    private static IntPtr Handle(Window window) => new WindowInteropHelper(window).EnsureHandle();

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hWnd, int attribute, ref int value, int size);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(IntPtr hWnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr(IntPtr hWnd, int index, nint value);
}
