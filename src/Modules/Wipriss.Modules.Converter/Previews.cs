using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Wipriss.Converter;

namespace Wipriss.Modules.Converter;

/// <summary>
/// Pictures for the window: small previews and the full picture to draw on. Windows' own decoders (WIC) read them
/// straight at the needed size, so a 50-megapixel photo costs a few hundred kilobytes as a preview. What Windows
/// can't read (HEIC or AVIF without the codec from the Store) is made into a PNG by Wipriss.Converter.exe.
/// </summary>
internal static class Previews
{
    /// <summary>Largest side of a picture opened for drawing.</summary>
    public const int MaxEditSide = 10_000;

    /// <summary>The picture at most <paramref name="maxSide"/> pixels long (0: full size), upright, frozen; null if
    /// Windows can't read it. Safe on any thread.</summary>
    public static BitmapSource? Load(string path, int maxSide)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile,
                BitmapCacheOption.None);
            var frame = decoder.Frames[0];
            int width = frame.PixelWidth, height = frame.PixelHeight;
            int orientation = Orientation(frame.Metadata as BitmapMetadata);

            stream.Position = 0;
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            image.StreamSource = stream;
            int limit = maxSide > 0 ? maxSide : MaxEditSide;
            if (Math.Max(width, height) > limit)
            {
                if (width >= height) image.DecodePixelWidth = limit;
                else image.DecodePixelHeight = limit;
            }
            image.EndInit();
            image.Freeze();
            return Upright(image, orientation);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException
                                   or InvalidOperationException or FileFormatException or OverflowException or OutOfMemoryException
                                   or System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }

    /// <summary>
    /// PNG copies of pictures Windows can't read, made by the converter in <see cref="ConverterSession.TempFolder"/>.
    /// The caller loads and deletes them. Entries stay null for files the converter couldn't read either.
    /// </summary>
    public static async Task<string?[]> ViaConverterAsync(IReadOnlyList<string> paths, int maxSide, CancellationToken cancel)
    {
        var result = new string?[paths.Count];
        if (paths.Count == 0) return result;
        var request = new ConvertRequest
        {
            Format = TargetFormat.Png,
            Resize = maxSide > 0 ? ResizeKind.Pixels : ResizeKind.None,
            Width = maxSide > 0 ? maxSide : null,
            Height = maxSide > 0 ? maxSide : null,
            OutputFolder = Path.Combine(ConverterSession.TempFolder, "preview"),
            Files = [.. paths],
        };
        try
        {
            await ConverterWorker.RunAsync(request, reply =>
            {
                if (reply.Progress is null && reply.Ok && reply.Index >= 0 && reply.Index < result.Length) result[reply.Index] = reply.Output;
            }, cancel);
        }
        catch (Exception ex) when (ex is FileNotFoundException or System.ComponentModel.Win32Exception or IOException) { }
        return result;
    }

    /// <summary>EXIF orientation (1 = upright). Phones save photos sideways and set this instead.</summary>
    private static int Orientation(BitmapMetadata? metadata)
    {
        if (metadata is null) return 1;
        foreach (var query in (string[])["/app1/ifd/{ushort=274}", "/ifd/{ushort=274}"])
        {
            try
            {
                if (metadata.ContainsQuery(query) && metadata.GetQuery(query) is ushort value) return value;
            }
            catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or ArgumentException
                                       or System.Runtime.InteropServices.COMException) { }
        }
        return 1;
    }

    private static BitmapSource Upright(BitmapSource image, int orientation)
    {
        double angle = orientation switch { 3 => 180, 6 or 5 => 90, 8 or 7 => 270, _ => 0 };
        if (angle == 0) return image;
        var turned = new TransformedBitmap(image, new RotateTransform(angle));
        turned.Freeze();
        return turned;
    }
}
