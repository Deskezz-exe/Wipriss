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

        // Wipriss closing our stdin (or exiting) means: stop. Later lines are the next steps of an uninstall.
        using var cancel = new CancellationTokenSource();
        var watcher = new Thread(() =>
        {
            try
            {
                while (stdin.ReadLine() is { } line)
                {
                    CleanRequest? next;
                    try { next = JsonSerializer.Deserialize(line, CleanJson.Default.CleanRequest); }
                    catch (JsonException) { continue; }
                    if (next is null) continue;
                    if (next.Command == CleanCommand.Continue) _continue = true;
                    else Inbox.Add(next);
                }
            }
            catch (IOException) { }
            cancel.Cancel();
        }) { IsBackground = true };
        watcher.Start();

        try
        {
            var hidden = request.Hidden.Select(PathText.Normalize).OfType<string>().ToList();
            if (request.Command == CleanCommand.Delete) return Delete(request, hidden, cancel.Token);
            if (request.Command == CleanCommand.Programs) return ListPrograms(cancel.Token);
            if (request.Command == CleanCommand.Uninstall) return RemoveProgram(request, cancel.Token);
            if (request.Command == CleanCommand.Restore) return Restore(request);
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

    private static readonly System.Collections.Concurrent.BlockingCollection<CleanRequest> Inbox = new();
    private static volatile bool _continue;

    /// <summary>The next step from Wipriss, one of <paramref name="wanted"/>; null when Wipriss is gone or said Quit.</summary>
    private static CleanRequest? Next(CancellationToken cancel, params CleanCommand[] wanted)
    {
        try
        {
            while (true)
            {
                var next = Inbox.Take(cancel);
                if (next.Command == CleanCommand.Quit) return null;
                if (wanted.Contains(next.Command)) return next;
            }
        }
        catch (OperationCanceledException) { return null; }
    }

    private static int ListPrograms(CancellationToken cancel)
    {
        Progress("Смотрю, что установлено", 5);
        var list = ProgramList.Read(p => Progress("Считаю размеры", 10 + p * 90 / 100), cancel);
        foreach (var program in list) Send(new CleanReply { Kind = CleanReplyKind.Program, Program = program.Entry });
        Send(new CleanReply { Kind = CleanReplyKind.Done });
        return 0;
    }

    /// <summary>
    /// Snapshot → the program's own uninstaller (or, if it has none or it failed, forced removal on request)
    /// → the tails → (on request) deleting the chosen tails, found again first, with a registry copy.
    /// </summary>
    private static int RemoveProgram(CleanRequest request, CancellationToken cancel)
    {
        if (request.ProgramId is not { } id || ProgramList.Find(id) is not { } program)
        {
            Send(new CleanReply { Kind = CleanReplyKind.Error, Text = "Программы уже нет в списке установленных" });
            return 2;
        }
        var snapshot = ProgramSnapshot.Take(program);
        bool forced = request.Forced && !program.Entry.Store;

        if (!forced)
        {
            bool started = true, refused = false;
            Send(new CleanReply { Kind = CleanReplyKind.Stage, Stage = UninstallStage.Running });
            if (program.PackageFullName is { } package) Uninstall.RemovePackage(package, cancel);
            else if (program.UninstallCommand is { } command && Uninstall.Start(command, out var process, out refused))
            {
                using (process) Uninstall.WaitForTree(process!, () => _continue, cancel);
            }
            else started = false;

            if (ProgramList.IsStillInstalled(program))
            {
                Send(new CleanReply
                {
                    Kind = CleanReplyKind.Stage,
                    Stage = program.UninstallCommand is null && !program.Entry.Store ? UninstallStage.NoUninstaller : UninstallStage.NotRemoved,
                    Text = refused ? "Вы отказались в окне Windows «Разрешить вносить изменения»."
                        : !started ? "Деинсталлятор программы не запустился: его файла нет или он повреждён."
                        : null,
                });
                if (program.Entry.Store || Next(cancel, CleanCommand.Force) is null) return 0;
                forced = true;
            }
        }
        Send(new CleanReply { Kind = CleanReplyKind.Stage, Stage = UninstallStage.Removed });

        var tails = Tails.Find(snapshot, forced, Progress, cancel);
        Send(new CleanReply { Kind = CleanReplyKind.Tails, Tails = tails });
        if (Next(cancel, CleanCommand.DeleteTails) is not { } chosen) return 0;

        // Found again: only what this search finds under the chosen ids is deleted.
        Progress("Проверяю выбранное ещё раз", 5);
        var fresh = Tails.Find(snapshot, forced, (_, p) => Progress("Проверяю выбранное ещё раз", 5 + p / 4), cancel);
        var wanted = chosen.Targets.Select(t => t.Title).ToHashSet(StringComparer.Ordinal);
        var delete = new List<TailItem>();
        foreach (var tail in fresh)
        {
            if (wanted.Contains(tail.Id)) { delete.Add(tail); continue; }
            delete.AddRange(tail.Children.Where(c => wanted.Contains(c.Id)));
        }
        var job = Purge.ToJob(delete);

        Progress("Сохраняю копию записей реестра", 35);
        string? backup = null;
        try { if (request.BackupDir is { } dir) backup = RegFile.Save(dir, program.Entry.Name, job); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        if (backup is null && job.Keys.Count + job.Values.Count + job.Services.Count + job.Tasks.Count > 0)
        {
            // No copy, no registry deletion: only files go.
            job.Keys.Clear(); job.Values.Clear(); job.Services.Clear(); job.Tasks.Clear();
        }

        Progress("Удаляю", 45);
        var forAdmin = new PurgeJob();
        var (userFreed, failed) = Purge.Run(job, elevated: false, forAdmin, cancel);
        if (forAdmin.Count > 0)
        {
            Progress("Удаляю то, что требует прав администратора", 75);
            failed += Purge.RunElevated(forAdmin);
        }
        // Freed: what is gone now, by its measured size; of what stayed, what the owner's own pass freed.
        long freed = 0;
        bool anyLeft = false;
        foreach (var tail in delete.Where(t => t.Kind is TailKind.Folder or TailKind.File))
        {
            if (!File.Exists(tail.Id[2..]) && !Directory.Exists(tail.Id[2..])) freed += tail.Size;
            else anyLeft = true;
        }
        if (anyLeft && forAdmin.Paths.Count == 0) freed = Math.Max(freed, userFreed);
        Send(new CleanReply { Kind = CleanReplyKind.TailsDeleted, Bytes = freed, Count = failed, BackupDir = backup });
        return 0;
    }

    private static int Restore(CleanRequest request)
    {
        var folder = PathText.Normalize(request.BackupDir);
        if (folder is null || !Directory.Exists(folder))
        {
            Send(new CleanReply { Kind = CleanReplyKind.Error, Text = "Копии нет" });
            return 2;
        }
        Progress("Возвращаю записи реестра", 30);
        Send(new CleanReply { Kind = CleanReplyKind.Restored, Count = RegFile.Restore(folder) });
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
