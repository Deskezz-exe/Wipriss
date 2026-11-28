using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using Wipriss.Core.Ui;

namespace Wipriss.Modules.Agents;

internal enum AgentState { None, Working, Waiting, Done }

/// <summary>
/// An agent in the closed pill: its small one-colour logo and a mark: a breathing blue dot while it works,
/// a pulsing amber dot while it waits for you, a green tick when it is done. Marks move only while seen.
/// </summary>
internal sealed class AgentPill : StackPanel
{
    private readonly Grid _mark = new() { Width = 10, Height = 14, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
    private AgentState _state;
    private bool _shown;
    private Ellipse? _dot;

    public AgentPill(string agent)
    {
        Orientation = Orientation.Horizontal;
        VerticalAlignment = VerticalAlignment.Center;
        Children.Add(Logo(agent, PillLook.Text, 13));
        Children.Add(_mark);
    }

    public static FrameworkElement Logo(string agent, Brush brush, double size)
    {
        if (agent == "claude")
            return new Viewbox
            {
                Width = size, Height = size, VerticalAlignment = VerticalAlignment.Center,
                Child = new Path { Data = AgentsModule.Mark, Fill = brush, Width = 24, Height = 24 },
            };
        // Codex: a terminal prompt in a rounded square, drawn in lines.
        return new Viewbox
        {
            Width = size, Height = size, VerticalAlignment = VerticalAlignment.Center,
            Child = new Path
            {
                Data = CodexMark, Stroke = brush, StrokeThickness = 1.3, Width = 16, Height = 16,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round,
            },
        };
    }

    public static string AgentName(string agent) => agent == "claude" ? "Claude Code" : "Codex";

    private static readonly Geometry CodexMark = Frozen(Geometry.Parse(
        "M4,2 L12,2 A2,2 0 0 1 14,4 L14,12 A2,2 0 0 1 12,14 L4,14 A2,2 0 0 1 2,12 L2,4 A2,2 0 0 1 4,2 Z M5,6 L7,8 L5,10 M8.5,10.5 L11,10.5"));

    public void Show(AgentState state)
    {
        if (state == _state) return;
        _state = state;
        _mark.Children.Clear();
        _dot = null;
        switch (state)
        {
            case AgentState.Working:
            case AgentState.Waiting:
                _dot = new Ellipse
                {
                    Width = 7, Height = 7, VerticalAlignment = VerticalAlignment.Center,
                    Fill = state == AgentState.Waiting ? PillLook.Amber : PillLook.Blue,
                };
                _mark.Children.Add(_dot);
                break;
            case AgentState.Done:
                _mark.Children.Add(Tick());
                break;
        }
        Animate(_shown);
    }

    /// <summary>
    /// Only "waits for you" pulses. Work can go on for hours, so its dot stands still: every animation frame
    /// redraws the island.
    /// </summary>
    public void Animate(bool shown)
    {
        _shown = shown;
        if (_dot is null) return;
        if (!shown || _state != AgentState.Waiting)
        {
            _dot.BeginAnimation(OpacityProperty, null);
            _dot.Opacity = 1;
            return;
        }
        _dot.BeginAnimation(OpacityProperty, Pulse(0.8));
    }

    /// <summary>A soft pulse at a low frame rate: the pill stays cheap while an agent works for hours.</summary>
    public static DoubleAnimation Pulse(double seconds)
    {
        var pulse = new DoubleAnimation(1, 0.3, TimeSpan.FromSeconds(seconds))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        };
        Timeline.SetDesiredFrameRate(pulse, 10);
        return pulse;
    }

    public static Path Tick() => new()
    {
        Data = Frozen(Geometry.Parse("M1.5,7.5 L4.3,10.2 L9,4")),
        Stroke = PillLook.Green, StrokeThickness = 1.7, Width = 10, Height = 14,
        StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round,
        VerticalAlignment = VerticalAlignment.Center,
    };

    /// <summary>The wider moment: logo, "Claude Code ждёт подтверждения · project", a pulsing amber dot.</summary>
    public static FrameworkElement Peek(string agent, string text, string project)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(Logo(agent, PillLook.Text, 14));
        var label = PillLook.Label($"{AgentName(agent)} {text}");
        label.Margin = new Thickness(9, 0, 0, 1);
        row.Children.Add(label);
        if (project.Length > 0)
        {
            var where = PillLook.Label($" · {project}", PillLook.Subtle);
            where.Margin = new Thickness(0, 0, 0, 1);
            where.MaxWidth = 160;
            row.Children.Add(where);
        }
        var dot = new Ellipse { Width = 7, Height = 7, Fill = PillLook.Amber, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        dot.BeginAnimation(OpacityProperty, Pulse(0.8));
        row.Children.Add(dot);
        return row;
    }

    private static Geometry Frozen(Geometry g)
    {
        g.Freeze();
        return g;
    }
}
