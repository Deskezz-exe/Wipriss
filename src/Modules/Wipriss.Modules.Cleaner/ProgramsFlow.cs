using System.IO;
using System.Windows.Threading;
using Wipriss.Cleaner;
using Wipriss.Core;

namespace Wipriss.Modules.Cleaner;

/// <summary>Where removing a program is now (what the window shows).</summary>
internal enum FlowState { Idle, Running, NotRemoved, Searching, Tails, Deleting, Done, Failed, Restoring }

/// <summary>A registry copy made before tails were deleted.</summary>
internal sealed record BackupInfo(string Folder, string Program, DateTime When);

/// <summary>
/// The «Установленные программы» tab: the list (read by Wipriss.Cleaner.exe when the tab opens) and one
/// removal at a time: the program's own uninstaller, then its tails, chosen by the owner, deleted with a
/// registry copy. All the work is in the scanner process; this only follows it.
/// </summary>
internal sealed class ProgramsFlow(Func<Dispatcher?> dispatcher)
{
    private CleanerSession? _list;
    private CleanerSession? _work;
    private bool _quitting;

    public static string BackupRoot => Path.Combine(AppPaths.DataDir, "Копии реестра");

    public event Action? Changed;

    public List<AppEntry>? Programs { get; private set; }
    public bool Loading => _list is not null;
    public string ProgressText { get; private set; } = "";
    public int ProgressPercent { get; private set; }
    public string? ListError { get; private set; }

    public FlowState State { get; private set; }
    public AppEntry? Target { get; private set; }
    public bool Forced { get; private set; }
    /// <summary>Running: none. NotRemoved: why (refused UAC, no uninstaller file), or null.</summary>
    public string? StageText { get; private set; }
    public bool NoUninstaller { get; private set; }
    public List<TailItem>? Tails { get; private set; }
    public long Freed { get; private set; }
    public int NotDeleted { get; private set; }
    public string? Backup { get; private set; }
    public string? Error { get; private set; }
    /// <summary>After a restore: what to say.</summary>
    public string? RestoreText { get; private set; }

    public void Load()
    {
        if (_list is not null || dispatcher() is not { } ui) return;
        var found = new List<AppEntry>();
        ListError = null;
        try { _list = CleanerSession.Start(ui, new CleanRequest { Command = CleanCommand.Programs }); }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception)
        {
            ListError = "Не удалось запустить: нет файла Wipriss.Cleaner.exe рядом с Wipriss.";
            Changed?.Invoke();
            return;
        }
        _list.ProgramFound += found.Add;
        _list.Progress += (text, percent) => { if (State is FlowState.Idle or FlowState.Done) { ProgressText = text; ProgressPercent = percent; } Changed?.Invoke(); };
        _list.Finished += error =>
        {
            _list = null;
            if (error is null) Programs = found;
            else ListError = "Не удалось прочитать список программ";
            Changed?.Invoke();
        };
        Changed?.Invoke();
    }

    public void Uninstall(AppEntry app, bool forced)
    {
        if (_work is not null || dispatcher() is not { } ui) return;
        Target = app;
        Forced = forced;
        Tails = null;
        Error = null;
        StageText = null;
        RestoreText = null;
        NoUninstaller = false;
        _quitting = false;
        var request = new CleanRequest { Command = CleanCommand.Uninstall, ProgramId = app.Id, Forced = forced, BackupDir = BackupRoot };
        try { _work = CleanerSession.Start(ui, request); }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception)
        {
            Fail("Не удалось запустить: нет файла Wipriss.Cleaner.exe рядом с Wipriss.");
            return;
        }
        State = forced ? FlowState.Searching : FlowState.Running;
        ProgressText = forced ? "Ищу, что осталось" : "";
        ProgressPercent = 0;
        _work.Progress += (text, percent) => { ProgressText = text; ProgressPercent = percent; Changed?.Invoke(); };
        _work.StageChanged += (stage, text) =>
        {
            switch (stage)
            {
                case UninstallStage.Running: State = FlowState.Running; break;
                case UninstallStage.NotRemoved or UninstallStage.NoUninstaller:
                    State = FlowState.NotRemoved;
                    StageText = text;
                    NoUninstaller = stage == UninstallStage.NoUninstaller;
                    break;
                case UninstallStage.Removed:
                    State = FlowState.Searching;
                    ProgressText = "Ищу, что осталось";
                    ProgressPercent = 0;
                    break;
            }
            Changed?.Invoke();
        };
        _work.TailsFound += tails => { Tails = tails; State = FlowState.Tails; Changed?.Invoke(); };
        _work.TailsDeleted += (freed, failed, backup) =>
        {
            Freed = freed;
            NotDeleted = failed;
            Backup = backup;
            State = FlowState.Done;
            Changed?.Invoke();
        };
        _work.Finished += error =>
        {
            _work = null;
            if (_quitting || State == FlowState.Done)
            {
                if (_quitting && State != FlowState.Done) State = FlowState.Idle;
                Programs = null;
                Load(); // the list without the removed program
            }
            else Fail(error ?? "Удаление прервалось");
            Changed?.Invoke();
        };
        Changed?.Invoke();
    }

    private void Fail(string error)
    {
        State = FlowState.Failed;
        Error = error;
        Changed?.Invoke();
    }

    /// <summary>The uninstaller window closed, but Wipriss still waits: go on.</summary>
    public void ContinueWaiting() => _work?.Send(new CleanRequest { Command = CleanCommand.Continue });

    public void Force()
    {
        if (_work is null || State != FlowState.NotRemoved) return;
        Forced = true;
        State = FlowState.Searching;
        ProgressText = "Ищу, что осталось";
        _work.Send(new CleanRequest { Command = CleanCommand.Force });
        Changed?.Invoke();
    }

    public void DeleteTails(IReadOnlyList<string> ids)
    {
        if (_work is null || State != FlowState.Tails || ids.Count == 0) return;
        State = FlowState.Deleting;
        ProgressText = "Готовлюсь к удалению";
        ProgressPercent = 0;
        _work.Send(new CleanRequest { Command = CleanCommand.DeleteTails, Targets = ids.Select(id => new CleanTarget { Title = id }).ToList() });
        Changed?.Invoke();
    }

    /// <summary>Leave the tails (or the program) as they are and go back to the list.</summary>
    public void Quit()
    {
        if (_work is null)
        {
            State = FlowState.Idle;
            Changed?.Invoke();
            return;
        }
        _quitting = true;
        _work.Send(new CleanRequest { Command = CleanCommand.Quit });
    }

    public void Dismiss()
    {
        if (_work is not null) return;
        State = FlowState.Idle;
        RestoreText = null;
        Changed?.Invoke();
    }

    public void Restore(string folder)
    {
        if (_work is not null || dispatcher() is not { } ui) return;
        try { _work = CleanerSession.Start(ui, new CleanRequest { Command = CleanCommand.Restore, BackupDir = folder }); }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception) { return; }
        State = FlowState.Restoring;
        ProgressText = "Возвращаю записи реестра";
        RestoreText = null;
        _work.Restored += failed => RestoreText = failed == 0
            ? "Записи реестра возвращены. Если программа ставила службу, она заработает после перезагрузки."
            : "Вернуть удалось не всё: часть записей требует прав администратора, а в окне Windows было «Нет».";
        _work.Finished += error =>
        {
            _work = null;
            State = FlowState.Done;
            Target = null;
            Backup = null;
            RestoreText ??= error ?? "Вернуть не удалось";
            Changed?.Invoke();
        };
        Changed?.Invoke();
    }

    /// <summary>The registry copies, newest first (read from the folder when asked).</summary>
    public static List<BackupInfo> Backups()
    {
        var list = new List<BackupInfo>();
        try
        {
            if (!Directory.Exists(BackupRoot)) return list;
            foreach (var folder in Directory.EnumerateDirectories(BackupRoot))
            {
                var info = Path.Combine(folder, RegFileNames.Info);
                string name;
                try { name = File.Exists(info) ? File.ReadAllText(info).Trim() : Path.GetFileName(folder); }
                catch (IOException) { name = Path.GetFileName(folder); }
                list.Add(new BackupInfo(folder, name, Directory.GetCreationTime(folder)));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return list.OrderByDescending(b => b.When).ToList();
    }

    public void Cancel()
    {
        _list?.Cancel();
        _list = null;
        _work?.Cancel();
        _work = null;
    }
}

/// <summary>File names of a registry copy (the same as the scanner's RegFile, which is not compiled here).</summary>
internal static class RegFileNames
{
    public const string Info = "info.txt";
}
