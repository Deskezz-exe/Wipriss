using System.IO;
using System.Text.Json;

namespace Wipriss.Modules.Agents;

public enum StepState { Pending, Active, Done }

public enum Phase { Idle, Working, Waiting, Done }

public sealed record Step(string Text, StepState State);

/// <summary>
/// What one Claude Code session is doing, read from its transcript (one JSON object per line).
/// Knows the plan (TodoWrite, TaskCreate/TaskUpdate, the desktop app's task list), the last action and
/// whether the turn is still running. Pure: no files, no clock, so it is easy to test.
/// </summary>
public sealed class Transcript
{
    private readonly List<Step> _steps = [];

    public string Project { get; private set; } = "";
    public Phase Phase { get; private set; } = Phase.Idle;

    /// <summary>The last thing it did, in a few words ("Читает IslandWindow.cs").</summary>
    public string Doing { get; private set; } = "";

    public DateTime LastEvent { get; private set; }
    public IReadOnlyList<Step> Steps => _steps;

    /// <summary>The step in progress, or what it is doing now.</summary>
    public string Headline =>
        _steps.FirstOrDefault(s => s.State == StepState.Active)?.Text is { Length: > 0 } step ? step
        : Doing.Length > 0 ? Doing
        : "Работает";

    public void Feed(string line)
    {
        if (line.Length < 20) return;
        // Cheap checks first: most lines are tool results, often large, and only a few facts are needed.
        bool assistant = line.Contains("\"type\":\"assistant\"", StringComparison.Ordinal);
        bool user = !assistant && line.Contains("\"type\":\"user\"", StringComparison.Ordinal);
        if (!assistant && !user) return;
        if (line.Contains("\"isSidechain\":true", StringComparison.Ordinal)) return; // helper agents

        // Big user lines are tool results (file contents, command output): only "still working" matters.
        if (user && line.Length > 8192 && line.Contains("\"tool_result\"", StringComparison.Ordinal))
        {
            if (Phase != Phase.Done) Phase = Phase.Working;
            return;
        }

        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.TryGetProperty("cwd", out var cwd) && cwd.GetString() is { Length: > 0 } dir)
                Project = Path.GetFileName(dir.TrimEnd('\\', '/'));
            if (root.TryGetProperty("timestamp", out var ts) && ts.TryGetDateTime(out var time))
                LastEvent = time.ToLocalTime();
            if (!root.TryGetProperty("message", out var message)) return;

            if (assistant) OnAssistant(message);
            else OnUser(root, message);
        }
        catch (JsonException)
        {
            // A line cut in the middle (still being written) or a format we do not know: skip it.
        }
    }

    private void OnUser(JsonElement root, JsonElement message)
    {
        if (root.TryGetProperty("isMeta", out var meta) && meta.ValueKind == JsonValueKind.True) return;
        if (!message.TryGetProperty("content", out var content)) return;

        if (content.ValueKind == JsonValueKind.String)
        {
            NewPrompt(content.GetString() ?? "");
            return;
        }
        if (content.ValueKind != JsonValueKind.Array) return;

        bool toolResult = false;
        foreach (var item in content.EnumerateArray())
        {
            string type = Str(item, "type");
            if (type == "tool_result") toolResult = true;
            else if (type == "text") { NewPrompt(Str(item, "text")); return; }
        }
        if (toolResult && Phase != Phase.Done) Phase = Phase.Working;
    }

    private void NewPrompt(string text)
    {
        if (text.StartsWith("[Request interrupted", StringComparison.Ordinal))
        {
            Phase = Phase.Idle;
            return;
        }
        if (text.StartsWith('<')) return; // command output, reminders and other non-prompts
        // A new request: a finished plan belongs to the previous one.
        if (_steps.Count > 0 && _steps.All(s => s.State == StepState.Done)) _steps.Clear();
        Doing = "";
        Phase = Phase.Working;
    }

    private void OnAssistant(JsonElement message)
    {
        bool usedTool = false;
        if (message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in content.EnumerateArray())
            {
                if (Str(item, "type") != "tool_use") continue;
                usedTool = true;
                var input = item.TryGetProperty("input", out var i) ? i : default;
                OnTool(Str(item, "name"), input);
            }
        }

        string stop = Str(message, "stop_reason");
        if (stop == "end_turn" && !usedTool)
        {
            if (Phase != Phase.Waiting) Phase = Phase.Done;
        }
        else if (usedTool && Phase != Phase.Waiting && Phase != Phase.Done)
        {
            Phase = Phase.Working;
        }
    }

    private void OnTool(string name, JsonElement input)
    {
        switch (name)
        {
            case "TodoWrite":
                if (input.ValueKind == JsonValueKind.Object && input.TryGetProperty("todos", out var todos) &&
                    todos.ValueKind == JsonValueKind.Array)
                {
                    _steps.Clear();
                    foreach (var t in todos.EnumerateArray())
                    {
                        var state = ParseState(Str(t, "status"));
                        string text = state == StepState.Active && Str(t, "activeForm") is { Length: > 0 } active ? active : Str(t, "content");
                        _steps.Add(new Step(text, state));
                    }
                }
                Phase = Phase.Working;
                return;

            case "TaskCreate":
                _steps.Add(new Step(Str(input, "subject"), StepState.Pending));
                Phase = Phase.Working;
                return;

            case "TaskUpdate":
                if (int.TryParse(Str(input, "taskId"), out int id) && id >= 1 && id <= _steps.Count &&
                    Str(input, "status") is { Length: > 0 } status)
                {
                    _steps[id - 1] = _steps[id - 1] with { State = ParseState(status) };
                }
                Phase = Phase.Working;
                return;

            case "mcp__ccd_turn__update_plan":
                OnPlan(input);
                Phase = Phase.Working;
                return;

            case "mcp__ccd_turn__wrap_up":
                for (int k = 0; k < _steps.Count; k++) _steps[k] = _steps[k] with { State = StepState.Done };
                Phase = input.ValueKind == JsonValueKind.Object && input.TryGetProperty("ask", out _) ? Phase.Waiting : Phase.Done;
                return;

            case "AskUserQuestion":
            case "ExitPlanMode":
                Phase = Phase.Waiting;
                return;
        }

        Doing = Describe(name, input);
        Phase = Phase.Working;
    }

    /// <summary>The desktop app's task list: a whole new list, or "advance", or done/start by number.</summary>
    private void OnPlan(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Object) return;
        if (input.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
        {
            _steps.Clear();
            foreach (var item in items.EnumerateArray())
                _steps.Add(new Step(Str(item, "content"), ParseState(Str(item, "status"))));
            return;
        }

        if (input.TryGetProperty("advance", out var adv) && adv.ValueKind == JsonValueKind.True)
        {
            int active = _steps.FindIndex(s => s.State == StepState.Active);
            if (active >= 0) _steps[active] = _steps[active] with { State = StepState.Done };
            int next = _steps.FindIndex(s => s.State == StepState.Pending);
            if (next >= 0) _steps[next] = _steps[next] with { State = StepState.Active };
        }
        if (input.TryGetProperty("done", out var done) && done.ValueKind == JsonValueKind.Array)
            foreach (var n in done.EnumerateArray())
                if (n.TryGetInt32(out int k) && k >= 1 && k <= _steps.Count)
                    _steps[k - 1] = _steps[k - 1] with { State = StepState.Done };
        if (input.TryGetProperty("start", out var start) && start.TryGetInt32(out int s0) && s0 >= 1 && s0 <= _steps.Count)
        {
            for (int k = 0; k < _steps.Count; k++)
                if (_steps[k].State == StepState.Active) _steps[k] = _steps[k] with { State = StepState.Pending };
            _steps[s0 - 1] = _steps[s0 - 1] with { State = StepState.Active };
        }
    }

    private static StepState ParseState(string status) => status switch
    {
        "completed" => StepState.Done,
        "in_progress" => StepState.Active,
        _ => StepState.Pending,
    };

    internal static string Describe(string tool, JsonElement input)
    {
        string file = Path.GetFileName(Str(input, "file_path"));
        string Short(string s) => s.Length > 60 ? s[..57] + "…" : s;
        return tool switch
        {
            "Bash" or "PowerShell" => Str(input, "description") is { Length: > 0 } d ? Short(d) : "Запускает команду",
            "Read" => file.Length > 0 ? "Читает " + file : "Читает файл",
            "Edit" or "Write" or "NotebookEdit" or "MultiEdit" => file.Length > 0 ? "Правит " + file : "Правит код",
            "Grep" or "Glob" => "Ищет в коде",
            "WebSearch" => "Ищет в интернете",
            "WebFetch" => "Читает страницу",
            "Agent" or "Task" => Str(input, "description") is { Length: > 0 } a ? "Помощник: " + Short(a) : "Работает помощник",
            "Skill" => "Скилл " + Str(input, "skill"),
            _ when tool.Contains("Browser", StringComparison.OrdinalIgnoreCase) || tool.Contains("chrome", StringComparison.OrdinalIgnoreCase)
                => "Работает в браузере",
            _ when tool.StartsWith("mcp__ccd", StringComparison.Ordinal) => "Работает",
            _ => "Работает",
        };
    }

    private static string Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v)
            ? v.ValueKind switch
            {
                JsonValueKind.String => v.GetString() ?? "",
                JsonValueKind.Number => v.GetRawText(),
                _ => "",
            }
            : "";
}
