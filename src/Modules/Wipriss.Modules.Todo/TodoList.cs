using System.Text.Json.Serialization;

namespace Wipriss.Modules.Todo;

public sealed class TodoItem
{
    public string Text { get; set; } = "";
    public bool Done { get; set; }
}

public sealed class TodoData
{
    public List<TodoItem> Items { get; set; } = [];
}

[JsonSerializable(typeof(TodoData))]
internal sealed partial class TodoJson : JsonSerializerContext;

/// <summary>The tasks, in the order they were added. No UI, no files: <see cref="TodoModule"/> saves it.</summary>
internal sealed class TodoList(TodoData data)
{
    public const int MaxLength = 300;
    public const int MaxItems = 100;

    public IReadOnlyList<TodoItem> Items => data.Items;

    public TodoData Data => data;

    /// <summary>Adds a task (trimmed, one line, not too long). False for empty text or a full list.</summary>
    public bool Add(string text)
    {
        text = string.Join(' ', (text ?? "").Split(['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries)).Trim();
        if (text.Length == 0 || data.Items.Count >= MaxItems) return false;
        if (text.Length > MaxLength) text = text[..MaxLength];
        data.Items.Add(new TodoItem { Text = text });
        return true;
    }

    public void Toggle(int index)
    {
        if (index >= 0 && index < data.Items.Count) data.Items[index].Done = !data.Items[index].Done;
    }

    public void Remove(int index)
    {
        if (index >= 0 && index < data.Items.Count) data.Items.RemoveAt(index);
    }

    public int DoneCount => data.Items.Count(i => i.Done);
}
