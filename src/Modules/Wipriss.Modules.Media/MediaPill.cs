using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using Wipriss.Core.Ui;

namespace Wipriss.Modules.Media;

/// <summary>
/// The music block of the closed pill: a small cover, the title (cut with "…") and sound bars.
/// The bars move only while music plays and the pill is seen, at a low frame rate.
/// </summary>
internal sealed class MediaPill : StackPanel
{
    private const double TitleWidth = 150;
    private static readonly double[] BarPeriods = [0.42, 0.55, 0.36, 0.48];

    private readonly TextBlock _title;
    private readonly ScaleTransform[] _bars = new ScaleTransform[BarPeriods.Length];
    private bool _animating;

    public MediaPill()
    {
        Orientation = Orientation.Horizontal;
        VerticalAlignment = VerticalAlignment.Center;

        var note = PillLook.IconView(PillLook.MusicNote, PillLook.Subtle, 13);
        note.VerticalAlignment = VerticalAlignment.Center;
        Children.Add(note);

        _title = PillLook.Label("");
        _title.Margin = new Thickness(8, 0, 9, 1);
        _title.MaxWidth = TitleWidth;
        Children.Add(_title);

        var bars = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Height = 12 };
        for (int i = 0; i < _bars.Length; i++)
        {
            _bars[i] = new ScaleTransform(1, 0.35);
            bars.Children.Add(new Rectangle
            {
                Width = 2, Height = 12, RadiusX = 1, RadiusY = 1, Margin = new Thickness(i == 0 ? 0 : 2, 0, 0, 0),
                Fill = PillLook.Green, VerticalAlignment = VerticalAlignment.Center,
                RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = _bars[i],
            });
        }
        Children.Add(bars);
    }

    /// <summary>Shows the song. Returns true when the block's width may have changed.</summary>
    public bool Show(NowPlaying now)
    {
        _title.Foreground = now.Playing ? PillLook.Text : PillLook.Subtle;
        _title.ToolTip = null;
        string old = _title.Text;
        _title.Text = now.Title;
        foreach (var bar in _bars) bar.ScaleY = 0.35;
        return old != now.Title;
    }

    /// <summary>Starts or stops the bars.</summary>
    public void Animate(bool on)
    {
        if (on == _animating) return;
        _animating = on;
        for (int i = 0; i < _bars.Length; i++)
        {
            if (!on)
            {
                _bars[i].BeginAnimation(ScaleTransform.ScaleYProperty, null);
                _bars[i].ScaleY = 0.35;
                continue;
            }
            var bounce = new DoubleAnimation(0.3, 1, TimeSpan.FromSeconds(BarPeriods[i]))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            };
            Timeline.SetDesiredFrameRate(bounce, 8);
            _bars[i].BeginAnimation(ScaleTransform.ScaleYProperty, bounce);
        }
    }
}
