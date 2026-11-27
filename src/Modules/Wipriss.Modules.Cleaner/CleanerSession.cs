using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows.Threading;
using Wipriss.Cleaner;

namespace Wipriss.Modules.Cleaner;

/// <summary>
/// One search (or one deletion) in Wipriss.Cleaner.exe. Answers arrive on the UI thread. Cancelling closes the scanner's
/// stdin (it stops by itself) and kills it if it does not within a moment.
/// </summary>
internal sealed class CleanerSession
{
    public const string FileName = "Wipriss.Cleaner.exe";

    private readonly Process _process;
    private readonly Dispatcher _dispatcher;
    private bool _cancelled;

    public event Action<string, int>? Progress;
    public event Action<CleanGroup>? Found;

    /// <summary>Delete finished: bytes freed, files that could not be deleted.</summary>
    public event Action<long, int>? Deleted;

    public event Action<AppEntry>? ProgramFound;
    public event Action<UninstallStage, string?>? StageChanged;
    public event Action<List<TailItem>>? TailsFound;
    /// <summary>Tails deleted: bytes freed, what could not be deleted, the registry copy folder.</summary>
    public event Action<long, int, string?>? TailsDeleted;
    public event Action<int>? Restored;

    /// <summary>The work ended: null = it finished, otherwise what went wrong.</summary>
    public event Action<string?>? Finished;

    private CleanerSession(Process process, Dispatcher dispatcher)
    {
        _process = process;
        _dispatcher = dispatcher;
    }

    public static CleanerSession Start(Dispatcher dispatcher, CleanRequest request)
    {
        var exe = Path.Combine(AppContext.BaseDirectory, FileName);
        if (!File.Exists(exe)) throw new FileNotFoundException(FileName);
        var process = Process.Start(new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
            WorkingDirectory = AppContext.BaseDirectory,
        }) ?? throw new IOException("cleaner did not start");

        var session = new CleanerSession(process, dispatcher);
        process.StandardInput.AutoFlush = true;
        process.StandardInput.WriteLine(JsonSerializer.Serialize(request, CleanJson.Default.CleanRequest));
        _ = session.ReadAsync();
        return session;
    }

    /// <summary>The next step of an uninstall (the scanner waits for it).</summary>
    public void Send(CleanRequest request)
    {
        if (_cancelled) return;
        try { _process.StandardInput.WriteLine(JsonSerializer.Serialize(request, CleanJson.Default.CleanRequest)); }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { }
    }

    public void Cancel()
    {
        if (_cancelled) return;
        _cancelled = true;
        try { _process.StandardInput.Close(); } catch (IOException) { }
        Task.Run(() =>
        {
            try { if (!_process.WaitForExit(2000)) _process.Kill(entireProcessTree: true); }
            catch (Exception) { }
        });
    }

    private async Task ReadAsync()
    {
        string? error = "Поиск прервался";
        try
        {
            while (await _process.StandardOutput.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                CleanReply? reply;
                try { reply = JsonSerializer.Deserialize(line, CleanJson.Default.CleanReply); }
                catch (JsonException) { continue; }
                switch (reply?.Kind)
                {
                    case CleanReplyKind.Progress:
                        Post(() => Progress?.Invoke(reply.Text ?? "", reply.Percent));
                        break;
                    case CleanReplyKind.Group when reply.Group is { } group:
                        Post(() => Found?.Invoke(group));
                        break;
                    case CleanReplyKind.Done:
                        error = null;
                        break;
                    case CleanReplyKind.Deleted:
                        error = null;
                        Post(() => Deleted?.Invoke(reply.Bytes, reply.Count));
                        break;
                    case CleanReplyKind.Error:
                        error = reply.Text is { Length: > 0 } text && !text.Contains("Exception") && text != "bad request" ? text : "Поиск прервался из-за ошибки";
                        break;
                    case CleanReplyKind.Program when reply.Program is { } program:
                        Post(() => ProgramFound?.Invoke(program));
                        break;
                    case CleanReplyKind.Stage:
                        Post(() => StageChanged?.Invoke(reply.Stage, reply.Text));
                        break;
                    case CleanReplyKind.Tails:
                        Post(() => TailsFound?.Invoke(reply.Tails ?? []));
                        break;
                    case CleanReplyKind.TailsDeleted:
                        error = null;
                        Post(() => TailsDeleted?.Invoke(reply.Bytes, reply.Count, reply.BackupDir));
                        break;
                    case CleanReplyKind.Restored:
                        error = null;
                        Post(() => Restored?.Invoke(reply.Count));
                        break;
                }
            }
            await _process.WaitForExitAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { }
        finally { _process.Dispose(); }
        Post(() => Finished?.Invoke(error));
    }

    private void Post(Action action) => _dispatcher.BeginInvoke(() => { if (!_cancelled) action(); });
}
