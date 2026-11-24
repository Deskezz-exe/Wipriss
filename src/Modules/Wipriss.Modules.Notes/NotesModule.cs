using System.IO;
using System.Text.Json.Serialization;
using System.Windows;
using Wipriss.Core;
using Wipriss.Core.Modules;
using Wipriss.Core.Storage;
using Wipriss.Core.Text;

namespace Wipriss.Modules.Notes;

public sealed class Note
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Text { get; set; } = "";
    public DateTime Updated { get; set; } = DateTime.Now;
}

public sealed class NotesFile
{
    public List<Note> Notes { get; set; } = [];
}

[JsonSerializable(typeof(NotesFile))]
internal sealed partial class NotesJson : JsonSerializerContext;

/// <summary>Quick notes. Nothing runs in the background: the file is read when the island or panel opens.</summary>
public sealed class NotesModule : IModule
{
    private static readonly string FilePath = Path.Combine(AppPaths.DataDir, "notes.dat");
    private NotesFile? _data;

    public ModuleInfo Info { get; } = ModuleCatalog.Get("notes");

    public Size PanelSize => new(460, 560);

    internal NotesFile Data => _data ??= SecureStore.ReadJson(FilePath, NotesJson.Default.NotesFile) ?? new NotesFile();

    public string Summary
    {
        get
        {
            int count = Data.Notes.Count(n => !string.IsNullOrWhiteSpace(n.Text));
            return count == 0 ? "пусто" : RuText.Count(count, "заметка", "заметки", "заметок");
        }
    }

    public void Start(IModuleHost host) { }

    public void Stop() => _data = null;

    public FrameworkElement CreatePanel(IPanelContext panel) => new NotesPanel(this, panel);

    internal void Save() => SecureStore.WriteJson(FilePath, Data, NotesJson.Default.NotesFile);
}
