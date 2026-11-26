using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Windows.Media;
using Wipriss.Converter;
using Wipriss.Core;
using Wipriss.Core.Text;

namespace Wipriss.Modules.Converter;

internal enum RowState { Waiting, Working, Done, Failed }

/// <summary>What to do with a picture, as the owner set it in the window.</summary>
internal sealed class PictureSettings
{
    public TargetFormat Format { get; set; } = TargetFormat.Jpg;
    public ResizeKind Resize { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
    public double Percent { get; set; } = 50;
    public bool LimitWeight { get; set; }
    public double LimitMb { get; set; } = 1;
    public bool RemoveBackground { get; set; }

    public PictureSettings Clone() => (PictureSettings)MemberwiseClone();

    public ConvertOptions ToOptions() => new()
    {
        Format = Format,
        Resize = Resize,
        Width = Width,
        Height = Height,
        Percent = Percent,
        MaxBytes = LimitWeight ? (long)(LimitMb * 1024 * 1024) : null,
        RemoveBackground = RemoveBackground,
    };

    /// <summary>Checks the numbers before a run. Null: all good.</summary>
    public string? Problem()
    {
        if (Resize == ResizeKind.Pixels)
        {
            if (Width is null && Height is null) return "Укажите ширину или высоту.";
            if (Width > 32_000 || Height > 32_000) return "Больше 32 000 пикселей нельзя.";
        }
        if (Resize == ResizeKind.Percent && Percent is < 1 or > 1000) return "Проценты: от 1 до 1000.";
        if (LimitWeight && LimitMb is < 0.01 or > 1000) return "Вес: от 0,01 до 1000 МБ.";
        return null;
    }

    /// <summary>A few words for a tile with its own settings.</summary>
    public string Short()
    {
        var text = new StringBuilder(Format.ToString().ToUpperInvariant());
        if (RemoveBackground) text.Append(" · без фона");
        if (Resize == ResizeKind.Percent) text.Append($" · {Percent:0}%");
        if (Resize == ResizeKind.Pixels) text.Append($" · {Width?.ToString() ?? "…"}×{Height?.ToString() ?? "…"}");
        if (LimitWeight) text.Append($" · ≤{LimitMb.ToString("0.##", System.Globalization.CultureInfo.GetCultureInfo("ru-RU"))} МБ");
        return text.ToString();
    }
}

/// <summary>One picture in the window.</summary>
internal sealed class FileRow(string path) : INotifyPropertyChanged
{
    /// <summary>The file this tile stands for. After "Заменить оригиналы" it is the new file.</summary>
    public string Path { get; private set; } = path;
    public string Name { get; private set; } = System.IO.Path.GetFileName(path);
    public string SourceSize { get; private set; } = SizeText.Of(SafeLength(path));

    /// <summary>The original was replaced: from now on the tile is the new file.</summary>
    public void Rebase(string newPath)
    {
        Path = newPath;
        Name = System.IO.Path.GetFileName(newPath);
        SourceSize = SizeText.Of(SafeLength(newPath));
        Changed(nameof(Path));
        Changed(nameof(Name));
        Changed(nameof(SourceSize));
    }

    public RowState State { get; private set; }
    public string Result { get; private set; } = "";
    public string? Output { get; private set; }
    public bool CanReveal => Output is not null;
    public bool IsFailed => State == RowState.Failed;
    public bool IsWorking => State == RowState.Working;
    public bool IsDone => State == RowState.Done;

    /// <summary>0–100 while this picture is being converted.</summary>
    public int Progress { get; private set; }
    public string ProgressText => $"{Progress}%";
    public double ProgressFraction => Progress / 100.0;

    // ---- What the owner did with this picture ----

    /// <summary>Own settings; null: the common ones.</summary>
    public PictureSettings? Own
    {
        get => _own;
        set { _own = value; Changed(); Changed(nameof(HasOwn)); Changed(nameof(OwnText)); }
    }
    private PictureSettings? _own;

    public bool HasOwn => _own is not null;
    public string OwnText => _own?.Short() ?? "";
    public void OwnChanged() => Changed(nameof(OwnText));

    /// <summary>The picture with the owner's drawings (a PNG in <see cref="ConverterSession.TempFolder"/>), or null.</summary>
    public string? EditedPath
    {
        get => _edited;
        set { _edited = value; Changed(); Changed(nameof(IsEdited)); }
    }
    private string? _edited;

    public bool IsEdited => _edited is not null;

    /// <summary>The preview; null while it loads or when no preview could be made.</summary>
    public ImageSource? Thumb
    {
        get => _thumb;
        set { _thumb = value; Changed(); Changed(nameof(HasThumb)); }
    }
    private ImageSource? _thumb;

    public bool HasThumb => _thumb is not null;

    /// <summary>The longest side of the preview, in pixels; 0 = not loaded yet.</summary>
    public int ThumbPixels { get; set; }

    /// <summary>No preview could be made (the converter still may read the file).</summary>
    public bool NoPreview
    {
        get => _noPreview;
        set { _noPreview = value; Changed(); }
    }
    private bool _noPreview;

    public bool IsSelected
    {
        get => _selected;
        set { if (_selected == value) return; _selected = value; Changed(); }
    }
    private bool _selected;

    public event PropertyChangedEventHandler? PropertyChanged;

    public void SetWorking()
    {
        SetProgress(0, force: true);
        Set(RowState.Working, "конвертирую…", null);
    }

    public void SetProgress(int percent, bool force = false)
    {
        if (!force && (State != RowState.Working || percent <= Progress)) return;
        Progress = Math.Clamp(percent, 0, 100);
        Changed(nameof(Progress));
        Changed(nameof(ProgressText));
        Changed(nameof(ProgressFraction));
    }

    public void SetWaiting() => Set(RowState.Waiting, "", null);

    public void SetReply(ConvertReply reply, string? output = null, string? note = null)
    {
        if (!reply.Ok)
        {
            Set(RowState.Failed, reply.Error ?? "не получилось", null);
            return;
        }
        output ??= reply.Output;
        var text = new StringBuilder($"{System.IO.Path.GetFileName(output)} · {SizeText.Of(reply.Bytes)} · {reply.Width}×{reply.Height}");
        if (reply.Quality is { } q) text.Append($" · качество {q}");
        if (reply.OverLimit) text.Append(" · меньше не вышло");
        else if (reply.Shrunk) text.Append(" · уменьшено, чтобы влезть");
        if (note is not null) text.Append(" · ").Append(note);
        Set(RowState.Done, text.ToString(), output);
    }

    private void Set(RowState state, string result, string? output)
    {
        State = state;
        Result = result;
        Output = output;
        Changed(nameof(State));
        Changed(nameof(Result));
        Changed(nameof(Output));
        Changed(nameof(CanReveal));
        Changed(nameof(IsFailed));
        Changed(nameof(IsWorking));
        Changed(nameof(IsDone));
    }

    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private static long SafeLength(string path)
    {
        try { return new FileInfo(path).Length; }
        catch (Exception) { return 0; }
    }
}

internal static class SizeText
{
    private static readonly System.Globalization.CultureInfo Ru = System.Globalization.CultureInfo.GetCultureInfo("ru-RU");

    public static string Of(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} Б",
        < 1024 * 1024 => $"{bytes / 1024.0:0} КБ",
        _ => (bytes / (1024.0 * 1024.0)).ToString(bytes < 10L * 1024 * 1024 ? "0.0" : "0", Ru) + " МБ",
    };
}

/// <summary>
/// The pictures and the chosen settings. Lives as long as the module, so closing the window in the middle
/// of a batch loses nothing: open it again to see the results.
/// </summary>
internal sealed class ConverterSession
{
    private const int MaxItems = 500;
    private CancellationTokenSource? _cancel;

    public ObservableCollection<FileRow> Items { get; } = [];

    /// <summary>The settings for every picture without its own.</summary>
    public PictureSettings Common { get; } = new();

    /// <summary>The originals go to the Recycle Bin and the results take their names. Off by default.</summary>
    public bool ReplaceOriginals { get; set; }

    public bool IsRunning => _cancel is not null;

    /// <summary>The batch started or ended.</summary>
    public event Action? RunningChanged;

    /// <summary>Drawn-on pictures and previews made by the converter; emptied when the window closes.</summary>
    public static string TempFolder => Path.Combine(Path.GetTempPath(), "Wipriss-edit");

    public PictureSettings SettingsOf(FileRow row) => row.Own ?? Common;

    /// <returns>How many pictures were added.</returns>
    public int Add(IEnumerable<string> paths)
    {
        int added = 0;
        foreach (var path in paths)
        {
            if (Items.Count >= MaxItems) break;
            if (!Path.IsPathFullyQualified(path) || !File.Exists(path)) continue;
            if (Items.Any(r => string.Equals(r.Path, path, StringComparison.OrdinalIgnoreCase) && r.State != RowState.Done)) continue;
            Items.Add(new FileRow(path));
            added++;
        }
        return added;
    }

    public void Remove(FileRow row)
    {
        if (row.State == RowState.Working) return;
        Items.Remove(row);
        DeleteEdited(row);
    }

    public void Clear()
    {
        if (IsRunning) return;
        foreach (var row in Items) DeleteEdited(row);
        Items.Clear();
        DeleteTempFolder();
    }

    public static void DeleteEdited(FileRow row)
    {
        if (row.EditedPath is { } edited) TryDelete(edited);
        row.EditedPath = null;
    }

    public static void DeleteTempFolder()
    {
        try { if (Directory.Exists(TempFolder)) Directory.Delete(TempFolder, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>The first problem in the settings of these pictures, or null.</summary>
    public string? Problem(IEnumerable<FileRow> rows)
    {
        if (Common.Problem() is { } common) return common;
        foreach (var row in rows)
            if (row.Own?.Problem() is { } own) return $"{row.Name}: {own}";
        return null;
    }

    public ConvertRequest BuildRequest(IReadOnlyList<FileRow> rows)
    {
        var request = new ConvertRequest { FallbackFolder = Path.Combine(AppPaths.Root, "Конвертер") };
        foreach (var row in rows)
        {
            request.Jobs.Add(new ConvertJob
            {
                Path = row.EditedPath ?? row.Path,
                NameFrom = row.EditedPath is null ? null : row.Path,
                Options = SettingsOf(row).ToOptions(),
            });
        }
        return request;
    }

    /// <summary>Converts every picture that isn't done yet. Runs on the UI thread, waits off it.</summary>
    public async Task<string?> RunAsync()
    {
        if (IsRunning) return null;
        if (Items.Count == 0) return "Нечего конвертировать: добавьте картинки.";
        // Only new, changed (settings or drawing) and failed pictures: a second press never redoes finished work,
        // which with "Заменить оригиналы" would send the fresh results to the Recycle Bin.
        var rows = Items.Where(r => r.State is RowState.Waiting or RowState.Failed && File.Exists(r.EditedPath ?? r.Path)).ToList();
        if (rows.Count == 0)
            return Items.All(r => r.State == RowState.Done)
                ? "Всё уже готово. Чтобы сделать ещё раз, поменяйте настройки."
                : "Файлов уже нет на месте.";

        var request = BuildRequest(rows);
        bool replace = ReplaceOriginals;
        var cancel = new CancellationTokenSource();
        _cancel = cancel;
        RunningChanged?.Invoke();
        foreach (var row in rows) row.SetWaiting();
        rows[0].SetWorking();
        int kept = 0;
        try
        {
            await ConverterWorker.RunAsync(request, reply =>
            {
                if (reply.Index < 0 || reply.Index >= rows.Count) return;
                var row = rows[reply.Index];
                if (reply.Progress is { } percent)
                {
                    row.SetProgress(percent);
                    return;
                }
                if (reply.Ok && replace && reply.Output is { } output)
                {
                    var (final, note) = OriginalReplacer.Replace(row.Path, output);
                    if (note is not null) kept++;
                    else
                    {
                        // The new file is the picture now; its drawing is already in it.
                        row.Rebase(final);
                        DeleteEdited(row);
                    }
                    row.SetReply(reply, final, note);
                }
                else row.SetReply(reply);
                if (row.Output is { } saved) ShellNotify.Created(saved);
                if (reply.Index + 1 < rows.Count) rows[reply.Index + 1].SetWorking();
            }, cancel.Token);

            var unanswered = rows.Where(r => r.State is RowState.Working or RowState.Waiting).ToList();
            foreach (var row in unanswered)
                row.SetReply(new ConvertReply { Error = cancel.IsCancellationRequested ? "остановлено" : "конвертер неожиданно закрылся" });
            int failed = rows.Count(r => r.State == RowState.Failed);
            string done = failed == 0 ? $"Готово: {RuText.Count(rows.Count, "файл", "файла", "файлов")}."
                                      : $"Готово, но {RuText.Count(failed, "файл", "файла", "файлов")} не получилось.";
            if (replace && kept > 0) done += $" Оригиналы оставлены: {kept}.";
            else if (replace && failed < rows.Count) done += " Оригиналы — в Корзине.";
            return done;
        }
        catch (Exception ex) when (ex is FileNotFoundException or System.ComponentModel.Win32Exception or IOException)
        {
            foreach (var row in rows.Where(r => r.State != RowState.Done)) row.SetWaiting();
            return "Не удалось запустить конвертер. Переустановите Wipriss.";
        }
        finally
        {
            _cancel = null;
            cancel.Dispose();
            RunningChanged?.Invoke();
        }
    }

    public void Cancel() => _cancel?.Cancel();
}

/// <summary>Starts Wipriss.Converter.exe for one batch and reads its answers.</summary>
internal static class ConverterWorker
{
    public const string FileName = "Wipriss.Converter.exe";

    /// <param name="onReply">Called on the caller's thread (the UI) for each finished file.</param>
    public static async Task RunAsync(ConvertRequest request, Action<ConvertReply> onReply, CancellationToken cancel)
    {
        var exe = Path.Combine(AppContext.BaseDirectory, FileName);
        if (!File.Exists(exe)) throw new FileNotFoundException(FileName);

        var start = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
            WorkingDirectory = AppContext.BaseDirectory,
        };
        using var process = Process.Start(start) ?? throw new IOException("converter did not start");
        // Background work: the owner's apps keep priority while a big batch runs.
        try { process.PriorityClass = ProcessPriorityClass.BelowNormal; } catch (Exception) { }

        using var stop = cancel.Register(() => { try { process.Kill(entireProcessTree: true); } catch (Exception) { } });

        await process.StandardInput.WriteAsync(JsonSerializer.Serialize(request, ProtocolJson.Default.ConvertRequest));
        process.StandardInput.Close();

        while (await process.StandardOutput.ReadLineAsync(CancellationToken.None) is { } line)
        {
            ConvertReply? reply;
            try { reply = JsonSerializer.Deserialize(line, ProtocolJson.Default.ConvertReply); }
            catch (JsonException) { continue; }
            if (reply is not null) onReply(reply);
        }
        await process.WaitForExitAsync(CancellationToken.None);
    }
}
