using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using Microsoft.Win32;
using Wipriss.App.Interop;

namespace Wipriss.App.Shell;

/// <summary>
/// Notification-area icon built on Shell_NotifyIcon directly (no WinForms, so less memory).
/// Follows the taskbar theme and survives an Explorer restart.
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private const int CallbackMessage = Native.WM_APP + 1;
    private const int IconId = 1;

    private readonly HwndSource _window;
    private readonly uint _taskbarCreated;
    private readonly string _tooltip;
    private IntPtr _icon;
    private bool _disposed;

    public TrayIcon(string tooltip)
    {
        _tooltip = tooltip;

        // A hidden top-level window: it receives broadcasts (theme change, Explorer restart).
        var parameters = new HwndSourceParameters("Wipriss.Tray")
        {
            WindowStyle = Native.WS_POPUP,
            ExtendedWindowStyle = Native.WS_EX_TOOLWINDOW,
            Width = 0,
            Height = 0,
        };
        _window = new HwndSource(parameters);
        _window.AddHook(WndProc);

        _taskbarCreated = Native.RegisterWindowMessage("TaskbarCreated");
        _icon = LoadIconForTaskbarTheme();
        Send(Native.NIM_ADD);
    }

    public event Action? LeftClick;
    public event Action? MenuRequested;

    /// <summary>Shows a native menu at the cursor and returns the chosen id (0 = nothing).</summary>
    public int ShowMenu(IEnumerable<(int Id, string? Text)> items)
    {
        IntPtr menu = Native.CreatePopupMenu();
        try
        {
            foreach (var (id, text) in items)
            {
                if (text is null) Native.AppendMenu(menu, Native.MF_SEPARATOR, 0, null);
                else Native.AppendMenu(menu, Native.MF_STRING, (nuint)id, text);
            }

            Native.GetCursorPos(out var point);
            Native.SetForegroundWindow(_window.Handle); // so the menu closes when clicking elsewhere
            int chosen = Native.TrackPopupMenuEx(menu,
                Native.TPM_RETURNCMD | Native.TPM_RIGHTBUTTON | Native.TPM_NONOTIFY,
                point.X, point.Y, _window.Handle, IntPtr.Zero);
            Native.PostMessage(_window.Handle, Native.WM_NULL, IntPtr.Zero, IntPtr.Zero);
            return chosen;
        }
        finally
        {
            Native.DestroyMenu(menu);
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == CallbackMessage)
        {
            int mouse = (int)(lParam.ToInt64() & 0xFFFF);
            if (mouse == Native.WM_LBUTTONUP) LeftClick?.Invoke();
            else if (mouse == Native.WM_RBUTTONUP) MenuRequested?.Invoke();
            handled = true;
        }
        else if (msg == (int)_taskbarCreated)
        {
            Send(Native.NIM_ADD); // Explorer restarted: the icon must be added again
        }
        else if (msg == Native.WM_SETTINGCHANGE && lParam != IntPtr.Zero
                 && Marshal.PtrToStringUni(lParam) == "ImmersiveColorSet")
        {
            var old = _icon;
            _icon = LoadIconForTaskbarTheme();
            Send(Native.NIM_MODIFY);
            if (old != IntPtr.Zero) Native.DestroyIcon(old);
        }

        return IntPtr.Zero;
    }

    private void Send(int message)
    {
        var data = new Native.NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<Native.NOTIFYICONDATA>(),
            hWnd = _window.Handle,
            uID = IconId,
            uFlags = Native.NIF_MESSAGE | Native.NIF_ICON | Native.NIF_TIP,
            uCallbackMessage = CallbackMessage,
            hIcon = _icon,
            szTip = _tooltip,
            szInfo = string.Empty,
            szInfoTitle = string.Empty,
        };
        Native.Shell_NotifyIcon(message, ref data);
    }

    private static IntPtr LoadIconForTaskbarTheme()
    {
        bool lightTaskbar = Registry.CurrentUser
            .OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize")
            ?.GetValue("SystemUsesLightTheme") is int v && v == 1;

        string file = Path.Combine(AppContext.BaseDirectory, "Assets", lightTaskbar ? "tray-dark.ico" : "tray-light.ico");
        int cx = Native.GetSystemMetrics(Native.SM_CXSMICON);
        int cy = Native.GetSystemMetrics(Native.SM_CYSMICON);
        return Native.LoadImage(IntPtr.Zero, file, Native.IMAGE_ICON, cx, cy, Native.LR_LOADFROMFILE);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Send(Native.NIM_DELETE);
        if (_icon != IntPtr.Zero) Native.DestroyIcon(_icon);
        _window.Dispose();
    }
}
