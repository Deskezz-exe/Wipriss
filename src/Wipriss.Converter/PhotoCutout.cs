using ImageMagick;

namespace Wipriss.Converter;

/// <summary>
/// Plain-backdrop removal for photos and big shapes (a portrait on a white wall, a product on a sheet,
/// a solid logo). Unlike the signature method it never makes the subject see-through: only the
/// backdrop that is connected to the picture's edge is removed, everything else stays fully opaque,
/// and just a thin band along the outline gets soft alpha so the edge is not jagged or fringed.
/// </summary>
public static class PhotoCutout
{
    /// <summary>Pixels this close to the outline (on either side) get soft alpha.</summary>
    private const int EdgeBand = 3;

    /// <summary>How far inside to look for the subject's own colour next to an edge pixel.</summary>
    private const int ReferenceRadius = 5;

    /// <summary>Foreground bits smaller than this share of the picture are dust or JPEG noise.</summary>
    private const double SpeckShare = 0.0005;

    /// <summary>
    /// Which part of the picture is not backdrop: the share of pixels the edge flood can't reach.
    /// A signature or thin text covers little; a portrait or a solid logo covers a lot.
    /// </summary>
    public static double SubjectShare(byte[] rgba, int width, int height, out (byte R, byte G, byte B) backdrop, out int threshold)
    {
        backdrop = BorderColor(rgba, width, height);
        threshold = Threshold(rgba, width, height, backdrop);
        var background = Flood(rgba, width, height, backdrop, threshold);
        int count = 0;
        foreach (bool b in background) if (!b) count++;
        return (double)count / background.Length;
    }

    /// <summary>A subject covering at least this share is a photo or a solid shape, not a signature.</summary>
    public const double PhotoShare = 0.12;

    /// <summary>
    /// "Убрать однотонный фон": picks the method. Thin ink (signatures, stamps, line art) goes to the
    /// Filee algorithm, which also clears the paper inside letter loops; photos and solid shapes go to
    /// the cut-out, which keeps the subject fully opaque.
    /// </summary>
    public static void RemovePlainBackground(MagickImage image, int signatureTolerance)
    {
        int width = (int)image.Width, height = (int)image.Height;
        if (width < 3 || height < 3) return;
        if (image.ColorSpace is not (ColorSpace.sRGB or ColorSpace.RGB)) image.ColorSpace = ColorSpace.sRGB;

        byte[]? rgba;
        using (var pixels = image.GetPixels()) rgba = pixels.ToByteArray(PixelMapping.RGBA);
        if (rgba is null) return;

        if (SubjectShare(rgba, width, height, out _, out _) < PhotoShare)
        {
            BackgroundRemover.Apply(image, signatureTolerance, keepEnclosed: false);
            return;
        }

        Apply(rgba, width, height);
        image.Alpha(AlphaAction.Set);
        image.ImportPixels(rgba, new PixelImportSettings((uint)width, (uint)height, StorageType.Char, PixelMapping.RGBA));
    }

    /// <summary>The same on a raw RGBA buffer (also what the tests use).</summary>
    public static void Apply(byte[] rgba, int width, int height)
    {
        var bg = BorderColor(rgba, width, height);
        int threshold = Threshold(rgba, width, height, bg);
        var background = Flood(rgba, width, height, bg, threshold);
        DropSpecks(background, width, height);

        // Distance to the outline, up to the band width, on both sides of it.
        var near = NearOutline(background, width, height);

        for (int i = 0; i < background.Length; i++)
        {
            int o = i * 4;
            if (near[i] == 0)
            {
                if (background[i]) rgba[o + 3] = 0;
                continue;
            }

            // Along the outline a pixel is a blend of the subject and the backdrop (plus any light halo that
            // sharpening or JPEG left there). Its alpha is how far it lies from the backdrop towards the
            // subject's own colour just inside, and then the backdrop is "un-mixed" out of it, so a pale rim
            // becomes a soft edge of the subject's colour instead of a light fringe.
            double alpha = Reference(rgba, background, near, width, height, i) is { } subject
                ? Projection(rgba, o, bg, subject)
                : Math.Clamp((Distance(rgba, o, bg) - threshold * 0.5) / (threshold * 1.5), 0, 1);
            if (background[i]) alpha = Math.Min(alpha, 0.5); // outside the outline it is never solid
            if (alpha <= 0.02)
            {
                rgba[o + 3] = 0;
                continue;
            }
            if (alpha < 1)
            {
                rgba[o] = Unmix(rgba[o], bg.R, alpha);
                rgba[o + 1] = Unmix(rgba[o + 1], bg.G, alpha);
                rgba[o + 2] = Unmix(rgba[o + 2], bg.B, alpha);
            }
            rgba[o + 3] = (byte)Math.Round(alpha * rgba[o + 3]);
        }
    }

    /// <summary>The colour of the nearest solid subject pixel beyond the edge band, if there is one close by.</summary>
    private static (int R, int G, int B)? Reference(byte[] rgba, bool[] background, byte[] near, int width, int height, int i)
    {
        int cx = i % width, cy = i / width, best = int.MaxValue, found = -1;
        for (int dy = -ReferenceRadius; dy <= ReferenceRadius; dy++)
        {
            int y = cy + dy;
            if (y < 0 || y >= height) continue;
            for (int dx = -ReferenceRadius; dx <= ReferenceRadius; dx++)
            {
                int x = cx + dx;
                if (x < 0 || x >= width) continue;
                int n = y * width + x;
                if (background[n] || near[n] != 0) continue;
                int dist = dx * dx + dy * dy;
                if (dist < best) { best = dist; found = n; }
            }
        }
        if (found < 0) return null;
        int o = found * 4;
        return (rgba[o], rgba[o + 1], rgba[o + 2]);
    }

    /// <summary>Where the pixel lies on the line from the backdrop (0) to the subject colour (1).</summary>
    private static double Projection(byte[] rgba, int o, (byte R, byte G, byte B) bg, (int R, int G, int B) subject)
    {
        double sr = subject.R - bg.R, sg = subject.G - bg.G, sb = subject.B - bg.B;
        double length = sr * sr + sg * sg + sb * sb;
        if (length < 30 * 30) return 1; // the subject is nearly the backdrop's colour: leave the pixel be
        double t = ((rgba[o] - bg.R) * sr + (rgba[o + 1] - bg.G) * sg + (rgba[o + 2] - bg.B) * sb) / length;
        return Math.Clamp(t, 0, 1);
    }

    private static byte Unmix(byte value, byte backdrop, double alpha) =>
        (byte)Math.Clamp(Math.Round(backdrop + (value - backdrop) / alpha), 0, 255);

    private static int Distance(byte[] rgba, int o, (byte R, byte G, byte B) bg) =>
        Math.Max(Math.Abs(rgba[o] - bg.R), Math.Max(Math.Abs(rgba[o + 1] - bg.G), Math.Abs(rgba[o + 2] - bg.B)));

    /// <summary>Per-channel median of the border: shrugs off a subject touching the edge.</summary>
    private static (byte R, byte G, byte B) BorderColor(byte[] rgba, int width, int height)
    {
        var r = new List<byte>(); var g = new List<byte>(); var b = new List<byte>();
        foreach (int i in Border(width, height))
        {
            int o = i * 4;
            r.Add(rgba[o]); g.Add(rgba[o + 1]); b.Add(rgba[o + 2]);
        }
        r.Sort(); g.Sort(); b.Sort();
        return (r[r.Count / 2], g[g.Count / 2], b[b.Count / 2]);
    }

    /// <summary>
    /// How far from the backdrop colour still counts as backdrop: above the noise of the backdrop itself
    /// (JPEG, grain, a little uneven light), measured on the half of the border closest to it.
    /// </summary>
    private static int Threshold(byte[] rgba, int width, int height, (byte R, byte G, byte B) bg)
    {
        var d = Border(width, height).Select(i => Distance(rgba, i * 4, bg)).OrderBy(x => x).ToList();
        int noise = d[d.Count / 2]; // the median: the backdrop part of the border, even if the subject touches it
        return Math.Clamp(24 + noise * 3, 28, 64);
    }

    private static IEnumerable<int> Border(int width, int height)
    {
        for (int x = 0; x < width; x++) { yield return x; yield return (height - 1) * width + x; }
        for (int y = 1; y < height - 1; y++) { yield return y * width; yield return y * width + width - 1; }
    }

    /// <summary>Backdrop: pixels close to its colour that the picture's edge reaches (4-connected).</summary>
    private static bool[] Flood(byte[] rgba, int width, int height, (byte R, byte G, byte B) bg, int threshold)
    {
        int count = width * height;
        var reached = new bool[count];
        var queue = new Queue<int>();
        void Visit(int i)
        {
            if (reached[i]) return;
            int o = i * 4;
            if (rgba[o + 3] >= 128 && Distance(rgba, o, bg) >= threshold) return;
            reached[i] = true;
            queue.Enqueue(i);
        }
        foreach (int i in Border(width, height)) Visit(i);
        while (queue.TryDequeue(out int i))
        {
            int x = i % width;
            if (x > 0) Visit(i - 1);
            if (x < width - 1) Visit(i + 1);
            if (i >= width) Visit(i - width);
            if (i < count - width) Visit(i + width);
        }
        return reached;
    }

    /// <summary>Small islands of "subject" in the backdrop (dust, noise) become backdrop too.</summary>
    private static void DropSpecks(bool[] background, int width, int height)
    {
        int count = background.Length;
        int least = Math.Max(16, (int)(count * SpeckShare));
        var seen = new bool[count];
        var part = new List<int>();
        var queue = new Queue<int>();
        for (int start = 0; start < count; start++)
        {
            if (background[start] || seen[start]) continue;
            part.Clear();
            seen[start] = true;
            queue.Enqueue(start);
            while (queue.TryDequeue(out int i))
            {
                part.Add(i);
                int x = i % width;
                void Visit(int n) { if (!background[n] && !seen[n]) { seen[n] = true; queue.Enqueue(n); } }
                if (x > 0) Visit(i - 1);
                if (x < width - 1) Visit(i + 1);
                if (i >= width) Visit(i - width);
                if (i < count - width) Visit(i + width);
            }
            if (part.Count < least)
                foreach (int i in part) background[i] = true;
        }
    }

    /// <summary>
    /// For each pixel next to the outline, how close it is: <see cref="EdgeBand"/> right at the outline,
    /// 1 a step further, 0 beyond the band. Counted on both sides.
    /// </summary>
    private static byte[] NearOutline(bool[] background, int width, int height)
    {
        int count = background.Length;
        var near = new byte[count];
        var frontier = new List<int>();
        for (int i = 0; i < count; i++)
        {
            int x = i % width;
            bool edge = (x > 0 && background[i - 1] != background[i]) ||
                        (x < width - 1 && background[i + 1] != background[i]) ||
                        (i >= width && background[i - width] != background[i]) ||
                        (i < count - width && background[i + width] != background[i]);
            if (edge) { near[i] = EdgeBand; frontier.Add(i); }
        }
        for (int step = EdgeBand - 1; step >= 1; step--)
        {
            var next = new List<int>();
            foreach (int i in frontier)
            {
                int x = i % width;
                void Visit(int n) { if (near[n] == 0) { near[n] = (byte)step; next.Add(n); } }
                if (x > 0) Visit(i - 1);
                if (x < width - 1) Visit(i + 1);
                if (i >= width) Visit(i - width);
                if (i < count - width) Visit(i + width);
            }
            frontier = next;
        }
        return near;
    }
}
