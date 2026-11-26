using System.IO;
using ImageMagick;

namespace Wipriss.Converter;

/// <summary>The result of converting one picture in memory.</summary>
public sealed record Encoded(byte[] Bytes, int Width, int Height, int? Quality, bool Shrunk, bool OverLimit);

/// <summary>A file we refuse, with a reason the owner can read.</summary>
public sealed class ConvertException(string message) : Exception(message);

/// <summary>
/// Changes a picture's format, size and weight. The original is never touched: the result goes
/// next to it under a new name, an existing file is never overwritten.
/// </summary>
public static class ImageConverter
{
    public const long MaxInputBytes = 300L * 1024 * 1024;

    /// <summary>Filee's default: removes paper and its shadows, keeps light-grey fills of a logo.</summary>
    private const int BackgroundTolerance = 25;

    /// <summary>The icon sizes put into an .ico, from big to small.</summary>
    private static readonly int[] IconSizes = [256, 128, 64, 48, 32, 24, 16];

    private const int DefaultQuality = 90, MinQuality = 30;
    private const int MaxShrinkRounds = 10;

    public static string Extension(TargetFormat format) => format switch
    {
        TargetFormat.Jpg => ".jpg",
        TargetFormat.Png => ".png",
        TargetFormat.WebP => ".webp",
        TargetFormat.Avif => ".avif",
        TargetFormat.Bmp => ".bmp",
        TargetFormat.Ico => ".ico",
        TargetFormat.Tiff => ".tif",
        _ => throw new ArgumentOutOfRangeException(nameof(format)),
    };

    public static bool IsLossy(TargetFormat format) => format is TargetFormat.Jpg or TargetFormat.WebP or TargetFormat.Avif;

    /// <summary>Converts one file and saves the result. Errors come back in the reply, never thrown.</summary>
    /// <param name="progress">Gets 0–99 while the file is being worked on.</param>
    public static ConvertReply ConvertFile(int index, string path, ConvertRequest request, Action<int>? progress = null) =>
        ConvertFile(index, new ConvertJob { Path = path }, request, progress);

    /// <summary>One file of a request: its own settings if it has them, else the request's.</summary>
    public static ConvertReply ConvertFile(int index, ConvertJob job, ConvertRequest request, Action<int>? progress = null)
    {
        var reply = new ConvertReply { Index = index };
        try
        {
            var options = job.Options ?? request;
            string nameFrom = string.IsNullOrEmpty(job.NameFrom) ? job.Path : job.NameFrom;
            if (!Path.IsPathFullyQualified(nameFrom)) throw new ConvertException("непонятный путь к файлу");
            var input = ReadInput(job.Path);
            var result = Encode(input, options, progress);
            reply.Output = request.OutputFolder is { } only && Path.IsPathFullyQualified(only)
                ? SaveInto(only, nameFrom, result.Bytes, options)
                : Save(nameFrom, result.Bytes, options, request.FallbackFolder);
            reply.Ok = true;
            reply.Bytes = result.Bytes.LongLength;
            reply.Width = result.Width;
            reply.Height = result.Height;
            reply.Quality = result.Quality;
            reply.Shrunk = result.Shrunk;
            reply.OverLimit = result.OverLimit;
        }
        catch (ConvertException ex) { reply.Error = ex.Message; }
        catch (MagickResourceLimitErrorException) { reply.Error = "картинка слишком большая"; }
        catch (MagickException) { reply.Error = "файл повреждён или это не картинка"; }
        catch (FileNotFoundException) { reply.Error = "файл не найден"; }
        catch (DirectoryNotFoundException) { reply.Error = "файл не найден"; }
        catch (UnauthorizedAccessException) { reply.Error = "нет доступа к файлу"; }
        catch (IOException) { reply.Error = "файл занят другой программой или диск недоступен"; }
        catch (OutOfMemoryException) { reply.Error = "не хватило памяти"; }
        return reply;
    }

    private static byte[] ReadInput(string path)
    {
        if (!Path.IsPathFullyQualified(path)) throw new ConvertException("непонятный путь к файлу");
        if (Directory.Exists(path)) throw new ConvertException("это папка, а не картинка");
        var info = new FileInfo(path);
        if (!info.Exists) throw new FileNotFoundException();
        if (info.Length > MaxInputBytes) throw new ConvertException("файл больше 300 МБ");
        return File.ReadAllBytes(path);
    }

    /// <summary>The whole conversion in memory: read, turn upright, resize, fit the weight limit.</summary>
    public static Encoded Encode(byte[] input, ConvertOptions request, Action<int>? progress = null)
    {
        using var meter = new ProgressMeter(progress);
        SafeMagick.Initialize();
        var format = SafeMagick.Sniff(input) ?? throw new ConvertException("это не картинка или формат не поддерживается");
        if (request.RemoveBackground && !KeepsTransparency(request.Format))
            throw new ConvertException("JPG и BMP не умеют прозрачность: выберите PNG, WEBP, AVIF, ICO или TIFF");

        var settings = new MagickReadSettings { Format = format };
        var ping = new MagickImageInfo(input, settings);
        if ((long)ping.Width * ping.Height > SafeMagick.MaxPixels) throw new ConvertException("картинка слишком большая");

        using var image = new MagickImage();
        meter.Watch(image);
        meter.Step(0, 15);
        image.Read(input, settings);
        meter.Step(15, 25);
        image.AutoOrient();
        StripPrivateData(image);
        Resize(image, request);
        if (request.Format == TargetFormat.Ico) FitIcon(image);
        if (request.RemoveBackground)
        {
            // After resizing: fewer pixels to look at. Before encoding, which flattens alpha for JPG and BMP.
            meter.Step(25, 45);
            PhotoCutout.RemovePlainBackground(image, BackgroundTolerance);
        }
        FlattenIfNeeded(image, request.Format);

        long? limit = request.MaxBytes is > 0 ? request.MaxBytes : null;
        // One encode gets the rest of the bar; with a weight limit more encodes may follow.
        if (limit is null) meter.Step(request.RemoveBackground ? 45 : 25, 99);
        else meter.NextEncode();
        int quality = DefaultQuality;
        byte[] bytes = Write(image, request.Format, quality);
        if (limit is null || bytes.LongLength <= limit)
            return Done(bytes, image, request.Format, quality, shrunk: false, over: false);

        return FitWeight(image, request.Format, limit.Value, bytes, meter);
    }

    /// <summary>
    /// Lossy formats: the best quality that fits, found by halving the range. If even the lowest
    /// quality is too heavy, the picture gets smaller step by step (lossless formats only do this).
    /// </summary>
    private static Encoded FitWeight(MagickImage original, TargetFormat format, long limit, byte[] first, ProgressMeter meter)
    {
        bool lossy = IsLossy(format);
        var image = (MagickImage)original.Clone();
        meter.Watch(image);
        try
        {
            bool shrunk = false;
            byte[] smallest = first;
            for (int round = 0; round <= MaxShrinkRounds; round++)
            {
                if (lossy)
                {
                    var (bytes, quality) = BestQuality(image, format, limit, meter);
                    if (bytes is not null) return Done(bytes, image, format, quality, shrunk, over: false);
                    meter.NextEncode();
                    smallest = Write(image, format, MinQuality);
                }
                else if (round > 0)
                {
                    meter.NextEncode();
                    smallest = Write(image, format, DefaultQuality);
                    if (smallest.LongLength <= limit) return Done(smallest, image, format, DefaultQuality, shrunk, over: false);
                }

                // Weight grows roughly with the pixel count, so scale both sides by the square root.
                double factor = Math.Clamp(Math.Sqrt((double)limit / smallest.LongLength) * 0.95, 0.3, 0.92);
                int width = Math.Max(1, (int)Math.Round(image.Width * factor));
                int height = Math.Max(1, (int)Math.Round(image.Height * factor));
                if (width < 16 && height < 16) break;
                image.Resize(new MagickGeometry((uint)width, (uint)height) { IgnoreAspectRatio = true });
                shrunk = true;
            }
            return Done(smallest, image, format, lossy ? MinQuality : DefaultQuality, shrunk, over: true);
        }
        finally
        {
            image.Dispose();
        }
    }

    private static (byte[]? Bytes, int Quality) BestQuality(MagickImage image, TargetFormat format, long limit, ProgressMeter meter)
    {
        byte[]? best = null;
        int bestQuality = 0, low = MinQuality, high = DefaultQuality;
        while (low <= high)
        {
            int quality = (low + high) / 2;
            meter.NextEncode();
            var bytes = Write(image, format, quality);
            if (bytes.LongLength <= limit)
            {
                best = bytes;
                bestQuality = quality;
                low = quality + 1;
            }
            else
            {
                high = quality - 1;
            }
        }
        return (best, bestQuality);
    }

    private static Encoded Done(byte[] bytes, MagickImage image, TargetFormat format, int quality, bool shrunk, bool over) =>
        new(bytes, (int)image.Width, (int)image.Height, IsLossy(format) ? quality : null, shrunk, over);

    public static bool KeepsTransparency(TargetFormat format) => format is not (TargetFormat.Jpg or TargetFormat.Bmp);

    /// <summary>Privacy: camera data and the place a photo was taken stay behind. The colour profile stays.</summary>
    private static void StripPrivateData(MagickImage image)
    {
        foreach (var name in new[] { "exif", "xmp", "iptc", "8bim" }) image.RemoveProfile(name);
    }

    /// <summary>JPG and BMP can't hold transparency: transparent parts become white.</summary>
    private static void FlattenIfNeeded(MagickImage image, TargetFormat format)
    {
        if (!KeepsTransparency(format) && image.HasAlpha)
        {
            image.BackgroundColor = MagickColors.White;
            image.Alpha(AlphaAction.Remove);
            image.Alpha(AlphaAction.Off);
        }
    }

    private static void Resize(MagickImage image, ConvertOptions request)
    {
        int width = (int)image.Width, height = (int)image.Height;
        (int w, int h) = request.Resize switch
        {
            ResizeKind.Pixels => FitBox(width, height, request.Width, request.Height),
            ResizeKind.Percent => (Scale(width, request.Percent), Scale(height, request.Percent)),
            _ => (width, height),
        };
        if (w == width && h == height) return;
        if ((long)w * h > SafeMagick.MaxPixels || w > 32_000 || h > 32_000)
            throw new ConvertException("такой размер слишком большой");
        image.Resize(new MagickGeometry((uint)w, (uint)h) { IgnoreAspectRatio = true });
    }

    private static int Scale(int side, double percent)
    {
        if (percent is <= 0 or > 1000 || double.IsNaN(percent)) throw new ConvertException("проценты: от 1 до 1000");
        return Math.Max(1, (int)Math.Round(side * percent / 100));
    }

    /// <summary>The size that fits into the box with the same proportions. An empty side follows the other.</summary>
    public static (int Width, int Height) FitBox(int width, int height, int? boxWidth, int? boxHeight)
    {
        if (boxWidth is <= 0 || boxHeight is <= 0) throw new ConvertException("размер должен быть больше нуля");
        if (boxWidth is null && boxHeight is null) return (width, height);
        double scale = Math.Min(
            boxWidth is { } bw ? (double)bw / width : double.PositiveInfinity,
            boxHeight is { } bh ? (double)bh / height : double.PositiveInfinity);
        return (Math.Max(1, (int)Math.Round(width * scale)), Math.Max(1, (int)Math.Round(height * scale)));
    }

    /// <summary>An icon is at most 256 px and holds every standard size up to that.</summary>
    private static void FitIcon(MagickImage image)
    {
        if (image.Width > 256 || image.Height > 256)
        {
            var (w, h) = FitBox((int)image.Width, (int)image.Height, 256, 256);
            image.Resize(new MagickGeometry((uint)w, (uint)h) { IgnoreAspectRatio = true });
        }
    }

    private static byte[] Write(MagickImage image, TargetFormat format, int quality)
    {
        switch (format)
        {
            case TargetFormat.Jpg:
                image.Quality = (uint)quality;
                image.Settings.Interlace = Interlace.Plane; // progressive: smaller and loads gracefully
                return image.ToByteArray(MagickFormat.Jpeg);
            case TargetFormat.WebP:
                image.Quality = (uint)quality;
                image.Settings.SetDefine(MagickFormat.WebP, "method", "5");
                return image.ToByteArray(MagickFormat.WebP);
            case TargetFormat.Avif:
                image.Quality = (uint)quality;
                image.Settings.SetDefine(MagickFormat.Heic, "speed", "6");
                return image.ToByteArray(MagickFormat.Avif);
            case TargetFormat.Png:
                image.Settings.SetDefine(MagickFormat.Png, "compression-level", "9");
                return image.ToByteArray(MagickFormat.Png);
            case TargetFormat.Bmp:
                return image.ToByteArray(MagickFormat.Bmp);
            case TargetFormat.Tiff:
                image.Settings.Compression = CompressionMethod.Zip;
                return image.ToByteArray(MagickFormat.Tiff);
            case TargetFormat.Ico:
                int largest = (int)Math.Max(image.Width, image.Height);
                var sizes = IconSizes.Where(s => s <= largest).DefaultIfEmpty(largest);
                image.Settings.SetDefine(MagickFormat.Icon, "auto-resize", string.Join(",", sizes));
                return image.ToByteArray(MagickFormat.Icon);
            default:
                throw new ArgumentOutOfRangeException(nameof(format));
        }
    }

    /// <summary>
    /// Saves next to the original as "name.ext", or "name (2).ext" and so on: an existing file,
    /// the original included, is never replaced. If that folder is read-only, uses the fallback.
    /// </summary>
    public static string Save(string source, byte[] bytes, ConvertRequest request) =>
        Save(source, bytes, request, request.FallbackFolder);

    public static string Save(string source, byte[] bytes, ConvertOptions request, string? fallbackFolder)
    {
        string name = Path.GetFileNameWithoutExtension(source) + (request.RemoveBackground ? " без фона" : "");
        string ext = Extension(request.Format);
        string? folder = Path.GetDirectoryName(source);
        try
        {
            if (folder is null) throw new UnauthorizedAccessException();
            return WriteNew(folder, name, ext, bytes);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException && fallbackFolder is { } fallback
                                   && Path.IsPathFullyQualified(fallback))
        {
            Directory.CreateDirectory(fallback);
            return WriteNew(fallback, name, ext, bytes);
        }
    }

    private static string SaveInto(string folder, string source, byte[] bytes, ConvertOptions options)
    {
        Directory.CreateDirectory(folder);
        return WriteNew(folder, Path.GetFileNameWithoutExtension(source), Extension(options.Format), bytes);
    }

    private static string WriteNew(string folder, string name, string ext, byte[] bytes)
    {
        for (int n = 1; n < 1000; n++)
        {
            string path = Path.Combine(folder, n == 1 ? name + ext : $"{name} ({n}){ext}");
            FileStream stream;
            try
            {
                // CreateNew fails if anything is already there: that is the "never overwrite" guarantee.
                stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            }
            catch (IOException) when (File.Exists(path) || Directory.Exists(path))
            {
                continue;
            }

            try
            {
                using (stream) stream.Write(bytes);
                return path;
            }
            catch
            {
                try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                throw;
            }
        }
        throw new ConvertException("слишком много файлов с таким именем");
    }
}
