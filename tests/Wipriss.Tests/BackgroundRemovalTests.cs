using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Wipriss.Converter;

namespace Wipriss.Tests;

/// <summary>"Убрать однотонный фон": the algorithm ported from Filee, run through the converter.</summary>
public sealed class BackgroundRemovalTests
{
    private const int W = 200, H = 120;

    /// <summary>A "signature" on paper: a black stroke, a blue stroke and a soft edge, made by WPF.</summary>
    private static byte[] Signature(byte paperR = 255, byte paperG = 255, byte paperB = 255)
    {
        var px = new byte[W * H * 4];
        for (int y = 0; y < H; y++)
        for (int x = 0; x < W; x++)
        {
            int o = (y * W + x) * 4;
            (byte r, byte g, byte b) = (paperR, paperG, paperB);
            if (y is >= 56 and < 64 && x is >= 20 and < 100) (r, g, b) = (0, 0, 0);       // black stroke
            if (y is >= 56 and < 64 && x is >= 120 and < 180) (r, g, b) = (20, 40, 200);  // blue stroke
            if (y == 55 && x is >= 20 and < 100) (r, g, b) = (128, 128, 128);             // anti-aliased edge
            px[o] = b; px[o + 1] = g; px[o + 2] = r; px[o + 3] = 255;                     // BGRA
        }
        var bitmap = BitmapSource.Create(W, H, 96, 96, PixelFormats.Bgra32, null, px, W * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    /// <summary>Reads the result back with WPF: RGBA at a point.</summary>
    private static (byte R, byte G, byte B, byte A) At(byte[] png, int x, int y)
    {
        var frame = BitmapDecoder.Create(new MemoryStream(png), BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
        var bgra = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        var px = new byte[4];
        bgra.CopyPixels(new System.Windows.Int32Rect(x, y, 1, 1), px, 4, 0);
        return (px[2], px[1], px[0], px[3]);
    }

    private static byte[] Removed(byte[] input) =>
        ImageConverter.Encode(input, new ConvertRequest { Format = TargetFormat.Png, RemoveBackground = true }).Bytes;

    [Fact]
    public void Paper_BecomesTransparent_InkStays()
    {
        var png = Removed(Signature());

        Assert.Equal(0, At(png, 5, 5).A);
        Assert.Equal(0, At(png, 110, 60).A); // paper between the strokes

        var black = At(png, 60, 60);
        Assert.Equal(255, black.A);
        Assert.True(black.R < 40 && black.G < 40 && black.B < 40);

        var blue = At(png, 150, 60);
        Assert.Equal(255, blue.A);
        Assert.True(blue.B > 150 && blue.R < 80, $"{blue}");
    }

    [Fact]
    public void SoftEdge_LosesItsWhiteFringe()
    {
        var edge = At(Removed(Signature()), 60, 55);
        // Grey over white = black ink half covering: partly transparent and dark, not opaque light grey.
        Assert.InRange(edge.A, 1, 254);
        Assert.True(edge.R < 80, $"{edge}");
    }

    [Fact]
    public void ColouredPaper_IsRemovedToo()
    {
        var png = Removed(Signature(250, 240, 200)); // yellowish paper
        Assert.Equal(0, At(png, 5, 5).A);
        Assert.Equal(255, At(png, 60, 60).A);
    }

    /// <summary>A "portrait" on white: a light skin oval with a pale halo, a light-grey shirt, white eyes inside.</summary>
    private static byte[] Portrait()
    {
        const int w = 300, h = 360;
        var px = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            (byte r, byte g, byte b) = (255, 255, 255);
            double e = Math.Pow((x - 150) / 80.0, 2) + Math.Pow((y - 150) / 110.0, 2);
            if (e < 1) (r, g, b) = (225, 180, 160);                       // skin, lighter than the paper test inks
            else if (e < 1.03) (r, g, b) = (235, 215, 205);               // pale 1–2 px halo along the outline
            if (y > 280 && x is > 40 and < 260) (r, g, b) = (205, 200, 210); // light-grey shirt
            if (y is >= 120 and < 130 && (x is >= 110 and < 130 || x is >= 170 and < 190)) (r, g, b) = (255, 255, 255); // whites of the eyes
            int o = (y * w + x) * 4;
            px[o] = b; px[o + 1] = g; px[o + 2] = r; px[o + 3] = 255;
        }
        var bitmap = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, w * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    [Fact]
    public void Photo_SubjectStaysSolid_OnlyOutsideIsRemoved()
    {
        var png = Removed(Portrait());

        Assert.Equal(0, At(png, 5, 5).A);           // wall
        Assert.Equal(0, At(png, 290, 350).A);       // wall beside the shirt
        Assert.Equal(255, At(png, 150, 200).A);     // skin: never see-through
        Assert.Equal(255, At(png, 150, 320).A);     // light-grey shirt
        Assert.Equal(255, At(png, 120, 125).A);     // white eyes are inside the face, not backdrop

        // Down the top of the head: no pale, solid pixel before the skin starts.
        for (int y = 0; y < 150 - 110; y++)
        {
            var p = At(png, 150, y);
            Assert.False(p.A == 255 && p.G > 200, $"pale fringe left at y={y}: {p}");
        }
    }

    [Theory]
    [InlineData(TargetFormat.Jpg)]
    [InlineData(TargetFormat.Bmp)]
    public void FormatsWithoutTransparency_AreRefused(TargetFormat format) =>
        Assert.Throws<ConvertException>(() =>
            ImageConverter.Encode(Signature(), new ConvertRequest { Format = format, RemoveBackground = true }));

    [Fact]
    public void Result_IsNamedWithoutBackground()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wipriss-bg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var source = Path.Combine(dir, "logo.png");
            File.WriteAllBytes(source, Signature());
            var reply = ImageConverter.ConvertFile(0, source, new ConvertRequest { Format = TargetFormat.Png, RemoveBackground = true });
            Assert.True(reply.Ok, reply.Error);
            Assert.Equal(Path.Combine(dir, "logo без фона.png"), reply.Output);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
