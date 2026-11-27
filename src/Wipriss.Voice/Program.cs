using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace Wipriss.Voice;

/// <summary>
/// Wipriss.Voice.exe: reads <see cref="VoiceRequest"/> lines from stdin and answers with
/// <see cref="VoiceReply"/> lines. No window and no arguments: only Wipriss starts it. Goes to the
/// network only for "download", and only to the pinned model links.
/// </summary>
internal static class Program
{
    /// <summary>A forgotten recording stops by itself.</summary>
    private const int MaxRecordSeconds = 5 * 60;

    private static readonly Lock Gate = new(), OutGate = new();
    private static StreamWriter? _stdout;
    private static SpeechRecognizer? _recognizer;
    private static Microphone? _microphone;
    private static Timer? _idle;
    private static int _idleMinutes;

    private static int Main()
    {
        using var stdin = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
        _stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
        try
        {
            string? line;
            while ((line = stdin.ReadLine()) is not null)
            {
                VoiceRequest? request;
                try { request = JsonSerializer.Deserialize(line, VoiceJson.Default.VoiceRequest); }
                catch (JsonException) { request = null; }
                if (request is null) { Fail(VoiceError.BadRequest); continue; }

                switch (request.Command)
                {
                    case VoiceCommand.Download:
                        return Download(request);
                    case VoiceCommand.Load:
                        Load(request);
                        break;
                    case VoiceCommand.Start:
                        StartListening();
                        break;
                    case VoiceCommand.Stop:
                        StopListening(recognize: true);
                        break;
                    case VoiceCommand.Cancel:
                        StopListening(recognize: false);
                        break;
                    case VoiceCommand.Quit:
                        return 0;
                    default:
                        Fail(VoiceError.BadRequest);
                        break;
                }
            }
            return 0; // Wipriss closed stdin (or is gone)
        }
        finally
        {
            lock (Gate)
            {
                _idle?.Dispose();
                _microphone?.Dispose();
                _recognizer?.Dispose();
            }
        }
    }

    private static int Download(VoiceRequest request)
    {
        var model = OpenStore(request.ModelsDir);
        if (model is null) { Fail(VoiceError.BadRequest); return 2; }
        using var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 5,
            AutomaticDecompression = DecompressionMethods.None,
            UseCookies = false,
            UseProxy = true,
        };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromHours(2) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Wipriss");
        try
        {
            model.DownloadAsync(http, percent => Send(new VoiceReply { Kind = VoiceReplyKind.Progress, Percent = percent }),
                CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (ModelDownloadException e) { Fail(e.Error); return 1; }
        catch (TaskCanceledException) { Fail(VoiceError.Network); return 1; } // timeout
        Send(new VoiceReply { Kind = VoiceReplyKind.Downloaded, Percent = 100 });
        return 0;
    }

    private static void Load(VoiceRequest request)
    {
        lock (Gate)
        {
            _idleMinutes = Math.Clamp(request.IdleMinutes, 0, 24 * 60);
            if (_recognizer is null)
            {
                var model = OpenStore(request.ModelsDir);
                if (model is null) { Fail(VoiceError.BadRequest); return; }
                var check = model.Verify();
                if (check != VoiceError.None) { Fail(check); return; }
                try { _recognizer = new SpeechRecognizer(model); }
                catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException
                                              or BadImageFormatException or SEHException)
                {
                    Fail(VoiceError.EngineFailed);
                    return;
                }
            }
            Send(new VoiceReply { Kind = VoiceReplyKind.Ready });
            ArmIdle();
        }
    }

    private static void StartListening()
    {
        lock (Gate)
        {
            if (_recognizer is null) { Fail(VoiceError.ModelMissing); return; }
            _idle?.Change(Timeout.Infinite, Timeout.Infinite);
            // The limit fires on the microphone thread; stopping from there would wait on itself.
            _microphone ??= new Microphone(MaxRecordSeconds,
                () => ThreadPool.QueueUserWorkItem(_ => StopListening(recognize: true)),
                level => Send(new VoiceReply { Kind = VoiceReplyKind.Level, Percent = level }));
            if (_microphone.IsRecording) return;
            var result = _microphone.Start();
            if (result != VoiceError.None) { Fail(result); ArmIdle(); return; }
            Send(new VoiceReply { Kind = VoiceReplyKind.Listening });
        }
    }

    private static void StopListening(bool recognize)
    {
        lock (Gate)
        {
            if (_microphone is not { IsRecording: true } || _recognizer is null)
            {
                // Already stopped (the 5-minute limit, a failed start): Wipriss still waits for an answer.
                if (recognize) Send(new VoiceReply { Kind = VoiceReplyKind.Text, Text = "" });
                return;
            }
            var samples = _microphone.Stop();
            if (recognize)
            {
                Send(new VoiceReply { Kind = VoiceReplyKind.Recognizing });
                string text;
                try { text = _recognizer.Recognize(samples); }
                catch (SEHException) { Fail(VoiceError.EngineFailed); ArmIdle(); return; }
                Send(new VoiceReply
                {
                    Kind = VoiceReplyKind.Text,
                    Text = text,
                    Seconds = Math.Round(samples.Length / (double)Microphone.SampleRate, 1),
                });
            }
            Array.Clear(samples); // the voice does not linger in memory longer than needed
            ArmIdle();
        }
    }

    /// <summary>"Unload after N minutes": the process exits when nobody dictated for that long.</summary>
    private static void ArmIdle()
    {
        if (_idleMinutes == 0) return;
        _idle ??= new Timer(_ =>
        {
            lock (Gate)
            {
                if (_microphone is { IsRecording: true }) return;
                _recognizer?.Dispose();
                _recognizer = null;
                Environment.Exit(0);
            }
        });
        _idle.Change(TimeSpan.FromMinutes(_idleMinutes), Timeout.InfiniteTimeSpan);
    }

    /// <summary>The models folder must be a full local path; the model lives in a fixed subfolder of it.</summary>
    internal static ModelStore? OpenStore(string? modelsDir)
    {
        if (string.IsNullOrWhiteSpace(modelsDir) || !Path.IsPathFullyQualified(modelsDir)) return null;
        if (modelsDir.StartsWith(@"\\", StringComparison.Ordinal)) return null; // no network shares
        return ModelStore.GigaAmV3(Path.GetFullPath(modelsDir));
    }

    private static void Fail(VoiceError error) => Send(new VoiceReply { Kind = VoiceReplyKind.Error, Error = error });

    /// <summary>
    /// Writes one answer. Its own lock: the microphone thread sends levels while the main thread may hold
    /// <see cref="Gate"/> and wait for that thread to finish.
    /// </summary>
    private static void Send(VoiceReply reply)
    {
        lock (OutGate)
        {
            try { _stdout?.WriteLine(JsonSerializer.Serialize(reply, VoiceJson.Default.VoiceReply)); }
            catch (IOException) { Environment.Exit(3); } // Wipriss is gone: close the microphone and leave
        }
    }
}
