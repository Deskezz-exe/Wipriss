using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Wipriss.Core.Ui;

namespace Wipriss.Modules.Agents;

/// <summary>
/// The agents on the home screen, one slim card each: logo, name and a status chip ("работает", "ждёт ответа",
/// "простаивает", "неактивен"). While it works, a second line says what it does right now ("Читает Test.html").
/// A click brings the agent's window to the front.
/// </summary>
internal sealed class AgentBlocks : StackPanel
{
    private static readonly Brush ClaudeBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xD9, 0x77, 0x57)));

    public AgentBlocks(AgentsModule module)
    {
        var agents = module.HomeAgents(DateTime.Now);
        for (int i = 0; i < agents.Count; i++)
        {
            var block = Block(agents[i]);
            if (i > 0) block.Margin = new Thickness(0, 8, 0, 0);
            Children.Add(block);
        }
    }

    private static Border Block(AgentView agent)
    {
        var (text, brush) = agent.Status switch
        {
            AgentStatus.Working => ("работает", PillLook.Blue),
            AgentStatus.Waiting => ("ждёт ответа", PillLook.Amber),
            AgentStatus.Idle => ("простаивает", PillLook.Green),
            _ => ("неактивен", PillLook.Faint),
        };

        var card = HomeLook.Card(padding: new Thickness(12, 10, 14, 10));
        card.Cursor = Cursors.Hand;
        card.ToolTip = "Открыть окно агента";
        var normal = card.Background;
        card.MouseEnter += (_, _) => card.Background = HomeLook.Hover;
        card.MouseLeave += (_, _) => card.Background = normal;

        var root = new DockPanel();
        card.Child = root;

        var logo = new Border
        {
            Width = 34, Height = 34, CornerRadius = new CornerRadius(17), Background = HomeLook.Raised, Margin = new Thickness(0, 0, 12, 0),
            VerticalAlignment = VerticalAlignment.Top,
            Child = AgentPill.Logo(agent.Agent, agent.Agent == "claude" ? ClaudeBrush : PillLook.Text, 16),
        };
        DockPanel.SetDock(logo, Dock.Left);
        root.Children.Add(logo);

        var body = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var line = new DockPanel();

        // Status chip on the right: a dot and the word, on a faint fill of its colour.
        var chip = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var dot = new Ellipse { Width = 7, Height = 7, Fill = brush, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 1, 7, 0) };
        if (agent.Status == AgentStatus.Waiting) dot.BeginAnimation(OpacityProperty, AgentPill.Pulse(0.8));
        chip.Children.Add(dot);
        chip.Children.Add(HomeLook.Text(text, 12, agent.Status == AgentStatus.Inactive ? PillLook.Subtle : brush));
        var chipBox = new Border
        {
            Child = chip, CornerRadius = new CornerRadius(11), Padding = new Thickness(9, 3, 10, 4),
            Background = Tint(brush), VerticalAlignment = VerticalAlignment.Center,
        };
        DockPanel.SetDock(chipBox, Dock.Right);
        line.Children.Add(chipBox);
        line.Children.Add(HomeLook.Text(AgentPill.AgentName(agent.Agent), 14, bold: true));
        body.Children.Add(line);

        // What it does now: only while it works.
        var note = HomeLook.Text("", 12, PillLook.Subtle);
        note.TextWrapping = TextWrapping.Wrap;
        note.MaxHeight = 34;
        note.Margin = new Thickness(0, 4, 0, 0);
        if (agent.Status == AgentStatus.Working && agent.Doing.Length > 0) note.Text = agent.Doing;
        note.Visibility = note.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        body.Children.Add(note);
        root.Children.Add(body);

        card.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            if (AgentWindows.Focus(agent.Agent)) return;
            note.Text = "Окно не найдено: агент не запущен";
            note.Visibility = Visibility.Visible;
        };
        return card;
    }

    private static Brush Tint(Brush brush)
    {
        var c = brush is SolidColorBrush s ? s.Color : Colors.Gray;
        return Frozen(new SolidColorBrush(Color.FromArgb(0x22, c.R, c.G, c.B)));
    }

    private static T Frozen<T>(T f) where T : Freezable
    {
        f.Freeze();
        return f;
    }
}
