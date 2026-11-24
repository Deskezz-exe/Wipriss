using System.Runtime.InteropServices;

namespace Wipriss.Capture;

/// <summary>A rectangle in physical screen pixels (the whole desktop shares one coordinate space).</summary>
public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
    public bool IsEmpty => Width <= 0 || Height <= 0;

    public bool Contains(int x, int y) => x >= X && x < Right && y >= Y && y < Bottom;

    public PixelRect Intersect(PixelRect other)
    {
        int x = Math.Max(X, other.X), y = Math.Max(Y, other.Y);
        int right = Math.Min(Right, other.Right), bottom = Math.Min(Bottom, other.Bottom);
        return right > x && bottom > y ? new PixelRect(x, y, right - x, bottom - y) : default;
    }
}

/// <summary>Pixels in BGRA order, top row first, 4 bytes per pixel, no padding.</summary>
public sealed class ScreenImage(int width, int height, byte[] pixels)
{
    public int Width { get; } = width;
    public int Height { get; } = height;
    public byte[] Pixels { get; } = pixels;
    public int Stride => Width * 4;

    public ScreenImage Crop(PixelRect area)
    {
        area = area.Intersect(new PixelRect(0, 0, Width, Height));
        if (area.IsEmpty) throw new ArgumentException("The area is outside the image.", nameof(area));

        var result = new byte[area.Width * area.Height * 4];
        for (int row = 0; row < area.Height; row++)
            Buffer.BlockCopy(Pixels, (area.Y + row) * Stride + area.X * 4, result, row * area.Width * 4, area.Width * 4);
        return new ScreenImage(area.Width, area.Height, result);
    }

    /// <summary>Fingerprint of one row between two columns, for comparing frames quickly.</summary>
    public ulong RowHash(int row, int fromX, int toX)
    {
        var span = MemoryMarshal.Cast<byte, uint>(Pixels.AsSpan(row * Stride + fromX * 4, (toX - fromX) * 4));
        ulong hash = 14695981039346656037UL;
        foreach (uint pixel in span)
        {
            hash ^= pixel & 0x00FFFFFF; // ignore alpha: desktop captures don't always fill it
            hash *= 1099511628211UL;
        }

        return hash;
    }

    public bool SameAs(ScreenImage other) =>
        other.Width == Width && other.Height == Height && Pixels.AsSpan().SequenceEqual(other.Pixels);
}
