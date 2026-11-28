using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Wipriss.Modules.Agents;

/// <summary>
/// The Agents page in Settings: a button per agent that adds (or removes) Wipriss's hook in that agent's own
/// settings. Nothing is changed until the user presses it.
/// </summary>
internal sealed class AgentsSettings : StackPanel
{
    public AgentsSettings()
    {
        Build();
    }

    private void Build()
    {
        Children.Clear();
        Children.Add(Caption("Без подключения островок видит только, что Claude Code работает или закончил. " +
                             "Чтобы он сразу показывал «ждёт подтверждения», Wipriss добавит в настройки агента маленькую команду " +
                             "Wipriss.AgentHook.exe. Она только передаёт островку событие и сразу закрывается. " +
                             "Прежний файл настроек сохраняется рядом с припиской .wipriss-backup.", 0));

        var card = new Border { Style = Res<Style>("ListCard"), Margin = new Thickness(0, 12, 0, 0) };
        var rows = new StackPanel();
        card.Child = rows;
        rows.Children.Add(ClaudeRow());
        rows.Children.Add(new Border { Height = 1, Background = Res<Brush>("DividerBrush"), Margin = new Thickness(0, 12, 0, 12) });
        rows.Children.Add(CodexRow());
        Children.Add(card);
    }

    private FrameworkElement ClaudeRow()
    {
        string file = HookSetup.ClaudeSettingsFile;
        if (!HookSetup.ClaudeInstalled) return Row("Claude Code", "Не найден на этом компьютере.", null, null);
        string? text = Read(file, out string? problem);
        if (problem is not null) return Row("Claude Code", problem, null, null);
        bool on = HookSetup.HasClaudeHook(text);
        return Row("Claude Code", on ? "Подключён: островок покажет, когда Claude ждёт подтверждения." : "Не подключён.",
            on ? "Отключить" : "Подключить",
            () => Apply(file, old => on ? HookSetup.RemoveClaudeHook(old ?? "{}") : HookSetup.AddClaudeHook(old, HookSetup.HookPath)));
    }

    private FrameworkElement CodexRow()
    {
        string file = HookSetup.CodexConfigFile;
        if (!HookSetup.CodexInstalled) return Row("Codex", "Не найден на этом компьютере.", null, null);
        string? text = Read(file, out string? problem);
        if (problem is not null) return Row("Codex", problem, null, null);
        bool on = HookSetup.HasCodexNotify(text);
        return Row("Codex", on ? "Подключён: островок покажет, когда Codex закончил." : "Не подключён.",
            on ? "Отключить" : "Подключить",
            () => Apply(file, old => on ? HookSetup.RemoveCodexNotify(old ?? "") : HookSetup.AddCodexNotify(old, HookSetup.HookPath)));
    }

    private void Apply(string file, Func<string?, string> change)
    {
        string? error = null;
        if (!File.Exists(HookSetup.HookPath)) error = "Рядом с Wipriss нет Wipriss.AgentHook.exe. Переустановите Wipriss.";
        else
        {
            try { HookSetup.Change(file, change); }
            catch (FormatException) { error = "Файл настроек агента повреждён, Wipriss не стал его менять."; }
            catch (InvalidOperationException) { error = "В Codex уже настроена своя команда notify. Wipriss не стал её заменять."; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { error = "Не удалось записать файл настроек: " + ex.Message; }
        }
        Build();
        if (error is not null)
        {
            var warn = Caption(error, 10);
            warn.Foreground = Res<Brush>("WarnBrush");
            Children.Add(warn);
        }
    }

    private static string? Read(string file, out string? problem)
    {
        problem = null;
        try { return File.Exists(file) ? File.ReadAllText(file) : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            problem = "Не удалось прочитать файл настроек.";
            return null;
        }
    }

    private FrameworkElement Row(string title, string note, string? button, Action? click)
    {
        var row = new DockPanel();
        if (button is not null && click is not null)
        {
            var b = new Button { Content = button, Style = Res<Style>("PlainButton"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
            b.Click += (_, _) => click();
            DockPanel.SetDock(b, Dock.Right);
            row.Children.Add(b);
        }
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
