using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Wipriss.Modules.Media;

/// <summary>
/// The colour a cover "feels" like, for the card's background: an average where vivid pixels weigh more than grey,
/// near-black and near-white ones (so a dark cover with a pink sun reads pink). Read once per cover, on a worker thread.
/// </summary>
internal static class CoverColor
{
    public static Color? Of(BitmapSource image)
    {
        try
        {
            // A few thousand pixels are plenty.
            double scale = Math.Min(1, 48.0 / Math.Max(image.PixelWidth, image.PixelHeight));
            BitmapSource small = scale < 1 ? new TransformedBitmap(image, new ScaleTransform(scale, scale)) : image;
            var bgra = new FormatConvertedBitmap(small, PixelFormats.Bgra32, null, 0);
            int w = bgra.PixelWidth, h = bgra.PixelHeight;
            var pixels = new byte[w * h * 4];
            bgra.CopyPixels(pixels, w * 4, 0);
            return Average(pixels);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return null; }
    }

    /// <summary>BGRA bytes in, a mid-dark colour out (or null for an empty picture).</summary>
    public static Color? Average(ReadOnlySpan<byte> bgra)
    {
        double r = 0, g = 0, b = 0, total = 0;
        for (int i = 0; i + 3 < bgra.Length; i += 4)
        {
            double pb = bgra[i], pg = bgra[i + 1], pr = bgra[i + 2], a = bgra[i + 3] / 255.0;
            double max = Math.Max(pr, Math.Max(pg, pb)), min = Math.Min(pr, Math.Min(pg, pb));
            double saturation = max <= 0 ? 0 : (max - min) / max;
            double light = max / 255.0;
            // Vivid and not too dark counts most; grey still counts a little, so grey covers stay grey.
            double weight = a * (0.08 + saturation * saturation) * (light < 0.12 ? 0.2 : 1) * (light > 0.95 && saturation < 0.1 ? 0.3 : 1);
            r += pr * weight; g += pg * weight; b += pb * weight; total += weight;
        }
        if (total <= 0) return null;
        r /= total; g /= total; b /= total;

        // Keep it a background: not brighter than about 60 %.
        double peak = Math.Max(r, Math.Max(g, b));
        if (peak > 150) { double k = 150 / peak; r *= k; g *= k; b *= k; }
        return Color.FromRgb((byte)r, (byte)g, (byte)b);
    }
}
