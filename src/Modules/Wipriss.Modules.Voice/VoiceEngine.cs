using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows.Threading;
using Wipriss.Core;
using Wipriss.Voice;

namespace Wipriss.Modules.Voice;

/// <summary>
/// One running Wipriss.Voice.exe. Answers arrive on the UI thread. Disposing closes its stdin: the engine
/// closes the microphone and exits by itself (and is killed if it does not within a moment).
/// </summary>
internal sealed class VoiceEngine : IDisposable
{
    public const string FileName = "Wipriss.Voice.exe";

    private readonly Process _process;
    private readonly StreamWriter _input;
    private readonly Dispatcher _dispatcher;
    private bool _disposed;

    private VoiceEngine(Process process, Dispatcher dispatcher)
    {
        _process = process;
        _dispatcher = dispatcher;
        _input = process.StandardInput;
        _input.AutoFlush = true;
        _ = ReadAsync();
    }

    /// <summary>An answer from the engine (UI thread).</summary>
    public event Action<VoiceReply>? Reply;

    /// <summary>The engine is gone: unloaded after the idle time, crashed or closed (UI thread).</summary>
    public event Action? Exited;

    public static string ExePath => Path.Combine(AppContext.BaseDirectory, FileName);

    public static VoiceEngine Start(Dispatcher dispatcher) => new(Launch(), dispatcher);

    public void Send(VoiceRequest request)
    {
        if (_disposed) return;
        try { _input.WriteLine(JsonSerializer.Serialize(request, VoiceJson.Default.VoiceRequest)); }
        catch (IOException) { } // already gone; Exited follows
        catch (ObjectDisposedException) { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _input.Close(); } catch (IOException) { }
        Task.Run(() =>
        {
            try
            {
                if (!_process.WaitForExit(3000)) _process.Kill(entireProcessTree: true);
            }
            catch (Exception) { }
            _process.Dispose();
        });
    }

    private async Task ReadAsync()
    {
        try
        {
            while (await _process.StandardOutput.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                VoiceReply? reply;
                try { reply = JsonSerializer.Deserialize(line, VoiceJson.Default.VoiceReply); }
                catch (JsonException) { continue; }
                if (reply is not null) _ = _dispatcher.BeginInvoke(() => { if (!_disposed) Reply?.Invoke(reply); });
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { }
        _ = _dispatcher.BeginInvoke(() => { if (!_disposed) Exited?.Invoke(); });
    }

    /// <summary>Downloads the model in a separate engine process; <paramref name="progress"/> runs on the caller's thread.</summary>
    public static async Task<VoiceError> DownloadAsync(string modelsDir, Action<int> progress, CancellationToken cancel)
    {
        using var process = Launch();
        try { process.PriorityClass = ProcessPriorityClass.BelowNormal; } catch (Exception) { }
        using var stop = cancel.Register(() => { try { process.Kill(entireProcessTree: true); } catch (Exception) { } });

        await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(
            new VoiceRequest { Command = VoiceCommand.Download, ModelsDir = modelsDir }, VoiceJson.Default.VoiceRequest));
        process.StandardInput.Close();

        var result = VoiceError.EngineFailed;
        while (await process.StandardOutput.ReadLineAsync(CancellationToken.None) is { } line)
        {
            VoiceReply? reply;
            try { reply = JsonSerializer.Deserialize(line, VoiceJson.Default.VoiceReply); }
            catch (JsonException) { continue; }
            switch (reply?.Kind)
            {
                case VoiceReplyKind.Progress: progress(reply.Percent); break;
                case VoiceReplyKind.Downloaded: result = VoiceError.None; break;
                case VoiceReplyKind.Error: result = reply.Error; break;
            }
        }
        await process.WaitForExitAsync(CancellationToken.None);
        return result;
    }

    private static Process Launch()
    {
        var exe = ExePath;
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
        return Process.Start(start) ?? throw new IOException("voice engine did not start");
    }

    /// <summary>Where models live: Wipriss\Служебное\Модели.</summary>
    public static string ModelsDir => Path.Combine(AppPaths.DataDir, "Модели");
}
