using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Interop;
using Wipriss.App.Interop;

namespace Wipriss.App.Shell;

/// <summary>
/// Tells when a full-screen app (game, video) opens or closes. Windows pushes the notification
/// to registered app bars, so nothing is polled. No screen space is reserved.
/// </summary>
internal sealed class FullscreenWatcher : IDisposable
{
    private readonly HwndSource _source;
    private readonly uint _callbackMessage;
    private bool _disposed;

    public FullscreenWatcher(HwndSource source)
    {
        _source = source;
        _callbackMessage = Native.RegisterWindowMessage("Wipriss.FullscreenNotify");
        var data = NewData();
        Native.SHAppBarMessage(Native.ABM_NEW, ref data);
        _source.AddHook(WndProc);
    }

    /// <summary>true when a full-screen app is in front.</summary>
    public event Action<bool>? Changed;

    private Native.APPBARDATA NewData() => new()
    {
        cbSize = Marshal.SizeOf<Native.APPBARDATA>(),
        hWnd = _source.Handle,
        uCallbackMessage = _callbackMessage,
    };

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == (int)_callbackMessage && wParam.ToInt64() == Native.ABN_FULLSCREENAPP)
        {
            bool fullscreen = lParam != IntPtr.Zero && !IsDesktopInFront();
            Changed?.Invoke(fullscreen);
        }

        return IntPtr.Zero;
    }

    // The desktop itself is sometimes reported as "full screen"; ignore it.
    private static bool IsDesktopInFront()
    {
        var name = new StringBuilder(64);
        Native.GetClassName(Native.GetForegroundWindow(), name, name.Capacity);
        var cls = name.ToString();
        return cls is "Progman" or "WorkerW";
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _source.RemoveHook(WndProc);
        var data = NewData();
        Native.SHAppBarMessage(Native.ABM_REMOVE, ref data);
    }
}
