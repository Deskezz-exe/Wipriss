using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Wipriss.Core.Timing;

/// <summary>
/// Fires once at an exact wall-clock moment using a Windows waitable timer.
/// Nothing runs until then: no ticking, no polling. If the PC was asleep at that moment,
/// it fires right after wake-up; if the clock is changed, Windows moves the timer with it.
/// </summary>
public sealed class AbsoluteTimer : IDisposable
{
    private const uint TimerAllAccess = 0x1F0003;

    private readonly TimerHandle _handle;
    private readonly RegisteredWaitHandle _wait;
    private bool _disposed;

    /// <param name="onFire">Called on a thread-pool thread.</param>
    public AbsoluteTimer(Action onFire)
    {
        var handle = CreateWaitableTimerExW(IntPtr.Zero, null, 0, TimerAllAccess);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastPInvokeError());
        _handle = new TimerHandle(handle);
        _wait = ThreadPool.RegisterWaitForSingleObject(_handle, (_, _) => onFire(), null, Timeout.Infinite, executeOnlyOnce: false);
    }

    /// <summary>Sets (or moves) the moment to fire. A moment in the past fires immediately.</summary>
    public void Schedule(DateTime utc)
    {
        long due = utc.ToUniversalTime().ToFileTimeUtc(); // positive value = absolute time
        if (!SetWaitableTimer(_handle.SafeWaitHandle, ref due, 0, IntPtr.Zero, IntPtr.Zero, false))
            throw new Win32Exception(Marshal.GetLastPInvokeError());
    }

    public void Cancel() => CancelWaitableTimer(_handle.SafeWaitHandle);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Cancel();
        _wait.Unregister(null);
        _handle.Dispose();
    }

    private sealed class TimerHandle : WaitHandle
    {
        public TimerHandle(SafeWaitHandle handle) => SafeWaitHandle = handle;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeWaitHandle CreateWaitableTimerExW(IntPtr attributes, string? name, uint flags, uint access);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetWaitableTimer(SafeWaitHandle timer, ref long dueTime, int period, IntPtr completion, IntPtr argument, bool resume);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CancelWaitableTimer(SafeWaitHandle timer);
}
