using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace Wipriss.App.Shell;

/// <summary>
/// Tells when the screen goes off or the session is locked, so the pill's figures stop ticking.
/// Windows sends both as window messages; nothing is polled.
/// </summary>
internal sealed class ScreenStateWatcher : IDisposable
{
    private const int WM_POWERBROADCAST = 0x0218, PBT_POWERSETTINGCHANGE = 0x8013;
    private const int WM_WTSSESSION_CHANGE = 0x02B1, WTS_SESSION_LOCK = 7, WTS_SESSION_UNLOCK = 8;
    private const int NOTIFY_FOR_THIS_SESSION = 0, DEVICE_NOTIFY_WINDOW_HANDLE = 0;
    private static Guid ConsoleDisplayState = new("6FE69556-704A-47A0-8F24-C28D936FDA47");

    private readonly HwndSource _source;
    private readonly IntPtr _power;
    private readonly bool _session;
    private bool _screenOn = true, _unlocked = true, _disposed;

    public ScreenStateWatcher(HwndSource source)
    {
        _source = source;
        _source.AddHook(WndProc);
        // Windows answers the registration at once with the current display state.
        _power = RegisterPowerSettingNotification(source.Handle, ref ConsoleDisplayState, DEVICE_NOTIFY_WINDOW_HANDLE);
        _session = WTSRegisterSessionNotification(source.Handle, NOTIFY_FOR_THIS_SESSION);
    }

    /// <summary>(screen on, session unlocked).</summary>
    public event Action<bool, bool>? Changed;

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_POWERBROADCAST && wParam.ToInt64() == PBT_POWERSETTINGCHANGE && lParam != IntPtr.Zero)
        {
            // POWERBROADCAST_SETTING: GUID (16 bytes), DataLength (4), Data. 0 = off, 1 = on, 2 = dimmed.
            var guid = Marshal.PtrToStructure<Guid>(lParam);
            if (guid == ConsoleDisplayState && Marshal.ReadInt32(lParam, 16) >= 4)
                Set(Marshal.ReadInt32(lParam, 20) != 0, _unlocked);
        }
        else if (msg == WM_WTSSESSION_CHANGE)
        {
            int code = (int)wParam.ToInt64();
            if (code == WTS_SESSION_LOCK) Set(_screenOn, false);
            else if (code == WTS_SESSION_UNLOCK) Set(_screenOn, true);
        }
        return IntPtr.Zero;
    }

    private void Set(bool screenOn, bool unlocked)
    {
        if (screenOn == _screenOn && unlocked == _unlocked) return;
        _screenOn = screenOn;
        _unlocked = unlocked;
        Changed?.Invoke(screenOn, unlocked);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _source.RemoveHook(WndProc);
        if (_power != IntPtr.Zero) UnregisterPowerSettingNotification(_power);
        if (_session) WTSUnRegisterSessionNotification(_source.Handle);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr RegisterPowerSettingNotification(IntPtr hRecipient, ref Guid powerSettingGuid, int flags);

    [DllImport("user32.dll")]
    private static extern bool UnregisterPowerSettingNotification(IntPtr handle);

    [DllImport("wtsapi32.dll")]
    private static extern bool WTSRegisterSessionNotification(IntPtr hWnd, int flags);

    [DllImport("wtsapi32.dll")]
    private static extern bool WTSUnRegisterSessionNotification(IntPtr hWnd);
}
