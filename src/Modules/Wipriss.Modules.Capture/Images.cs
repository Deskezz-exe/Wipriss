using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Wipriss.Capture;

namespace Wipriss.Modules.Capture;

internal static class Images
{
    /// <summary>Screen pixels as a frozen WPF image at 96 DPI, so one image pixel is one layout unit.</summary>
    public static BitmapSource ToBitmap(ScreenImage image)
    {
        var bitmap = BitmapSource.Create(image.Width, image.Height, 96, 96, PixelFormats.Bgr32, null, image.Pixels, image.Stride);
        bitmap.Freeze();
        return bitmap;
    }

    public static byte[] Png(BitmapSource image)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    /// <summary>Pixels of any WPF image as 32-bit BGRA.</summary>
    public static (byte[] Pixels, int Width, int Height) Read(BitmapSource image)
    {
        var source = image.Format == PixelFormats.Bgr32 || image.Format == PixelFormats.Bgra32 || image.Format == PixelFormats.Pbgra32
            ? image
            : new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        int width = source.PixelWidth, height = source.PixelHeight;
        var pixels = new byte[width * height * 4];
        source.CopyPixels(pixels, width * 4, 0);
        return (pixels, width, height);
    }
}

/// <summary>Puts a screenshot on the clipboard, ready to paste anywhere, and keeps it out of clipboard history.</summary>
internal static class ClipboardWriter
{
    /// <returns>False if another program kept the clipboard locked the whole time.</returns>
    public static bool Put(BitmapSource image)
    {
        var data = new DataObject();
        data.SetImage(image); // the classic bitmap format every program understands
        data.SetData("PNG", new MemoryStream(Images.Png(image))); // sharper in browsers and chats
        // Windows' clipboard history (Win+V, cloud sync) and Wipriss' own history skip content marked like this.
        data.SetData("CanIncludeInClipboardHistory", new MemoryStream(BitConverter.GetBytes(0)));
        data.SetData("ExcludeClipboardContentFromMonitorProcessing", new MemoryStream(BitConverter.GetBytes(1)));

        for (int attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                Clipboard.SetDataObject(data, copy: true);
                return true;
            }
            catch (ExternalException)
            {
                Thread.Sleep(40); // another program holds the clipboard for a moment
            }
        }

        return false;
    }
}
