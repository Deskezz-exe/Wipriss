using System.Runtime.InteropServices;

namespace Wipriss.Capture;

/// <summary>One display: where it is on the desktop, its usable area without the taskbar, and its scale.</summary>
public sealed record MonitorInfo(PixelRect Bounds, PixelRect WorkArea, double Scale, bool IsPrimary);

/// <summary>
/// Copies screen pixels with GDI. Works for every normal window, including ones running as administrator,
/// because it reads what Windows has already composed. Windows can't be captured on the secure desktop
/// (UAC prompt, lock screen), and DRM-protected video comes out black: that is Windows, not us.
/// Needs a per-monitor DPI aware process, so coordinates are real pixels.
/// </summary>
public static class ScreenGrabber
{
    private const int SRCCOPY = 0x00CC0020, CAPTUREBLT = 0x40000000;

    public static ScreenImage Grab(PixelRect area)
    {
        if (area.IsEmpty) throw new ArgumentException("Empty area.", nameof(area));

        IntPtr screen = GetDC(IntPtr.Zero);
        IntPtr memory = CreateCompatibleDC(screen);
        var info = new BitmapInfo
        {
            biSize = Marshal.SizeOf<BitmapInfo>(),
            biWidth = area.Width,
            biHeight = -area.Height, // top row first
            biPlanes = 1,
            biBitCount = 32,
        };
        IntPtr bitmap = CreateDIBSection(memory, ref info, 0, out IntPtr bits, IntPtr.Zero, 0);
        try
        {
            if (bitmap == IntPtr.Zero) throw new InvalidOperationException("Not enough memory for the screenshot.");
            IntPtr old = SelectObject(memory, bitmap);
            bool ok = BitBlt(memory, 0, 0, area.Width, area.Height, screen, area.X, area.Y, SRCCOPY | CAPTUREBLT);
            SelectObject(memory, old);
            if (!ok) throw new InvalidOperationException("Windows did not give the screen image.");

            var pixels = new byte[area.Width * area.Height * 4];
            Marshal.Copy(bits, pixels, 0, pixels.Length);
            for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255; // GDI leaves alpha undefined
            return new ScreenImage(area.Width, area.Height, pixels);
        }
        finally
        {
            if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
            DeleteDC(memory);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    /// <summary>All displays, in physical pixels.</summary>
    public static List<MonitorInfo> Monitors()
    {
        var list = new List<MonitorInfo>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr monitor, IntPtr dc, ref Rect rect, IntPtr data) =>
        {
            var info = new MonitorInfoEx { cbSize = Marshal.SizeOf<MonitorInfoEx>() };
            if (GetMonitorInfoW(monitor, ref info))
            {
                double scale = GetDpiForMonitor(monitor, 0, out uint dpiX, out _) == 0 ? dpiX / 96.0 : 1.0;
                list.Add(new MonitorInfo(info.rcMonitor.ToPixelRect(), info.rcWork.ToPixelRect(), scale, (info.dwFlags & 1) != 0));
            }

            return true;
        }, IntPtr.Zero);
        return list;
    }

    public static (int X, int Y) CursorPosition() => GetCursorPos(out Point p) ? (p.X, p.Y) : (0, 0);

    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect
    {
        public int Left, Top, Right, Bottom;
        public readonly PixelRect ToPixelRect() => new(Left, Top, Right - Left, Bottom - Top);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X, Y;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfoEx
    {
        public int cbSize;
        public Rect rcMonitor;
        public Rect rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public int biSize, biWidth, biHeight;
        public short biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
    }

    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr dc, ref Rect rect, IntPtr data);

    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr dc, ref BitmapInfo info, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr dest, int x, int y, int w, int h, IntPtr src, int sx, int sy, int rop);
    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorEnumProc callback, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfoW(IntPtr monitor, ref MonitorInfoEx info);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
}
