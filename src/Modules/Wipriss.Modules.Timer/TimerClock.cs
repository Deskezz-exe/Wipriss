namespace Wipriss.Modules.Timer;

/// <summary>
/// The timer's state without any UI or ticking: a length, and either a moment it ends (running) or the time left
/// (paused). Everything is computed from the clock when asked.
/// </summary>
internal sealed class TimerClock
{
    public TimeSpan Length { get; private set; } = TimeSpan.FromMinutes(25);

    /// <summary>When it ends, while running.</summary>
    public DateTime? EndsAt { get; private set; }

    private TimeSpan _left = TimeSpan.FromMinutes(25);

    public bool Running => EndsAt is not null;

    /// <summary>Paused part-way (not at the start, not finished).</summary>
    public bool Paused => !Running && _left > TimeSpan.Zero && _left < Length;

    public TimeSpan Left(DateTime now)
    {
        if (EndsAt is { } end) return end > now ? end - now : TimeSpan.Zero;
        return _left;
    }

    /// <summary>Picks a length and starts from the beginning.</summary>
    public void Preset(TimeSpan length, DateTime now)
    {
        Length = length;
        _left = length;
        EndsAt = now + length;
    }

    public void Start(DateTime now)
    {
        if (Running) return;
        if (_left <= TimeSpan.Zero) _left = Length;
        EndsAt = now + _left;
    }

    public void Pause(DateTime now)
    {
        if (EndsAt is not { } end) return;
        _left = end > now ? end - now : TimeSpan.Zero;
        EndsAt = null;
    }

    public void Reset()
    {
        EndsAt = null;
        _left = Length;
    }

    /// <summary>The end came: back to the full length, stopped.</summary>
    public void Finish()
    {
        EndsAt = null;
        _left = Length;
    }

    /// <summary>"25:00", "4:05", "1:02:00".</summary>
    public static string Format(TimeSpan t)
    {
        // Round up: 24:59.4 shows as 25:00 for the first moment, and 0:00 only at the very end.
        long seconds = (long)Math.Ceiling(t.TotalSeconds - 0.05);
        if (seconds < 0) seconds = 0;
        long h = seconds / 3600, m = seconds / 60 % 60, s = seconds % 60;
        return h > 0 ? $"{h}:{m:00}:{s:00}" : $"{m}:{s:00}";
    }

    /// <summary>For the closed pill, which changes once a minute: "25 мин", "1 мин", "<1 мин".</summary>
    public static string Short(TimeSpan t)
    {
        if (t < TimeSpan.FromMinutes(1)) return "<1 мин";
        return $"{(int)Math.Ceiling(t.TotalMinutes - 0.001)} мин";
    }
}
