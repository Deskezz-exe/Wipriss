using System.IO;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ImageMagick;
using Wipriss.Converter;

namespace Wipriss.Tests;

public sealed class ImageConverterTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "wipriss-convert-" + Guid.NewGuid().ToString("N"));

    public ImageConverterTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    /// <summary>A PNG made by WPF, so the tests don't depend on ImageMagick to create input.</summary>
    private static byte[] Png(int width, int height, bool noise = false, bool alpha = false)
    {
        var pixels = new byte[width * height * 4];
        var random = new Random(42);
        for (int i = 0; i < pixels.Length; i += 4)
        {
            int x = i / 4 % width, y = i / 4 / width;
            pixels[i] = noise ? (byte)random.Next(256) : (byte)(x * 255 / width);
            pixels[i + 1] = noise ? (byte)random.Next(256) : (byte)(y * 255 / height);
            pixels[i + 2] = 128;
            pixels[i + 3] = alpha && x < width / 2 ? (byte)0 : (byte)255;
        }
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static ConvertRequest To(TargetFormat format) => new() { Format = format };

    [Fact]
    public void Sniff_FindsFormatsFromBytes()
    {
        Assert.Equal(MagickFormat.Png, SafeMagick.Sniff(Png(4, 4)));
        Assert.Equal(MagickFormat.Jpeg, SafeMagick.Sniff([0xFF, 0xD8, 0xFF, 0xE0, 0, 0, 0, 0, 0, 0, 0, 0]));
        Assert.Equal(MagickFormat.WebP, SafeMagick.Sniff("RIFF\0\0\0\0WEBPVP8 "u8));
        Assert.Equal(MagickFormat.Heic, SafeMagick.Sniff([0, 0, 0, 0x18, .. "ftypheic\0\0\0\0mif1heic"u8]));
        Assert.Equal(MagickFormat.Avif, SafeMagick.Sniff([0, 0, 0, 0x1C, .. "ftypMA1B\0\0\0\0mif1avifmiaf"u8]));
        Assert.Null(SafeMagick.Sniff("<svg xmlns='http://www.w3.org/2000/svg'/>"u8));
        Assert.Null(SafeMagick.Sniff("push graphic-context"u8));
        Assert.Null(SafeMagick.Sniff([1, 2, 3]));
    }

    [Fact]
    public void ScriptsAndVectorsAreRefused()
    {
        var mvg = Encoding.ASCII.GetBytes("push graphic-context\nviewbox 0 0 10 10\nimage over 0,0 0,0 'C:/x.png'\npop graphic-context\n");
        Assert.Throws<ConvertException>(() => ImageConverter.Encode(mvg, To(TargetFormat.Png)));

        // Even read directly, the policy stops the MVG and SVG coders.
        SafeMagick.Initialize();
        Assert.ThrowsAny<MagickException>(() => new MagickImage(mvg, new MagickReadSettings { Format = MagickFormat.Mvg }));
        Assert.ThrowsAny<MagickException>(() => new MagickImage("<svg/>"u8.ToArray(), new MagickReadSettings { Format = MagickFormat.Svg }));
    }

    [Theory]
    [InlineData(TargetFormat.Jpg, MagickFormat.Jpeg)]
    [InlineData(TargetFormat.Png, MagickFormat.Png)]
    [InlineData(TargetFormat.WebP, MagickFormat.WebP)]
    [InlineData(TargetFormat.Avif, MagickFormat.Avif)]
    [InlineData(TargetFormat.Bmp, MagickFormat.Bmp)]
    [InlineData(TargetFormat.Ico, MagickFormat.Ico)]
    [InlineData(TargetFormat.Tiff, MagickFormat.Tiff)]
    public void Converts_ToEveryFormat(TargetFormat target, MagickFormat expected)
    {
        var result = ImageConverter.Encode(Png(120, 80, alpha: true), To(target));
        Assert.Equal(expected, SafeMagick.Sniff(result.Bytes));
        Assert.Equal(120, result.Width);
        Assert.Equal(80, result.Height);
    }

    [Fact]
    public void Converted_FileReadsBack()
    {
        var result = ImageConverter.Encode(Png(64, 48), To(TargetFormat.WebP));
        var back = ImageConverter.Encode(result.Bytes, To(TargetFormat.Png));
        Assert.Equal((64, 48), (back.Width, back.Height));
    }

    [Fact]
    public void Resize_ByPercentAndBox()
    {
        var half = ImageConverter.Encode(Png(200, 100), new ConvertRequest { Format = TargetFormat.Png, Resize = ResizeKind.Percent, Percent = 50 });
        Assert.Equal((100, 50), (half.Width, half.Height));

        var box = ImageConverter.Encode(Png(200, 100), new ConvertRequest { Format = TargetFormat.Png, Resize = ResizeKind.Pixels, Width = 80, Height = 80 });
        Assert.Equal((80, 40), (box.Width, box.Height));

        var byHeight = ImageConverter.Encode(Png(200, 100), new ConvertRequest { Format = TargetFormat.Png, Resize = ResizeKind.Pixels, Height = 300 });
        Assert.Equal((600, 300), (byHeight.Width, byHeight.Height));
    }

    [Theory]
    [InlineData(1000, 500, 100, null, 100, 50)]
    [InlineData(1000, 500, null, 100, 200, 100)]
    [InlineData(1000, 500, 300, 300, 300, 150)]
    [InlineData(500, 1000, 300, 300, 150, 300)]
    [InlineData(10, 10, null, null, 10, 10)]
    public void FitBox_KeepsProportions(int w, int h, int? bw, int? bh, int ew, int eh) =>
        Assert.Equal((ew, eh), ImageConverter.FitBox(w, h, bw, bh));

    [Fact]
    public void Progress_OnlyGoesUpAndStaysBelow100()
    {
        var seen = new List<int>();
        var input = Png(800, 600, noise: true);
        long free = ImageConverter.Encode(input, To(TargetFormat.Jpg)).Bytes.LongLength;

        ImageConverter.Encode(input, new ConvertRequest { Format = TargetFormat.Jpg, MaxBytes = free / 2 }, p => { lock (seen) seen.Add(p); });

        lock (seen)
        {
            Assert.True(seen.Count >= 4, $"only {seen.Count} updates");
            Assert.Equal(seen.OrderBy(p => p).Distinct(), seen);
            Assert.All(seen, p => Assert.InRange(p, 0, 99));
            Assert.True(seen[^1] >= 45);
        }
    }

    [Fact]
    public void Icon_IsAtMost256()
    {
        var result = ImageConverter.Encode(Png(600, 300), To(TargetFormat.Ico));
        Assert.Equal((256, 128), (result.Width, result.Height));
    }

    [Theory]
    [InlineData(TargetFormat.Jpg)]
    [InlineData(TargetFormat.WebP)]
    public void WeightLimit_LossyLowersQuality(TargetFormat format)
    {
        var input = Png(800, 600, noise: true);
        long free = ImageConverter.Encode(input, To(format)).Bytes.LongLength;
        long limit = free * 6 / 10;

        var result = ImageConverter.Encode(input, new ConvertRequest { Format = format, MaxBytes = limit });

        Assert.True(result.Bytes.LongLength <= limit);
        Assert.False(result.OverLimit);
        Assert.NotNull(result.Quality);
        Assert.True(result.Quality < 90);
    }

    [Fact]
    public void WeightLimit_LosslessShrinksPicture()
    {
        var input = Png(600, 400, noise: true);
        const long limit = 150 * 1024;

        var result = ImageConverter.Encode(input, new ConvertRequest { Format = TargetFormat.Png, MaxBytes = limit });

        Assert.True(result.Bytes.LongLength <= limit);
        Assert.True(result.Shrunk);
        Assert.True(result.Width < 600);
        Assert.Equal(1.5, (double)result.Width / result.Height, 1);
    }

    [Fact]
    public void Save_NeverOverwrites()
    {
        var original = Path.Combine(_dir, "photo.png");
        var originalBytes = Png(10, 10);
        File.WriteAllBytes(original, originalBytes);
        File.WriteAllBytes(Path.Combine(_dir, "photo.jpg"), [1, 2, 3]);

        var first = ImageConverter.Save(original, [9], To(TargetFormat.Jpg));
        var same = ImageConverter.Save(original, [8], To(TargetFormat.Png));

        Assert.Equal(Path.Combine(_dir, "photo (2).jpg"), first);
        Assert.Equal(Path.Combine(_dir, "photo (2).png"), same);
        Assert.Equal(originalBytes, File.ReadAllBytes(original));
        Assert.Equal([1, 2, 3], File.ReadAllBytes(Path.Combine(_dir, "photo.jpg")));
    }

    [Fact]
    public void ConvertFile_WritesResultNextToOriginal()
    {
        var original = Path.Combine(_dir, "a.png");
        File.WriteAllBytes(original, Png(30, 20));

        var reply = ImageConverter.ConvertFile(0, original, To(TargetFormat.WebP));

        Assert.True(reply.Ok, reply.Error);
        Assert.Equal(Path.Combine(_dir, "a.webp"), reply.Output);
        Assert.Equal(new FileInfo(reply.Output!).Length, reply.Bytes);
    }

    [Fact]
    public void ConvertFile_ReportsProblemsInsteadOfThrowing()
    {
        var missing = ImageConverter.ConvertFile(0, Path.Combine(_dir, "nope.png"), To(TargetFormat.Jpg));
        Assert.False(missing.Ok);
        Assert.Equal("файл не найден", missing.Error);

        var text = Path.Combine(_dir, "fake.png");
        File.WriteAllText(text, "not a picture at all, just text");
        var fake = ImageConverter.ConvertFile(1, text, To(TargetFormat.Jpg));
        Assert.False(fake.Ok);
        Assert.False(File.Exists(Path.Combine(_dir, "fake.jpg")));

        var relative = ImageConverter.ConvertFile(2, "a.png", To(TargetFormat.Jpg));
        Assert.False(relative.Ok);
    }
}
