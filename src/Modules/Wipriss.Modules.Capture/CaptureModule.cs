using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using Wipriss.Core;
using Wipriss.Core.Modules;

namespace Wipriss.Modules.Capture;

/// <summary>
/// Screenshots on PrtSc. The key is a system hotkey (it works even over programs running as administrator),
/// so nothing runs while idle. A screenshot lives only in memory and on the clipboard until you press "Сохранить".
/// </summary>
public sealed class CaptureModule : IModule
{
    private const int ShotKey = 1, StopKey = 2;

    private HwndSource? _hotkeys;
    private IModuleHost? _host;
    private ShotToast? _toast;
    private Action? _onStopKey;

    public ModuleInfo Info { get; } = ModuleCatalog.Get("capture");

    public Size PanelSize => new(440, 470);

    /// <summary>True when PrtSc is ours. False if another program already took the key.</summary>
    internal bool KeyRegistered { get; private set; }

    internal bool IsCapturing { get; private set; }

    public string Summary => !KeyRegistered ? "PrtSc занят" : PrintScreenSetting.SnippingToolTakesKey ? "нужна настройка" : "по PrtSc";

    public void Start(IModuleHost host)
    {
        _host = host;
        _hotkeys = new HwndSource(new HwndSourceParameters("Wipriss.Capture") { ParentWindow = new IntPtr(-3), WindowStyle = 0 });
        _hotkeys.AddHook(OnMessage);
        KeyRegistered = Native.RegisterHotKey(_hotkeys.Handle, ShotKey, Native.MOD_NOREPEAT, Native.VK_SNAPSHOT);
    }

    public void Stop()
    {
        _toast?.Close();
        if (_hotkeys is not null)
        {
            Native.UnregisterHotKey(_hotkeys.Handle, ShotKey);
            Native.UnregisterHotKey(_hotkeys.Handle, StopKey);
            _hotkeys.Dispose();
            _hotkeys = null;
        }

        KeyRegistered = false;
    }

    public FrameworkElement CreatePanel(IPanelContext panel) => new CapturePanel(this, panel);

    /// <summary>Freezes the screen and shows the choice: area, whole screen or scrolling.</summary>
    internal void TakeScreenshot()
    {
        if (IsCapturing) return;
        IsCapturing = true;
        try
        {
            new CaptureSession(this).Start();
        }
        catch (Exception ex)
        {
            ErrorLog.Write(ex);
            IsCapturing = false;
        }
    }

    internal void SessionEnded()
    {
        IsCapturing = false;
        _host?.TrimMemory(); // the frozen screen copies are no longer needed
    }

    /// <summary>The finished screenshot: on the clipboard already; the toast offers the editor.</summary>
    internal void Deliver(BitmapSource image, bool onClipboard)
    {
        _toast?.Close();
        _toast = new ShotToast(image, onClipboard);
        _toast.EditRequested += () =>
        {
            var editor = new EditorWindow(image);
            editor.Closed += (_, _) =>
            {
                _host?.TrimMemory(); // the picture and its undo history are gone: give the memory back
            };
            editor.Show();
            // The click landed on a window that never takes focus, so bring the editor forward explicitly.
            Native.BringToFront(editor);
            editor.Activate();
        };
        _toast.Closed += (_, _) =>
        {
            _toast = null;
            _host?.TrimMemory();
        };
        _toast.Show();
    }

    /// <summary>Esc works as a system key while a scrolling capture runs (the page has focus, not Wipriss).</summary>
    internal void WatchStopKey(Action? onStop)
    {
        if (_hotkeys is null) return;
        _onStopKey = onStop;
        if (onStop is null) Native.UnregisterHotKey(_hotkeys.Handle, StopKey);
        else Native.RegisterHotKey(_hotkeys.Handle, StopKey, Native.MOD_NOREPEAT, Native.VK_ESCAPE);
    }

    /// <summary>Tries again after the user changed the Windows setting or another program let the key go.</summary>
    internal void RetryKey()
    {
        if (_hotkeys is null || KeyRegistered) return;
        KeyRegistered = Native.RegisterHotKey(_hotkeys.Handle, ShotKey, Native.MOD_NOREPEAT, Native.VK_SNAPSHOT);
    }

    private IntPtr OnMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != Native.WM_HOTKEY) return IntPtr.Zero;
        handled = true;
        if (wParam == ShotKey) TakeScreenshot();
        else if (wParam == StopKey) _onStopKey?.Invoke();
        return IntPtr.Zero;
    }
}
