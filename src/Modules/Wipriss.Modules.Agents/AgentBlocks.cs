using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Wipriss.Core.Ui;

namespace Wipriss.Modules.Agents;

/// <summary>
/// The agents on the home screen: one block per agent (two side by side when both are there): its logo, name,
/// what it does now ("работает", "ждёт ответа", "простаивает", "неактивен") and the project. A click brings the
/// agent's window to the front.
/// </summary>
internal sealed class AgentBlocks : Grid
{
    private static readonly Brush ClaudeBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xD9, 0x77, 0x57)));

    public AgentBlocks(AgentsModule module)
    {
        var agents = module.HomeAgents(DateTime.Now);
        for (int i = 0; i < agents.Count; i++)
        {
            if (i > 0) ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
            ColumnDefinitions.Add(new ColumnDefinition());
            var block = Block(agents[i], wide: agents.Count == 1);
            SetColumn(block, i * 2);
            Children.Add(block);
        }
    }

    private static Border Block(AgentView agent, bool wide)
    {
        var (text, brush) = agent.Status switch
        {
            AgentStatus.Working => ("работает", PillLook.Blue),
            AgentStatus.Waiting => ("ждёт ответа", PillLook.Amber),
            AgentStatus.Idle => ("простаивает", PillLook.Green),
            _ => ("неактивен", PillLook.Faint),
        };

        var card = HomeLook.Card(padding: new Thickness(14, 12, 14, 12));
        card.Cursor = Cursors.Hand;
        card.ToolTip = "Открыть окно агента";
        var normal = card.Background;
        card.MouseEnter += (_, _) => card.Background = HomeLook.Hover;
        card.MouseLeave += (_, _) => card.Background = normal;
        var note = HomeLook.Text("", 11, PillLook.Faint);
        card.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            if (!AgentWindows.Focus(agent.Agent)) note.Text = "Окно не найдено: агент не запущен";
        };

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        card.Child = grid;

        // Top: the logo in a round socket, the name and the status; an "open" arrow in the corner.
        var top = new DockPanel();
        var open = HomeLook.Glyph("", 12, PillLook.Faint);
        open.VerticalAlignment = VerticalAlignment.Top;
        DockPanel.SetDock(open, Dock.Right);
        top.Children.Add(open);
        var logo = new Border
        {
            Width = 38, Height = 38, CornerRadius = new CornerRadius(19), Background = HomeLook.Raised, Margin = new Thickness(0, 0, 11, 0),
            Child = AgentPill.Logo(agent.Agent, agent.Agent == "claude" ? ClaudeBrush : PillLook.Text, 17),
        };
        DockPanel.SetDock(logo, Dock.Left);
        top.Children.Add(logo);
        var names = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        names.Children.Add(HomeLook.Text(AgentPill.AgentName(agent.Agent), 14, bold: true));
        var status = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 0) };
        var dot = new Ellipse { Width = 7, Height = 7, Fill = brush, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 1, 7, 0) };
        if (agent.Status == AgentStatus.Waiting) dot.BeginAnimation(OpacityProperty, AgentPill.Pulse(0.8));
        status.Children.Add(dot);
        status.Children.Add(HomeLook.Text(text, 12.5, brush == PillLook.Faint ? PillLook.Subtle : brush));
        names.Children.Add(status);
        top.Children.Add(names);
        grid.Children.Add(top);

        // Below: the project and what it does now (two lines at most); a note when its window is not found.
        var info = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 8, 0, 0) };
        Grid.SetRow(info, 1);
        if (agent.Project.Length > 0) info.Children.Add(HomeLook.Text(agent.Project, 12, PillLook.Subtle, bold: true));
        if (wide && agent.Doing.Length > 0)
        {
            var doing = HomeLook.Text(agent.Doing, 11.5, PillLook.Subtle);
            doing.Margin = new Thickness(0, 2, 0, 0);
            info.Children.Add(doing);
        }
        info.Children.Add(note);
        grid.Children.Add(info);
        return card;
    }

    private static T Frozen<T>(T f) where T : Freezable
    {
        f.Freeze();
        return f;
    }
}
