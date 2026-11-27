using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace Wipriss.Modules.Agents;

/// <summary>
/// The home-screen card: which session, its plan with ticks and a spinner on the current step,
/// and what it is doing right now. Rebuilt by the island whenever the session changes.
/// </summary>
internal sealed class AgentCard : UserControl
{
    public static readonly Brush AgentBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xD9, 0x77, 0x57)));
    private const int MaxRows = 5;

    public AgentCard(AgentsModule module)
    {
        var sessions = module.Visible(DateTime.Now);
        var root = new DockPanel { LastChildFill = true };
        Content = root;
        if (sessions.Count == 0) return;
        var s = sessions[0];

        // Header: mark, name, project; on the right the state.
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        DockPanel.SetDock(header, System.Windows.Controls.Dock.Top);
        header.Children.Add(Docked(new Viewbox
        {
            Width = 14, Height = 14, VerticalAlignment = VerticalAlignment.Center,
            Child = new Path { Data = AgentsModule.Mark, Fill = AgentBrush, Width = 24, Height = 24 },
        }, System.Windows.Controls.Dock.Left));
        header.Children.Add(Docked(StateChip(s.Phase), System.Windows.Controls.Dock.Right));
        var title = new TextBlock { Margin = new Thickness(8, 0, 8, 1), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        title.Inlines.Add(new System.Windows.Documents.Run("Claude Code") { FontWeight = FontWeights.SemiBold });
        if (s.Project.Length > 0)
            title.Inlines.Add(new System.Windows.Documents.Run("  " + s.Project) { Foreground = Res("SubtleTextBrush"), FontSize = 12 });
        header.Children.Add(title);
        root.Children.Add(header);

        // Footer: other sessions, and what it is doing when the plan does not say.
        string footer = sessions.Count > 1 ? $"Ещё сессий: {sessions.Count - 1}" : "";
        var rows = Window(s.Steps, out int hiddenDone);
        bool hasPlan = rows.Count > 0;
        if (s.Phase == Phase.Working && s.Doing.Length > 0 && (!hasPlan || rows.Count < MaxRows))
            footer = footer.Length > 0 ? $"{s.Doing} · {footer}" : s.Doing;
        if (footer.Length > 0)
        {
            var foot = Small(footer, "FaintTextBrush");
            foot.Margin = new Thickness(0, 6, 0, 0);
            DockPanel.SetDock(foot, System.Windows.Controls.Dock.Bottom);
            root.Children.Add(foot);
        }

        var list = new StackPanel();
        if (hiddenDone > 0) list.Children.Add(StepRow(new Step($"Готово шагов: {hiddenDone}", StepState.Done), muted: true));
        foreach (var step in rows) list.Children.Add(StepRow(step, muted: false));
        if (!hasPlan)
        {
            list.Children.Add(new TextBlock
            {
                Text = s.Phase switch
                {
                    Phase.Working => s.Headline,
                    Phase.Waiting => "Ждёт вашего ответа",
                    _ => "Закончил работу",
                },
                FontSize = 13, TextWrapping = TextWrapping.Wrap, MaxHeight = 54, TextTrimming = TextTrimming.CharacterEllipsis,
            });
        }
        root.Children.Add(list);
    }

    /// <summary>
    /// At most five rows around the current step: done ones collapse into one line when they do not fit.
    /// </summary>
    private static List<Step> Window(Step[] steps, out int hiddenDone)
    {
        hiddenDone = 0;
        if (steps.Length <= MaxRows) return [.. steps];
        int current = Array.FindIndex(steps, x => x.State != StepState.Done);
        if (current < 0) current = steps.Length - 1;
        int start = Math.Max(0, Math.Min(current - 1, steps.Length - (MaxRows - 1)));
        hiddenDone = start;
        return [.. steps.Skip(start).Take(hiddenDone > 0 ? MaxRows - 1 : MaxRows)];
    }

    private static FrameworkElement StepRow(Step step, bool muted)
    {
        var row = new DockPanel { Margin = new Thickness(0, 1, 0, 1), Height = 18 };
        FrameworkElement mark = step.State switch
        {
            StepState.Done => new Path
            {
                Data = Geometry.Parse("M2.5,7.5 L5.8,10.5 L11.5,3.8"), Stroke = Res("OkBrush"), StrokeThickness = 1.6,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round,
                Width = 14, Height = 14,
            },
            StepState.Active => Spinner(),
            _ => new Ellipse { Width = 4, Height = 4, Fill = Res("FaintTextBrush"), HorizontalAlignment = HorizontalAlignment.Center },
        };
        var markBox = new Grid { Width = 14, Height = 14, Margin = new Thickness(0, 0, 9, 0), VerticalAlignment = VerticalAlignment.Center };
        markBox.Children.Add(mark);
        row.Children.Add(Docked(markBox, System.Windows.Controls.Dock.Left));

        row.Children.Add(new TextBlock
        {
            Text = step.Text,
            FontSize = 12.5,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = muted ? Res("FaintTextBrush")
                : step.State switch
                {
                    StepState.Active => Res("TextBrush"),
                    StepState.Done => Res("SubtleTextBrush"),
                    _ => Res("FaintTextBrush"),
                },
            FontWeight = step.State == StepState.Active && !muted ? FontWeights.SemiBold : FontWeights.Normal,
        });
        return row;
    }

    private static FrameworkElement StateChip(Phase phase)
    {
        var (text, brush) = phase switch
        {
            Phase.Working => ("работает", "BusyBrush"),
            Phase.Waiting => ("ждёт вас", "BusyBrush"),
            _ => ("готово", "OkBrush"),
        };
        return new TextBlock
        {
            Text = text, FontSize = 11.5, Foreground = Res(brush), VerticalAlignment = VerticalAlignment.Center,
            FontFamily = (FontFamily)Application.Current.FindResource("SmallFont"),
        };
    }

    private static Path Spinner()
    {
        var turn = new RotateTransform();
        var path = new Path
        {
            Data = Geometry.Parse("M7,1.5 A5.5,5.5 0 1 1 1.5,7"), Stroke = Res("BusyBrush"), StrokeThickness = 1.6,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, Width = 14, Height = 14,
            RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = turn,
        };
        var spin = new DoubleAnimation(0, 360, TimeSpan.FromSeconds(0.9)) { RepeatBehavior = RepeatBehavior.Forever };
        Timeline.SetDesiredFrameRate(spin, 15);
        // Stops by itself when the card is dropped (the island clears its cards on close).
        path.Loaded += (_, _) => turn.BeginAnimation(RotateTransform.AngleProperty, spin);
        path.Unloaded += (_, _) => turn.BeginAnimation(RotateTransform.AngleProperty, null);
        return path;
    }

    private static TextBlock Small(string text, string brush) => new()
    {
        Text = text, FontSize = 11.5, Foreground = Res(brush), TextTrimming = TextTrimming.CharacterEllipsis,
        FontFamily = (FontFamily)Application.Current.FindResource("SmallFont"),
    };

    private static T Docked<T>(T element, System.Windows.Controls.Dock dock) where T : UIElement
    {
        DockPanel.SetDock(element, dock);
        return element;
    }

    private static Brush Res(string key) => (Brush)Application.Current.FindResource(key);

    private static T Frozen<T>(T f) where T : Freezable
    {
        f.Freeze();
        return f;
    }
}
