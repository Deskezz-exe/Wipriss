namespace Wipriss.Voice;

/// <summary>
/// Cuts a long recording into pieces the model handles well (GigaAM is trained on short phrases).
/// Each cut goes into the quietest 100 ms between <see cref="MinSeconds"/> and
/// <see cref="MaxSeconds"/>, so words are not split in the middle.
/// </summary>
internal static class SpeechChunker
{
    public const int MinSeconds = 12;
    public const int MaxSeconds = 20;

    /// <summary>Below this level (about -54 dBFS) the whole recording counts as silence.</summary>
    public const float SilenceRms = 0.002f;

    private const int Frame = Microphone.SampleRate / 10;

    public static List<(int Start, int Length)> Split(ReadOnlySpan<float> samples, int sampleRate = Microphone.SampleRate)
    {
        var pieces = new List<(int, int)>();
        if (IsSilence(samples)) return pieces;
        int frame = sampleRate / 10;
        int min = MinSeconds * sampleRate, max = MaxSeconds * sampleRate;
        int start = 0;
        while (samples.Length - start > max)
        {
            int best = start + max, bestFrom = start + min;
            double quietest = double.MaxValue;
            for (int at = bestFrom; at + frame <= start + max; at += frame)
            {
                double energy = Energy(samples.Slice(at, frame));
                if (energy < quietest) { quietest = energy; best = at + frame / 2; }
            }
            pieces.Add((start, best - start));
            start = best;
        }
        if (samples.Length > start) pieces.Add((start, samples.Length - start));
        return pieces;
    }

    public static bool IsSilence(ReadOnlySpan<float> samples)
    {
        double limit = (double)SilenceRms * SilenceRms;
        for (int at = 0; at < samples.Length; at += Frame)
        {
            var frame = samples.Slice(at, Math.Min(Frame, samples.Length - at));
            if (Energy(frame) > limit) return false;
        }
        return true;
    }

    private static double Energy(ReadOnlySpan<float> frame)
    {
        if (frame.IsEmpty) return 0;
        double sum = 0;
        foreach (var s in frame) sum += (double)s * s;
        return sum / frame.Length;
    }
}
