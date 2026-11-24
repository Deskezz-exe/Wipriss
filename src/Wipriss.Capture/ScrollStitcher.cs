namespace Wipriss.Capture;

public enum StitchStep
{
    /// <summary>The frame moved and its new rows were added.</summary>
    Added,
    /// <summary>Nothing moved: usually the end of the page.</summary>
    NoChange,
    /// <summary>The frame can't be matched to the previous one (scrolled too far, or the content changed).</summary>
    Lost,
}

/// <summary>
/// Glues screenshots of a scrolling area into one tall image. Each new frame is matched to the previous one
/// by comparing row fingerprints, so it does not matter how far one wheel step scrolls.
/// Rows that never move at the top and bottom (a sticky header, a footer bar, browser toolbars) are kept once.
/// Columns near the left and right edges are ignored when matching: that is where scroll bars move.
/// </summary>
public sealed class ScrollStitcher
{
    public const int MinOverlap = 24;

    private readonly int _width, _height, _fromX, _toX;
    private readonly List<byte[]> _chunks = [];
    private int _rows;
    private ulong[] _previous = [];
    private ScreenImage? _last;
    private int _top = -1, _bottom = -1;

    public ScrollStitcher(int width, int height)
    {
        if (width < 16 || height < MinOverlap * 2) throw new ArgumentException("The area is too small to scroll.");
        _width = width;
        _height = height;
        int margin = Math.Min(40, width / 8); // a scroll bar is about 26 px wide at 150 %, 34 px at 200 %
        _fromX = margin;
        _toX = width - margin;
    }

    /// <summary>Height of the image so far.</summary>
    public int Height => _rows + Math.Max(0, _bottom);

    /// <summary>How far the last frame moved, in pixels.</summary>
    public int LastShift { get; private set; }

    public StitchStep Add(ScreenImage frame)
    {
        if (frame.Width != _width || frame.Height != _height) throw new ArgumentException("All frames must be the same size.", nameof(frame));
        var hashes = Hashes(frame);

        if (_last is null)
        {
            _chunks.Add((byte[])frame.Pixels.Clone());
            _rows = _height;
            Remember(frame, hashes);
            return StitchStep.Added;
        }

        if (hashes.AsSpan().SequenceEqual(_previous)) return StitchStep.NoChange;

        int top = _top, bottom = _bottom;
        if (top < 0)
        {
            // Rows that stayed exactly in place at the edges are fixed bars, not page content.
            top = 0;
            while (top < _height / 3 && hashes[top] == _previous[top]) top++;
            bottom = 0;
            while (bottom < _height / 3 && hashes[_height - 1 - bottom] == _previous[_height - 1 - bottom]) bottom++;
        }

        int shift = FindShift(hashes, top, bottom);
        if (shift <= 0) shift = FindShiftApproximately(Profiles(frame), top, bottom);
        if (shift <= 0) return StitchStep.Lost;

        if (_top < 0)
        {
            _top = top;
            _bottom = bottom;
            // The first frame's bottom bar goes to the very end of the image instead.
            if (bottom > 0)
            {
                _chunks[0] = _chunks[0].AsSpan(0, (_height - bottom) * _width * 4).ToArray();
                _rows -= bottom;
            }
        }

        // New content is the strip that just scrolled in above the bottom bar.
        int areaEnd = _height - _bottom;
        _chunks.Add(frame.Pixels.AsSpan((areaEnd - shift) * _width * 4, shift * _width * 4).ToArray());
        _rows += shift;
        LastShift = shift;
        Remember(frame, hashes);
        return StitchStep.Added;
    }

    /// <summary>The finished image: everything stitched so far plus the bottom bar.</summary>
    public ScreenImage Build()
    {
        if (_last is null) throw new InvalidOperationException("No frames yet.");
        int footer = Math.Max(0, _bottom);
        var pixels = new byte[(_rows + footer) * _width * 4];
        int offset = 0;
        foreach (var chunk in _chunks)
        {
            Buffer.BlockCopy(chunk, 0, pixels, offset, chunk.Length);
            offset += chunk.Length;
        }

        if (footer > 0)
            Buffer.BlockCopy(_last.Pixels, (_height - footer) * _width * 4, pixels, offset, footer * _width * 4);
        return new ScreenImage(_width, _rows + footer, pixels);
    }

    /// <summary>
    /// How many rows the content moved up. Only "informative" rows count (rows that differ from the row above):
    /// flat areas fit any shift. Ties go to the shift closest to the previous step.
    /// </summary>
    private int FindShift(ulong[] now, int top, int bottom)
    {
        int area = _height - top - bottom;
        int best = 0, bestMatches = 0;
        double bestRatio = 0;
        for (int d = 1; d <= area - MinOverlap; d++)
        {
            int informative = 0, matches = 0;
            for (int i = 0; i < area - d; i++)
            {
                int before = top + i + d;
                if (_previous[before] == _previous[before - 1]) continue;
                informative++;
                if (now[top + i] == _previous[before]) matches++;
            }

            if (informative < 8) continue;
            double ratio = (double)matches / informative;
            if (ratio < 0.9) continue;

            bool better = ratio > bestRatio + 0.001
                || (Math.Abs(ratio - bestRatio) <= 0.001 && LastShift > 0 && Math.Abs(d - LastShift) < Math.Abs(best - LastShift))
                || (Math.Abs(ratio - bestRatio) <= 0.001 && LastShift == 0 && matches > bestMatches);
            if (better)
            {
                best = d;
                bestRatio = ratio;
                bestMatches = matches;
            }
        }

        return best;
    }

    private const int Buckets = 64;

    /// <summary>
    /// For pictures Windows scales (old programs on a 150 % screen) or that land between pixels after a scroll:
    /// rows are blurred a little differently each time, so exact fingerprints never match. Each row is reduced to
    /// 64 brightness averages; the shift with the smallest difference wins, but only if it clearly beats every
    /// other shift that is not right next to it. Otherwise nothing is glued: better stop than glue wrong.
    /// </summary>
    private int FindShiftApproximately(float[][] now, int top, int bottom)
    {
        var before = Profiles(_last!);
        int area = _height - top - bottom;
        var informative = new bool[_height];
        for (int row = top + 1; row < _height - bottom; row++)
            informative[row] = Distance(before[row], before[row - 1]) > 2 * Buckets;

        var scores = new double[area + 1];
        for (int d = 1; d <= area - MinOverlap; d++)
        {
            double sum = 0;
            int count = 0;
            for (int i = 0; i < area - d; i++)
            {
                int row = top + i + d;
                if (!informative[row]) continue;
                sum += Distance(now[top + i], before[row]);
                count++;
            }

            scores[d] = count >= 8 ? sum / count : double.MaxValue;
        }

        int best = 0;
        for (int d = 1; d <= area - MinOverlap; d++)
        {
            if (scores[d] == double.MaxValue) continue;
            bool tie = best > 0 && scores[d] <= scores[best] * 1.02;
            if (best == 0 || scores[d] < scores[best] * 0.98
                || (tie && LastShift > 0 && Math.Abs(d - LastShift) < Math.Abs(best - LastShift)))
                best = d;
        }

        if (best == 0) return 0;
        double second = double.MaxValue;
        for (int d = 1; d <= area - MinOverlap; d++)
            if (Math.Abs(d - best) > 3 && scores[d] < second) second = scores[d];

        // A clear winner (half the error of any other shift) that is also close in absolute terms.
        return scores[best] < second * 0.5 && scores[best] < 24 * Buckets ? best : 0;
    }

    private float[][] Profiles(ScreenImage frame)
    {
        var profiles = new float[_height][];
        int span = _toX - _fromX;
        for (int row = 0; row < _height; row++)
        {
            var profile = new float[Buckets];
            int offset = row * frame.Stride;
            for (int b = 0; b < Buckets; b++)
            {
                int from = _fromX + span * b / Buckets, to = _fromX + span * (b + 1) / Buckets;
                long sum = 0;
                for (int x = from; x < to; x++)
                {
                    int i = offset + x * 4;
                    sum += frame.Pixels[i] + 2 * frame.Pixels[i + 1] + frame.Pixels[i + 2]; // rough brightness
                }

                profile[b] = to > from ? sum / (4f * (to - from)) : 0;
            }

            profiles[row] = profile;
        }

        return profiles;
    }

    private static double Distance(float[] a, float[] b)
    {
        double sum = 0;
        for (int k = 0; k < Buckets; k++) sum += Math.Abs(a[k] - b[k]);
        return sum;
    }

    private ulong[] Hashes(ScreenImage frame)
    {
        var hashes = new ulong[_height];
        for (int row = 0; row < _height; row++) hashes[row] = frame.RowHash(row, _fromX, _toX);
        return hashes;
    }

    private void Remember(ScreenImage frame, ulong[] hashes)
    {
        _last = frame;
        _previous = hashes;
    }
}
