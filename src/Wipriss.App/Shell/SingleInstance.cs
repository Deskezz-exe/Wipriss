namespace Wipriss.App.Shell;

/// <summary>
/// Keeps one Wipriss per user session. A second launch just asks the running one to open settings.
/// Waiting costs nothing: the thread pool wakes up only when the event is signalled.
/// </summary>
internal sealed class SingleInstance : IDisposable
{
    private const string MutexName = @"Local\Wipriss.Instance";
    private const string EventName = @"Local\Wipriss.Activate";

    private readonly Mutex _mutex;
    private readonly EventWaitHandle _signal;
    private readonly RegisteredWaitHandle _wait;

    private SingleInstance(Mutex mutex)
    {
        _mutex = mutex;
        _signal = new EventWaitHandle(false, EventResetMode.AutoReset, EventName);
        _wait = ThreadPool.RegisterWaitForSingleObject(_signal, (_, _) => Activated?.Invoke(), null, Timeout.Infinite, executeOnlyOnce: false);
    }

    /// <summary>Raised on a thread-pool thread when another launch asks to be shown.</summary>
    public event Action? Activated;

    public static SingleInstance? TryAcquire()
    {
        var mutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);
        if (createdNew) return new SingleInstance(mutex);
        mutex.Dispose();
        return null;
    }

    public static void SignalRunningInstance()
    {
        if (EventWaitHandle.TryOpenExisting(EventName, out var handle))
        {
            handle.Set();
            handle.Dispose();
        }
    }

    public void Dispose()
    {
        _wait.Unregister(null);
        _signal.Dispose();
        _mutex.ReleaseMutex();
        _mutex.Dispose();
    }
}
