using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Wipriss.Converter;
using Wipriss.Core;
using Wipriss.Core.Text;

namespace Wipriss.Modules.Converter;

internal enum RowState { Waiting, Working, Done, Failed }

/// <summary>One picture in the list.</summary>
internal sealed class FileRow(string path) : INotifyPropertyChanged
{
    public string Path { get; } = path;
    public string Name { get; } = System.IO.Path.GetFileName(path);
    public string SourceSize { get; } = SizeText.Of(SafeLength(path));

    public RowState State { get; private set; }
    public string Result { get; private set; } = "";
    public string? Output { get; private set; }
    public bool CanReveal => Output is not null;
    public bool IsFailed => State == RowState.Failed;
    public bool IsWorking => State == RowState.Working;

    /// <summary>0–100 while this picture is being converted.</summary>
    public int Progress { get; private set; }
    public string ProgressText => $"{Progress}%";
    public double ProgressFraction => Progress / 100.0;

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

    public void SetReply(ConvertReply reply)
    {
        if (!reply.Ok)
        {
            Set(RowState.Failed, reply.Error ?? "не получилось", null);
            return;
        }
        var text = new StringBuilder($"{System.IO.Path.GetFileName(reply.Output)} · {SizeText.Of(reply.Bytes)} · {reply.Width}×{reply.Height}");
        if (reply.Quality is { } q) text.Append($" · качество {q}");
        if (reply.OverLimit) text.Append(" · меньше не вышло");
        else if (reply.Shrunk) text.Append(" · уменьшено, чтобы влезть");
        Set(RowState.Done, text.ToString(), reply.Output);
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
/// The list and the chosen options. Lives as long as the module, so closing the panel in the middle
/// of a batch loses nothing: open it again to see the results.
/// </summary>
internal sealed class ConverterSession
{
    private const int MaxItems = 500;
    private CancellationTokenSource? _cancel;

    public ObservableCollection<FileRow> Items { get; } = [];

    // Options, remembered while Wipriss runs.
    public TargetFormat Format { get; set; } = TargetFormat.Jpg;
    public ResizeKind Resize { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
    public double Percent { get; set; } = 50;
    public bool LimitWeight { get; set; }
    public double LimitMb { get; set; } = 2;

    public bool IsRunning => _cancel is not null;

    /// <summary>The batch started or ended.</summary>
    public event Action? RunningChanged;

    public void Add(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            if (Items.Count >= MaxItems) break;
            if (!Path.IsPathFullyQualified(path) || !File.Exists(path)) continue;
            if (Items.Any(r => string.Equals(r.Path, path, StringComparison.OrdinalIgnoreCase) && r.State != RowState.Done)) continue;
            Items.Add(new FileRow(path));
        }
    }

    public void Remove(FileRow row)
    {
        if (row.State != RowState.Working) Items.Remove(row);
    }

    public void Clear()
    {
        if (IsRunning) return;
        Items.Clear();
    }

    public ConvertRequest BuildRequest(IReadOnlyList<FileRow> rows) => new()
    {
        Files = rows.Select(r => r.Path).ToList(),
        Format = Format,
        Resize = Resize,
        Width = Width,
        Height = Height,
        Percent = Percent,
        MaxBytes = LimitWeight ? (long)(LimitMb * 1024 * 1024) : null,
        FallbackFolder = Path.Combine(AppPaths.Root, "Конвертер"),
    };

    /// <summary>Converts every picture that isn't done yet. Runs on the UI thread, waits off it.</summary>
    public async Task<string?> RunAsync()
    {
        if (IsRunning) return null;
        if (Items.Count == 0) return "Нечего конвертировать: добавьте картинки.";
        // New and failed pictures; if all are done, the whole list again (say, in another format).
        var rows = Items.Where(r => r.State is RowState.Waiting or RowState.Failed).ToList();
        if (rows.Count == 0) rows = [.. Items];

        var request = BuildRequest(rows);
        var cancel = new CancellationTokenSource();
        _cancel = cancel;
        RunningChanged?.Invoke();
        foreach (var row in rows) row.SetWaiting();
        rows[0].SetWorking();
        try
        {
            await ConverterWorker.RunAsync(request, reply =>
            {
                if (reply.Index < 0 || reply.Index >= rows.Count) return;
                if (reply.Progress is { } percent)
                {
                    rows[reply.Index].SetProgress(percent);
                    return;
                }
                rows[reply.Index].SetReply(reply);
                if (reply.Index + 1 < rows.Count) rows[reply.Index + 1].SetWorking();
            }, cancel.Token);

            var unanswered = rows.Where(r => r.State is RowState.Working or RowState.Waiting).ToList();
            foreach (var row in unanswered)
                row.SetReply(new ConvertReply { Error = cancel.IsCancellationRequested ? "остановлено" : "конвертер неожиданно закрылся" });
            int failed = rows.Count(r => r.State == RowState.Failed);
            return failed == 0 ? $"Готово: {RuText.Count(rows.Count, "файл", "файла", "файлов")}."
                               : $"Готово, но {RuText.Count(failed, "файл", "файла", "файлов")} не получилось.";
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
