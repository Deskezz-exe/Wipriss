using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;

namespace Wipriss.App.Shell;

/// <summary>
/// Keeps one Wipriss per user session. A second launch passes its command line
/// (e.g. <c>--open notes</c>) to the running one through a named pipe that only the
/// current Windows user can open. Waiting costs nothing: no thread is blocked.
/// </summary>
internal sealed class SingleInstance : IDisposable
{
    private const string MutexName = @"Local\Wipriss.Instance";
    private const int MaxMessageBytes = 2048;
    private static readonly string PipeName = $"Wipriss.Commands.{Process.GetCurrentProcess().SessionId}";

    private readonly Mutex _mutex;
    private readonly CancellationTokenSource _stop = new();

    private SingleInstance(Mutex mutex) => _mutex = mutex;

    /// <summary>Raised on a thread-pool thread with the other launch's arguments.</summary>
    public event Action<string[]>? CommandReceived;

    public static SingleInstance? TryAcquire()
    {
        var mutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);
        if (createdNew) return new SingleInstance(mutex);
        mutex.Dispose();
        return null;
    }

    public void StartListening() => _ = ListenAsync(_stop.Token);

    private async Task ListenAsync(CancellationToken cancel)
    {
        while (!cancel.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(cancel).ConfigureAwait(false);

                var buffer = new byte[MaxMessageBytes];
                int total = 0, read;
                while (total < buffer.Length && (read = await server.ReadAsync(buffer.AsMemory(total), cancel).ConfigureAwait(false)) > 0)
                    total += read;

                var args = Encoding.UTF8.GetString(buffer, 0, total).Split('\n', StringSplitOptions.RemoveEmptyEntries);
                CommandReceived?.Invoke(args);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (IOException)
            {
                // A client dropped mid-message; wait for the next one.
            }
            catch (UnauthorizedAccessException)
            {
                return;
            }
        }
    }

    public static void SendToRunningInstance(string[] args)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect(3000);
            // We were just launched by the user (e.g. a shortcut hotkey): let the running copy take focus.
            AllowSetForegroundWindow(-1);
            var bytes = Encoding.UTF8.GetBytes(string.Join('\n', args));
            client.Write(bytes, 0, Math.Min(bytes.Length, MaxMessageBytes));
            client.Flush();
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            // The running copy is busy or closing; nothing else to do.
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);

    public void Dispose()
    {
        _stop.Cancel();
        _stop.Dispose();
        _mutex.ReleaseMutex();
        _mutex.Dispose();
    }
}
