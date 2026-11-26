using System.Text.Json.Serialization;

namespace Wipriss.Converter;

// What Wipriss and Wipriss.Converter.exe say to each other. This file is compiled into both:
// the island writes one ConvertRequest to the converter's stdin, the converter answers with
// one ConvertReply per file, a JSON line each, on stdout.

public enum TargetFormat { Jpg, Png, WebP, Avif, Bmp, Ico, Tiff }

public enum ResizeKind { None, Pixels, Percent }

/// <summary>What to do with a picture. The request's own values are the default for every file.</summary>
public class ConvertOptions
{
    public TargetFormat Format { get; set; } = TargetFormat.Jpg;
    public ResizeKind Resize { get; set; }

    /// <summary>For <see cref="ResizeKind.Pixels"/>: the picture fits into this box, proportions kept.
    /// One side may be empty: then it follows from the other.</summary>
    public int? Width { get; set; }
    public int? Height { get; set; }

    /// <summary>For <see cref="ResizeKind.Percent"/>.</summary>
    public double Percent { get; set; } = 100;

    /// <summary>Make each file no bigger than this; quality and, if needed, size are lowered.</summary>
    public long? MaxBytes { get; set; }

    /// <summary>Make a plain (one-colour) background transparent: logos, signatures, stamps, scans.
    /// Only for formats with transparency.</summary>
    public bool RemoveBackground { get; set; }

    public ConvertOptions Copy() => new()
    {
        Format = Format, Resize = Resize, Width = Width, Height = Height, Percent = Percent,
        MaxBytes = MaxBytes, RemoveBackground = RemoveBackground,
    };
}

/// <summary>One file of a request.</summary>
public sealed class ConvertJob
{
    /// <summary>The picture to read.</summary>
    public string Path { get; set; } = "";

    /// <summary>The original the result is named after and saved next to, when <see cref="Path"/> is a drawn-on
    /// copy in a temporary folder. Empty: <see cref="Path"/> itself.</summary>
    public string? NameFrom { get; set; }

    /// <summary>This file's own settings; empty: the request's.</summary>
    public ConvertOptions? Options { get; set; }
}

public sealed class ConvertRequest : ConvertOptions
{
    /// <summary>Files with the request's settings (the simple form).</summary>
    public List<string> Files { get; set; } = [];

    /// <summary>Files with their own settings or names; used instead of <see cref="Files"/> when not empty.</summary>
    public List<ConvertJob> Jobs { get; set; } = [];

    /// <summary>Where to save when the original's folder can't be written to.</summary>
    public string? FallbackFolder { get; set; }

    /// <summary>Save everything here instead of next to the originals (previews, pictures to draw on).</summary>
    public string? OutputFolder { get; set; }

    public List<ConvertJob> AllJobs() => Jobs.Count > 0 ? Jobs : Files.Select(f => new ConvertJob { Path = f }).ToList();
}

public sealed class ConvertReply
{
    public int Index { get; set; }

    /// <summary>Set only on in-between lines: how far this file is, 0–99. The final line leaves it empty.</summary>
    public int? Progress { get; set; }

    public bool Ok { get; set; }
    public string? Output { get; set; }
    public long Bytes { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }

    /// <summary>The quality that was used for a lossy format.</summary>
    public int? Quality { get; set; }

    /// <summary>The picture was made smaller than asked to fit <see cref="ConvertRequest.MaxBytes"/>.</summary>
    public bool Shrunk { get; set; }

    /// <summary>The size limit couldn't be reached even at the smallest size tried.</summary>
    public bool OverLimit { get; set; }

    /// <summary>A short reason in Russian, shown as is.</summary>
    public string? Error { get; set; }
}

[JsonSourceGenerationOptions(UseStringEnumConverter = true)]
[JsonSerializable(typeof(ConvertRequest))]
[JsonSerializable(typeof(ConvertOptions))]
[JsonSerializable(typeof(ConvertJob))]
[JsonSerializable(typeof(ConvertReply))]
internal sealed partial class ProtocolJson : JsonSerializerContext;
