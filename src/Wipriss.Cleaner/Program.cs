using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace Wipriss.Cleaner;

/// <summary>
/// Wipriss.Cleaner.exe: reads one <see cref="CleanRequest"/> line from stdin, answers with
/// <see cref="CleanReply"/> lines and exits. No window, no network, no administrator rights. «Scan» only reads;
/// «Delete» searches again and deletes only what the fresh search names (see <see cref="Deleter"/>).
/// Closing stdin stops the work.
/// </summary>
internal static class Program
{
    private static readonly Lock OutGate = new();
    private static StreamWriter? _stdout;

    private static int Main()
    {
        _stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
        try { Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.BelowNormal; } catch (Exception) { }

        var stdin = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
        CleanRequest? request;
        try { request = JsonSerializer.Deserialize(stdin.ReadLine() ?? "", CleanJson.Default.CleanRequest); }
        catch (JsonException) { request = null; }
        if (request is null) { Send(new CleanReply { Kind = CleanReplyKind.Error, Text = "bad request" }); return 2; }

        // Wipriss closing our stdin (or exiting) means: stop.
        using var cancel = new CancellationTokenSource();
        var watcher = new Thread(() =>
        {
            try { while (stdin.ReadLine() is not null) { } } catch (IOException) { }
            cancel.Cancel();
        }) { IsBackground = true };
        watcher.Start();

        try
        {
            var hidden = request.Hidden.Select(PathText.Normalize).OfType<string>().ToList();
            if (request.Command == CleanCommand.Delete) return Delete(request, hidden, cancel.Token);
            foreach (var group in Scanner.Run(Progress, cancel.Token))
            {
                if (cancel.IsCancellationRequested) return 1;
                var visible = Scanner.WithoutHidden(group, hidden);
                if (visible is not null) Send(new CleanReply { Kind = CleanReplyKind.Group, Group = visible });
            }
            Send(new CleanReply { Kind = CleanReplyKind.Done });
            return 0;
        }
        catch (OperationCanceledException) { return 1; }
        catch (Exception ex)
        {
            Send(new CleanReply { Kind = CleanReplyKind.Error, Text = ex.GetType().Name });
            return 3;
        }
    }

    /// <summary>Search again and delete what the fresh search finds under the requested names.</summary>
    private static int Delete(CleanRequest request, IReadOnlyList<string> hidden, CancellationToken cancel)
    {
        var wanted = request.Targets.Select(t => (t.GroupId, t.Title)).ToHashSet();
        var items = new List<CleanItem>();
        foreach (var group in Scanner.Run((_, percent) => Progress("Проверяю выбранное ещё раз", percent * 40 / 100), cancel))
        {
            if (Scanner.WithoutHidden(group, hidden) is not { } visible) continue;
            items.AddRange(visible.Items.Where(i => wanted.Contains((visible.Id, i.Title)) && Deleter.CanDelete(i)));
        }
        var (freed, failed) = Deleter.Delete(items, percent => Progress("Удаляю", 40 + percent * 60 / 100), cancel);
        Send(new CleanReply { Kind = CleanReplyKind.Deleted, Bytes = freed, Count = failed });
        return 0;
    }

    private static void Progress(string text, int percent) =>
        Send(new CleanReply { Kind = CleanReplyKind.Progress, Text = text, Percent = percent });

    private static void Send(CleanReply reply)
    {
        lock (OutGate)
        {
            try { _stdout!.WriteLine(JsonSerializer.Serialize(reply, CleanJson.Default.CleanReply)); }
            catch (IOException) { } // Wipriss is gone
        }
    }
}

internal static class Scanner
{
    public static IEnumerable<CleanGroup> Run(Action<string, int> progress, CancellationToken cancel)
    {
        progress("Смотрю, что установлено", 2);
        var context = new ScanContext(Inventory.Take());
        cancel.ThrowIfCancellationRequested();

        progress("Windows и видеокарта", 12);
        if (SystemPlaces.Windows(context) is { } windows) yield return windows;
        foreach (var group in SystemPlaces.Graphics(context)) yield return group;
        cancel.ThrowIfCancellationRequested();

        progress("Браузеры", 25);
        foreach (var group in Browsers.Find(context)) yield return group;
        cancel.ThrowIfCancellationRequested();

        progress("Программы", 38);
        foreach (var group in Apps.Known(context)) yield return group;
        cancel.ThrowIfCancellationRequested();

        progress("Кэши разработчика", 46);
        foreach (var group in Developer.Caches(context)) yield return group;
        cancel.ThrowIfCancellationRequested();

        progress("Модели ИИ", 50);
        foreach (var group in Models.Find(context)) yield return group;

        progress("Установщики в «Загрузках»", 52);
        if (Installers.Find(context) is { } installers) yield return installers;

        if (context.Inventory.Complete)
        {
            progress("Остатки удалённых программ", 56);
            foreach (var group in Leftovers.Find(context, p => progress("Остатки удалённых программ", 56 + p * 30 / 100), cancel))
                yield return group;
        }
        cancel.ThrowIfCancellationRequested();

        progress("Ваши проекты (node_modules, venv)", 88);
        foreach (var group in Developer.Projects(context, cancel)) yield return group;
        progress("Готово", 100);
    }

    /// <summary>The group without the paths the owner hid; null when nothing is left.</summary>
    public static CleanGroup? WithoutHidden(CleanGroup group, IReadOnlyList<string> hidden)
    {
        if (hidden.Count == 0) return group;
        group.Items = group.Items
            .Where(i => !i.Paths.Any(p => hidden.Any(h => PathText.IsInside(p, h))))
            .ToList();
        return group.Items.Count == 0 ? null : group;
    }
}
