using ImageMagick;

namespace Wipriss.Converter;

/// <summary>
/// Turns the steps of one conversion into a percentage that only goes up. Each step gets a slice
/// (reading, resizing, every encode); inside a slice ImageMagick's own progress moves the number,
/// and where a codec reports nothing (AVIF, WEBP) the number creeps slowly towards the slice's end,
/// so a long encode never looks frozen. It never reaches 100 before the file is really done.
/// </summary>
public sealed class ProgressMeter : IDisposable
{
    private const double Cap = 99;
    private readonly Action<int>? _report;
    private readonly Lock _gate = new();
    private readonly Timer? _creep;
    private double _start, _end, _value;
    private int _last = -1;
    private long _lastTicks;
    private bool _disposed;

    public ProgressMeter(Action<int>? report)
    {
        _report = report;
        if (report is not null) _creep = new Timer(_ => Creep(), null, 250, 250);
    }

    public int Last => _last;

    /// <summary>The next step runs from <paramref name="start"/> to <paramref name="end"/> percent.</summary>
    public void Step(double start, double end)
    {
        lock (_gate)
        {
            _start = Math.Max(start, _value);
            _end = Math.Max(end, _start);
            Set(_start, force: true);
        }
    }

    /// <summary>An encode of unknown count: takes 30% of what is left up to 95.</summary>
    public void NextEncode()
    {
        double from;
        lock (_gate) from = Math.Max(_value, _end);
        Step(from, from + (95 - from) * 0.3);
    }

    /// <summary>Feeds ImageMagick's progress for this picture into the current step.</summary>
    public void Watch(MagickImage image)
    {
        if (_report is null) return;
        image.Progress += (_, e) =>
        {
            double fraction = Math.Clamp((double)e.Progress / 100, 0, 1);
            lock (_gate) Set(_start + (_end - _start) * fraction, force: false);
        };
    }

    private void Creep()
    {
        lock (_gate)
        {
            if (_end <= _value) return;
            // Covers 4% of the rest of the step every quarter second: quick at first, then slower.
            Set(_value + (_end - _value) * 0.04, force: false);
        }
    }

    private void Set(double value, bool force)
    {
        value = Math.Min(value, Cap);
        if (_disposed || value <= _value) return;
        _value = value;
        int percent = (int)value;
        if (percent <= _last) return;
        long now = Environment.TickCount64;
        // At most ~8 lines a second, unless a step starts.
        if (!force && now - _lastTicks < 120) return;
        _last = percent;
        _lastTicks = now;
        _report?.Invoke(percent);
    }

    public void Dispose()
    {
        // A tick already running must not report after the file is done.
        lock (_gate) _disposed = true;
        _creep?.Dispose();
    }
}
