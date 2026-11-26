using ImageMagick;
using ImageMagick.Configuration;

namespace Wipriss.Converter;

/// <summary>
/// ImageMagick with the doors closed. Pictures come from anywhere, so:
/// <list type="bullet">
/// <item>the policy allows only the picture formats Wipriss works with; scripts and vector
/// formats (MSL, MVG, SVG, PS, PDF, URL, EPHEMERAL, TXT…) and external programs are refused;</item>
/// <item>the format is found from the file's first bytes, never from its name, and the picture is
/// read from memory, so a name like <c>msl:x.png</c> or <c>a.png[0]</c> means nothing;</item>
/// <item>limits on size, memory and time stop "picture bombs".</item>
/// </list>
/// </summary>
public static class SafeMagick
{
    private const string Policy = """
        <policymap>
          <policy domain="delegate" rights="none" pattern="*" />
          <policy domain="module" rights="none" pattern="{MSL,MVG,PS,PDF,XPS,SVG,MSVG,URL,HTTP,HTTPS,FTP,TEXT,TXT,EPHEMERAL,LABEL,CAPTION}" />
          <policy domain="coder" rights="none" pattern="*" />
          <policy domain="coder" rights="read|write" pattern="{PNG,PNG8,PNG24,PNG32,PNG48,PNG64,JPEG,JPG,WEBP,AVIF,BMP,BMP3,ICO,ICON,TIFF,TIF,TIFF64}" />
          <policy domain="coder" rights="read" pattern="{HEIC,HEIF,GIF}" />
          <policy domain="path" rights="none" pattern="@*" />
          <policy domain="resource" name="width" value="32KP" />
          <policy domain="resource" name="height" value="32KP" />
          <policy domain="resource" name="list-length" value="64" />
          <policy domain="resource" name="time" value="300" />
          <policy domain="system" name="max-memory-request" value="1GiB" />
        </policymap>
        """;

    /// <summary>The biggest picture accepted, in pixels (a 16 000 x 12 500 photo).</summary>
    public const long MaxPixels = 200_000_000;

    private static readonly Lock Gate = new();
    private static bool _ready;

    /// <summary>Must run before any other ImageMagick call.</summary>
    public static void Initialize()
    {
        lock (Gate)
        {
            if (_ready) return;
            var files = ConfigurationFiles.Default;
            files.Policy.Data = Policy;
            MagickNET.Initialize(files);
            ResourceLimits.Width = 32_000;
            ResourceLimits.Height = 32_000;
            ResourceLimits.Area = MaxPixels;
            ResourceLimits.ListLength = 64;
            ResourceLimits.Memory = 1536UL * 1024 * 1024;
            ResourceLimits.Disk = 0; // nothing spills to the temp folder
            _ready = true;
        }
    }

    /// <summary>Finds the picture format from the file's first bytes. Null: not a picture we take.</summary>
    public static MagickFormat? Sniff(ReadOnlySpan<byte> head)
    {
        if (head.Length < 12) return null;
        if (head[0] == 0x89 && head[1] == 'P' && head[2] == 'N' && head[3] == 'G') return MagickFormat.Png;
        if (head[0] == 0xFF && head[1] == 0xD8 && head[2] == 0xFF) return MagickFormat.Jpeg;
        if (head[0] == 'G' && head[1] == 'I' && head[2] == 'F' && head[3] == '8') return MagickFormat.Gif;
        if (head[0] == 'B' && head[1] == 'M') return MagickFormat.Bmp;
        if ((head[0] == 'I' && head[1] == 'I' && head[2] == 42 && head[3] == 0) ||
            (head[0] == 'M' && head[1] == 'M' && head[2] == 0 && head[3] == 42)) return MagickFormat.Tiff;
        if (head[0] == 'R' && head[1] == 'I' && head[2] == 'F' && head[3] == 'F' &&
            head[8] == 'W' && head[9] == 'E' && head[10] == 'B' && head[11] == 'P') return MagickFormat.WebP;
        if (head[0] == 0 && head[1] == 0 && head[2] == 1 && head[3] == 0) return MagickFormat.Ico;
        if (head[4] == 'f' && head[5] == 't' && head[6] == 'y' && head[7] == 'p') return SniffHeif(head);
        return null;
    }

    // ISO media "ftyp" box: major brand, minor version, then compatible brands.
    private static MagickFormat? SniffHeif(ReadOnlySpan<byte> head)
    {
        int boxSize = (head[0] << 24) | (head[1] << 16) | (head[2] << 8) | head[3];
        int end = Math.Min(head.Length, Math.Max(boxSize, 16));
        bool heic = false;
        for (int i = 8; i + 4 <= end; i += 4)
        {
            if (i == 12) continue; // minor version, not a brand
            var brand = System.Text.Encoding.ASCII.GetString(head.Slice(i, 4));
            if (brand is "avif" or "avis") return MagickFormat.Avif;
            if (brand is "heic" or "heix" or "hevc" or "hevx" or "heim" or "heis" or "hevm" or "hevs" or "mif1" or "msf1")
                heic = true;
        }
        return heic ? MagickFormat.Heic : null;
    }
}
