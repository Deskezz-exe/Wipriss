using System.IO;
using System.Windows;
using Wipriss.Cleaner;
using Wipriss.Core.Modules;

namespace Wipriss.Modules.Cleaner;

/// <summary>
/// The cleaner: a search shows every finding with its colour, size and reasons; the owner selects and
/// deletes after a confirmation. Searching and deleting run in Wipriss.Cleaner.exe, only when asked;
/// in between nothing runs. The tile opens a separate, resizable window.
/// </summary>
public sealed class CleanerModule : IModule
{
    private IModuleHost? _host;
    private CleanerSession? _session;
    private CleanerWindow? _window;

    public ModuleInfo Info { get; } = ModuleCatalog.Get("cleaner");

    /// <summary>The panel only opens the window.</summary>
    public Size PanelSize => new(320, 120);

    internal CleanerSettings Settings { get; private set; } = new();

    /// <summary>Findings of the last search (kept until the next one; a few hundred small records).</summary>
    internal List<CleanGroup>? Results { get; private set; }

    internal bool Busy => _session is not null;
    internal bool Deleting { get; private set; }
    internal string ProgressText { get; private set; } = "";
    internal int ProgressPercent { get; private set; }
    internal string? LastError { get; private set; }

    /// <summary>What the last deletion did (shown until dismissed): bytes freed, files left, free space before.</summary>
    internal DeleteResult? LastDelete { get; private set; }

    internal void DismissDelete()
    {
        LastDelete = null;
        Changed?.Invoke();
    }

    /// <summary>Something changed: progress, results, hidden paths (UI thread).</summary>
    internal event Action? Changed;

    public string Summary =>
        Busy ? (Deleting ? "удаляю…" : "ищу…")
        : Settings.LastScan is null ? "найти мусор"
        : Settings.LastGreenBytes > 0 ? "можно удалить " + Sizes.Format(Settings.LastGreenBytes)
        : "мусора нет";

    public void Start(IModuleHost host)
    {
        _host = host;
        Settings = CleanerSettings.Load();
    }

    public void Stop()
    {
        Cancel();
        _window?.Close();
    }

    public FrameworkElement CreatePanel(IPanelContext panel) => new CleanerPanel(this, panel);

    internal void OpenWindow()
    {
        if (_window is null)
        {
            _window = new CleanerWindow(this);
            _window.Closed += (_, _) => { _window = null; _host?.TrimMemory(); };
        }
        if (!_window.IsVisible) _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    internal void StartScan()
    {
        if (Busy) return;
        var found = new List<CleanGroup>();
        var session = Launch(new CleanRequest { Command = CleanCommand.Scan, Hidden = [.. Settings.Hidden] }, "Запускаю поиск");
        if (session is null) return;
        session.Found += found.Add;
        session.Finished += error =>
        {
            _session = null;
            LastError = error;
            if (error is null)
            {
                Results = found;
                Settings.LastScan = DateTime.Now;
                Settings.LastGreenBytes = found.SelectMany(g => g.Items).Where(i => i.Color == CleanColor.Green && i.Size > 0).Sum(i => i.Size);
                Settings.Save();
            }
            Changed?.Invoke();
        };
    }

    /// <summary>Deletes the items (by group id and title: the scanner finds them again itself), then searches again.</summary>
    internal void Delete(IReadOnlyList<(CleanGroup Group, CleanItem Item)> items)
    {
        if (Busy || items.Count == 0) return;
        var request = new CleanRequest
        {
            Command = CleanCommand.Delete,
            Hidden = [.. Settings.Hidden],
            Targets = items.Select(x => new CleanTarget { GroupId = x.Group.Id, Title = x.Item.Title }).ToList(),
        };
        var session = Launch(request, "Готовлюсь к удалению");
        if (session is null) return;
        Deleting = true;
        LastDelete = null;
        long freeBefore = Disk.Free();
        session.Deleted += (freed, failed) => LastDelete = new DeleteResult(freed, failed, freeBefore);
        session.Finished += error =>
        {
            _session = null;
            Deleting = false;
            LastError = error is null ? null : "Удаление прервалось";
            Changed?.Invoke();
            StartScan(); // show what is left
        };
    }

    private CleanerSession? Launch(CleanRequest request, string firstText)
    {
        if (_host is null) return null;
        LastError = null;
        ProgressText = firstText;
        ProgressPercent = 0;
        try { _session = CleanerSession.Start(_host.Dispatcher, request); }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception)
        {
            LastError = "Не удалось запустить: нет файла Wipriss.Cleaner.exe рядом с Wipriss.";
            Changed?.Invoke();
            return null;
        }
        _session.Progress += (text, percent) => { ProgressText = text; ProgressPercent = percent; Changed?.Invoke(); };
        Changed?.Invoke();
        return _session;
    }

    internal void Cancel()
    {
        if (_session is null) return;
        _session.Cancel();
        _session = null;
        Deleting = false;
        Changed?.Invoke();
    }

    /// <summary>Never show this path again (the item disappears now and from later searches).</summary>
    internal void Hide(CleanItem item)
    {
        if (item.Paths.Count == 0) return;
        foreach (var path in item.Paths)
            if (!Settings.Hidden.Contains(path, StringComparer.OrdinalIgnoreCase)) Settings.Hidden.Add(path);
        Settings.Save();
        if (Results is not null)
        {
            foreach (var group in Results) group.Items.Remove(item);
            Results.RemoveAll(g => g.Items.Count == 0);
        }
        Changed?.Invoke();
    }

    internal void ShowHiddenAgain()
    {
        Settings.Hidden.Clear();
        Settings.Save();
        Changed?.Invoke();
    }
}

/// <summary>The result of one deletion.</summary>
internal sealed record DeleteResult(long Freed, int Failed, long FreeBefore);

/// <summary>The drives: total and free bytes (read when shown, no watching).</summary>
internal static class Disk
{
    public static string Name => Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows)) ?? @"C:\";

    /// <summary>Free bytes on all fixed drives together (to compare before and after a deletion).</summary>
    public static long Free() => All().Sum(d => d.Free);

    /// <summary>Fixed drives that are ready, the system drive first.</summary>
    public static List<(string Name, long Total, long Free)> All()
    {
        var list = new List<(string, long, long)>();
        try
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                try
                {
                    if (drive.DriveType != DriveType.Fixed || !drive.IsReady) continue;
                    list.Add((drive.Name, drive.TotalSize, drive.AvailableFreeSpace));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return list.OrderBy(d => d.Item1.Equals(Name, StringComparison.OrdinalIgnoreCase) ? 0 : 1).ThenBy(d => d.Item1).ToList();
    }
}

internal static class Sizes
{
    public static string Format(long bytes)
    {
        if (bytes < 0) return "—";
        if (bytes < 1024) return $"{bytes} Б";
        double kb = bytes / 1024.0;
        if (kb < 1024) return $"{kb:0} КБ";
        double mb = kb / 1024;
        if (mb < 1024) return $"{mb:0} МБ";
        double gb = mb / 1024;
        return gb < 10 ? $"{gb:0.0} ГБ" : $"{gb:0} ГБ";
    }
}
