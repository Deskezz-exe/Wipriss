using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Wipriss.Core.Ui;

/// <summary>The icon Explorer shows for a file. Safe to call from a background thread.</summary>
public static class FileIcons
{
    private const uint SHGFI_ICON = 0x100, SHGFI_LARGEICON = 0x0;

    /// <returns>A frozen image (usable on any thread), or null if the file has no icon.</returns>
    public static ImageSource? For(string path)
    {
        var info = new ShFileInfo();
        IntPtr ok = SHGetFileInfoW(path, 0, ref info, (uint)Marshal.SizeOf<ShFileInfo>(), SHGFI_ICON | SHGFI_LARGEICON);
        if (ok == IntPtr.Zero || info.hIcon == IntPtr.Zero) return null;
        try
        {
            var image = Imaging.CreateBitmapSourceFromHIcon(info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            image.Freeze();
            return image;
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            DestroyIcon(info.hIcon);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileInfo
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfoW(string path, uint attributes, ref ShFileInfo info, uint size, uint flags);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);
}
