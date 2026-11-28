using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Wipriss.Modules.Agents;

/// <summary>
/// The Agents page in Settings: only where each agent stands. The hook goes in by itself when the module starts
/// and leaves when the module is switched off, so there is nothing to press.
/// </summary>
internal sealed class AgentsSettings : StackPanel
{
    public AgentsSettings()
    {
        Children.Add(Caption("Wipriss сам подключается к найденным агентам, чтобы островок сразу показывал «ждёт подтверждения». " +
                             "Для этого в настройки агента добавляется маленькая команда Wipriss.AgentHook.exe: она только передаёт " +
                             "островку событие и сразу закрывается. Прежний файл настроек сохраняется рядом с припиской .wipriss-backup. " +
                             "Если выключить модуль «Агенты», команда убирается.", 0));

        var (claude, codex) = HookSetup.Inspect();
        var card = new Border { Style = Res<Style>("ListCard"), Margin = new Thickness(0, 12, 0, 0) };
        var rows = new StackPanel();
        card.Child = rows;
        rows.Children.Add(Row("Claude Code", claude));
        rows.Children.Add(new Border { Height = 1, Background = Res<Brush>("DividerBrush"), Margin = new Thickness(0, 12, 0, 12) });
        rows.Children.Add(Row("Codex", codex));
        Children.Add(card);
    }

    private FrameworkElement Row(string title, HookState state)
    {
        var (note, brush) = state switch
        {
            HookState.Connected => ("Подключён", "OkBrush"),
            HookState.NotFound => ("Не найден на этом компьютере", "SubtleTextBrush"),
            HookState.Off or HookState.OldPath => ("Подключится при следующем запуске Wipriss", "SubtleTextBrush"),
            HookState.Broken => ("Файл настроек агента повреждён, Wipriss его не трогает", "WarnBrush"),
            HookState.Busy => ("В Codex уже настроена своя команда notify, Wipriss её не заменяет", "WarnBrush"),
            HookState.NoHookFile => ("Рядом с Wipriss нет Wipriss.AgentHook.exe, переустановите Wipriss", "WarnBrush"),
            _ => ("Не удалось прочитать файл настроек агента", "WarnBrush"),
        };
        var row = new DockPanel();
        var dot = new Border
        {
            Width = 8, Height = 8, CornerRadius = new CornerRadius(4), Background = Res<Brush>(brush),
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0),
        };
        DockPanel.SetDock(dot, Dock.Left);
        row.Children.Add(dot);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = title, Style = Res<Style>("RowTitle") });
        text.Children.Add(Caption(note, 3));
        row.Children.Add(text);
        return row;
    }

    private TextBlock Caption(string text, double top) => new()
    {
        Text = text, Style = Res<Style>("Caption"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, top, 0, 0),
    };

    private T Res<T>(string key) => (T)FindResource(key);
}
