using Wipriss.Capture;

namespace Wipriss.Tests;

public class ScrollStitcherTests
{
    private const int Width = 320, Header = 60, Footer = 40, View = 500, ScrollBar = 12;

    /// <summary>A long page: rows of "text" with blank gaps, every text row different.</summary>
    private static byte[] PageRow(int row)
    {
        var pixels = new byte[Width * 4];
        bool blank = row / 40 % 3 == 2;
        for (int x = 0; x < Width; x++)
        {
            uint value = blank ? 0xFFFFFF : (uint)((row * 7919 + x * 104729) ^ (row * x)) & 0xFFFFFF;
            pixels[x * 4] = (byte)value;
            pixels[x * 4 + 1] = (byte)(value >> 8);
            pixels[x * 4 + 2] = (byte)(value >> 16);
            pixels[x * 4 + 3] = 255;
        }

        return pixels;
    }

    private static byte[] Bar(byte shade)
    {
        var pixels = new byte[Width * 4];
        for (int i = 0; i < pixels.Length; i += 4) { pixels[i] = shade; pixels[i + 1] = shade; pixels[i + 2] = shade; pixels[i + 3] = 255; }
        return pixels;
    }

    /// <summary>What the screen shows at scroll position <paramref name="top"/>: toolbar, page slice, footer, moving scroll bar.</summary>
    private static ScreenImage Frame(int pageHeight, int top)
    {
        int height = Header + View + Footer;
        var pixels = new byte[Width * height * 4];
        for (int y = 0; y < height; y++)
        {
            byte[] row = y < Header ? Bar(40) : y >= Header + View ? Bar(90) : PageRow(top + y - Header);
            Buffer.BlockCopy(row, 0, pixels, y * Width * 4, row.Length);
        }

        // The scroll thumb moves with the page, so the right edge differs between frames.
        int thumbTop = Header + top * View / pageHeight;
        for (int y = Header; y < Header + View; y++)
            for (int x = Width - ScrollBar; x < Width; x++)
            {
                byte shade = y >= thumbTop && y < thumbTop + 60 ? (byte)200 : (byte)240;
                int i = (y * Width + x) * 4;
                pixels[i] = shade; pixels[i + 1] = shade; pixels[i + 2] = shade;
            }

        return new ScreenImage(Width, height, pixels);
    }

    private static void AssertRowsEqual(byte[] expected, ScreenImage image, int y)
    {
        // Compare away from the edges: the scroll bar column comes from different frames.
        var actual = image.Pixels.AsSpan(y * Width * 4 + 24 * 4, (Width - 48) * 4);
        Assert.True(actual.SequenceEqual(expected.AsSpan(24 * 4, (Width - 48) * 4)), $"row {y} differs");
    }

    [Theory]
    [InlineData(new[] { 230, 260, 245, 280, 250, 270, 240, 265, 255 })]
    [InlineData(new[] { 90, 90, 90, 90, 90, 90, 90, 90, 90, 90, 90, 90, 90, 90, 90, 90, 90, 90, 90, 90 })]
    public void Page_is_rebuilt_exactly_with_header_and_footer_once(int[] steps)
    {
        int pageHeight = View + steps.Sum();
        var stitcher = new ScrollStitcher(Width, Header + View + Footer);
        int top = 0;
        Assert.Equal(StitchStep.Added, stitcher.Add(Frame(pageHeight, top)));
        foreach (int step in steps)
        {
            top += step;
            Assert.Equal(StitchStep.Added, stitcher.Add(Frame(pageHeight, top)));
        }

        Assert.Equal(StitchStep.NoChange, stitcher.Add(Frame(pageHeight, top)));

        var image = stitcher.Build();
        Assert.Equal(Header + pageHeight + Footer, image.Height);
        for (int y = 0; y < Header; y++) AssertRowsEqual(Bar(40), image, y);
        for (int r = 0; r < pageHeight; r++) AssertRowsEqual(PageRow(r), image, Header + r);
        for (int y = 0; y < Footer; y++) AssertRowsEqual(Bar(90), image, Header + pageHeight + y);
    }

    [Fact]
    public void Scrolling_further_than_one_screen_is_reported_not_glued_wrong()
    {
        var stitcher = new ScrollStitcher(Width, Header + View + Footer);
        stitcher.Add(Frame(3000, 0));
        Assert.Equal(StitchStep.Lost, stitcher.Add(Frame(3000, View + 100)));
        Assert.Equal(Header + View + Footer, stitcher.Build().Height);
    }

    [Fact]
    public void Crop_takes_the_exact_pixels()
    {
        var frame = Frame(2000, 0);
        var part = frame.Crop(new PixelRect(10, Header + 5, 50, 20));
        Assert.Equal(50, part.Width);
        Assert.Equal(20, part.Height);
        Assert.True(part.Pixels.AsSpan(0, 50 * 4).SequenceEqual(PageRow(5).AsSpan(10 * 4, 50 * 4)));
    }

    /// <summary>Text-like page: strokes a few pixels tall and wide, lines of different length, blank gaps.</summary>
    private static double TextPixel(int row, int x)
    {
        int line = row / 30, inLine = row % 30;
        if (inLine >= 22 || line % 7 == 6) return 245; // gap between lines, empty line
        int length = 120 + (line * 37 % 180);
        if (x >= length) return 245;
        int glyph = (x / 9) * 31 + line * 17;
        bool ink = ((glyph >> (inLine / 3)) & 1) == 1 && x % 9 < 6;
        return ink ? 30 : 245;
    }

    /// <summary>Like a scaled old program: each frame lands at a different sub-pixel position, so rows are blended.</summary>
    private static ScreenImage BlurredFrame(int top, double phase)
    {
        var pixels = new byte[Width * View * 4];
        for (int y = 0; y < View; y++)
            for (int x = 0; x < Width; x++)
            {
                byte v = (byte)Math.Round(TextPixel(top + y, x) * (1 - phase) + TextPixel(top + y + 1, x) * phase);
                int i = (y * Width + x) * 4;
                pixels[i] = v; pixels[i + 1] = v; pixels[i + 2] = v; pixels[i + 3] = 255;
            }

        return new ScreenImage(Width, View, pixels);
    }

    [Fact]
    public void Blurred_frames_still_line_up_closely()
    {
        int[] steps = [180, 175, 190, 185];
        double[] phases = [0, 0.5, 0, 0.5, 0];
        var stitcher = new ScrollStitcher(Width, View);
        int top = 0;
        Assert.Equal(StitchStep.Added, stitcher.Add(BlurredFrame(top, phases[0])));
        for (int i = 0; i < steps.Length; i++)
        {
            top += steps[i];
            Assert.Equal(StitchStep.Added, stitcher.Add(BlurredFrame(top, phases[i + 1])));
            // Within one pixel of the true scroll (a half-pixel blend can't be exact).
            Assert.InRange(stitcher.LastShift, steps[i] - 1, steps[i] + 1);
        }

        Assert.InRange(stitcher.Build().Height, View + steps.Sum() - steps.Length, View + steps.Sum() + steps.Length);
    }
}
