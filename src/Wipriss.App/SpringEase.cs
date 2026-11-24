using System.Windows.Media.Animation;

namespace Wipriss.App;

/// <summary>
/// Spring-like easing: a quick start and a soft landing, like a physical part settling into place.
/// Damping below 1 adds a small overshoot (0.78 gives about 1.5 %); 1 settles without one.
/// Frequency sets how fast it gets there within the animation's duration.
/// A plain class, not a Freezable, so animations can share one instance.
/// </summary>
internal sealed class SpringEase : IEasingFunction
{
    private readonly double _damping;
    private readonly double _frequency;
    private readonly double _end;

    public SpringEase(double damping, double frequency)
    {
        _damping = damping;
        _frequency = frequency;
        _end = Raw(1);
    }

    public double Ease(double normalizedTime)
    {
        double t = normalizedTime;
        if (t <= 0) return 0;
        if (t >= 1) return 1;
        // The spring is not fully at rest at t = 1; spread that tiny leftover so the curve ends exactly on 1.
        return Raw(t) + t * (1 - _end);
    }

    private double Raw(double t)
    {
        double decay = Math.Exp(-_damping * _frequency * t);
        if (_damping >= 1) return 1 - decay * (1 + _frequency * t);

        double damped = _frequency * Math.Sqrt(1 - _damping * _damping);
        return 1 - decay * (Math.Cos(damped * t) + _damping * _frequency / damped * Math.Sin(damped * t));
    }
}
