using System.Runtime.InteropServices;

namespace Wipriss.Capture;

/// <summary>Visible windows and simulated scrolling. Window titles are never read.</summary>
public static class Desktop
{
    private const int GWL_EXSTYLE = -20, WS_EX_TRANSPARENT = 0x20, WS_EX_NOACTIVATE = 0x08000000;
    private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9, DWMWA_CLOAKED = 14;

    /// <summary>On-screen bounds of visible top-level windows, topmost first, without this app's own windows.</summary>
    public static List<PixelRect> VisibleWindows()
    {
        var list = new List<PixelRect>();
        int self = Environment.ProcessId;
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd) || IsIconic(hwnd)) return true;
            GetWindowThreadProcessId(hwnd, out int pid);
            if (pid == self) return true;

            long style = GetWindowLongPtrW(hwnd, GWL_EXSTYLE).ToInt64();
            if ((style & WS_EX_TRANSPARENT) != 0 || (style & WS_EX_NOACTIVATE) != 0) return true; // click-through overlays
            if (DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return true;

            if (DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out ScreenGrabber.Rect rect, Marshal.SizeOf<ScreenGrabber.Rect>()) != 0
                && !GetWindowRect(hwnd, out rect))
                return true;

            var bounds = rect.ToPixelRect();
            if (bounds.Width >= 40 && bounds.Height >= 30) list.Add(bounds);
            return true;
        }, IntPtr.Zero);
        return list;
    }

    public static void MoveCursor(int x, int y) => SetCursorPos(x, y);

    /// <summary>Turns the mouse wheel where the cursor is. Negative scrolls down, 120 is one notch.</summary>
    public static void Wheel(int delta)
    {
        var input = new Input[1];
        input[0].type = 0; // mouse
        input[0].mi.mouseData = unchecked((uint)delta);
        input[0].mi.dwFlags = 0x0800; // MOUSEEVENTF_WHEEL
        SendInput(1, input, Marshal.SizeOf<Input>());
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint type;
        public MouseInput mi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int dx, dy;
        public uint mouseData, dwFlags, time;
        public IntPtr dwExtraInfo;
        // KEYBDINPUT and HARDWAREINPUT are smaller, so MOUSEINPUT alone gives the right size.
    }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr data);

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr data);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int GetWindowThreadProcessId(IntPtr hwnd, out int pid);
    [DllImport("user32.dll")] private static extern IntPtr GetWindowLongPtrW(IntPtr hwnd, int index);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out ScreenGrabber.Rect rect);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out ScreenGrabber.Rect value, int size);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern uint SendInput(uint count, Input[] inputs, int size);
}
