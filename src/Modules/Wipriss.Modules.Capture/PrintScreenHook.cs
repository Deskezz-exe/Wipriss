using System.Runtime.InteropServices;

namespace Wipriss.Modules.Capture;

/// <summary>
/// Takes the PrtSc key before Windows does, so switching it to Wipriss works at once: no Explorer restart, no sign-out.
/// It is a standard low-level keyboard hook (the same mechanism ShareX, PowerToys and AutoHotkey use), with a narrow job:
/// it looks only at the key code, keeps PrtSc for Wipriss and passes every other key on untouched. Nothing is stored or sent.
/// It runs on its own small thread with its own message loop, so a busy Wipriss window can never delay typing.
/// Windows doesn't show keys aimed at programs running as administrator to such hooks; for those, the registered
/// hotkey and the Windows setting take over after the next sign-in.
/// </summary>
internal sealed class PrintScreenHook : IDisposable
{
    private const int WH_KEYBOARD_LL = 13, WM_QUIT = 0x0012;
    private const int WM_KEYDOWN = 0x0100, WM_SYSKEYDOWN = 0x0104;
    private const int VK_SNAPSHOT = 0x2C;

    private readonly Action _pressed;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new();
    private HookProc? _proc; // kept alive while the hook exists
    private IntPtr _hook;
    private uint _threadId;
    private bool _down;

    /// <param name="pressed">Called on the hook thread when PrtSc goes down; must return quickly.</param>
    public PrintScreenHook(Action pressed)
    {
        _pressed = pressed;
        _thread = new Thread(Run) { IsBackground = true, Name = "Wipriss PrtSc" };
        _thread.Start();
        _ready.Wait(TimeSpan.FromSeconds(2));
    }

    /// <summary>False if Windows refused the hook (very rare); the registered hotkey still works then.</summary>
    public bool Active => _hook != IntPtr.Zero;

    public void Dispose()
    {
        if (_threadId != 0) PostThreadMessageW(_threadId, WM_QUIT, 0, 0);
        _thread.Join(TimeSpan.FromSeconds(2));
        _ready.Dispose();
    }

    private void Run()
    {
        _threadId = GetCurrentThreadId();
        _proc = OnKey;
        _hook = SetWindowsHookExW(WH_KEYBOARD_LL, _proc, GetModuleHandleW(null), 0);
        _ready.Set();
        if (_hook == IntPtr.Zero) return;

        while (GetMessageW(out _, IntPtr.Zero, 0, 0) > 0)
        {
            // The hook is called from inside GetMessage; there is nothing else to do here.
        }

        UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }

    /// <summary>The combination to catch. PrtSc alone also takes Alt+PrtSc and the like, as before.</summary>
    public Wipriss.Core.Input.Hotkey Key { get; set; } = Wipriss.Core.Input.Hotkey.PrtSc;

    /// <summary>While the user records a new key in Settings, every key passes through.</summary>
    public volatile bool Paused;

    private IntPtr OnKey(int code, IntPtr message, IntPtr data)
    {
        // Only the virtual-key code is read (the first field of KBDLLHOOKSTRUCT).
        var key = Key;
        // While Settings records a new key, PrtSc must not reach Snipping Tool (and windows never see its key-down):
        // it is kept here and handed to the recorder with the modifiers held.
        if (code >= 0 && Paused && Recorder is { } record && Marshal.ReadInt32(data) == VK_SNAPSHOT)
        {
            if (message == WM_KEYDOWN || message == WM_SYSKEYDOWN) record(CurrentModifiers());
            return 1;
        }
        if (code >= 0 && !Paused && Marshal.ReadInt32(data) == key.Key)
        {
            bool down = message == WM_KEYDOWN || message == WM_SYSKEYDOWN;
            if (down && !_down && !ModifiersMatch(key)) return CallNextHookEx(IntPtr.Zero, code, message, data);
            if (!down && !_down) return CallNextHookEx(IntPtr.Zero, code, message, data);
            if (down && !_down) _pressed(); // once per press, not for auto-repeat
            _down = down;
            return 1; // keep the key from reaching Snipping Tool or the window below
        }

        return CallNextHookEx(IntPtr.Zero, code, message, data);
    }

    /// <summary>Set while recording: gets the modifiers held with PrtSc (called on the hook thread).</summary>
    public volatile Action<int>? Recorder;

    private static bool ModifiersMatch(Wipriss.Core.Input.Hotkey key) =>
        key.Key == VK_SNAPSHOT && key.Modifiers == 0 || CurrentModifiers() == key.Modifiers;

    private static int CurrentModifiers()
    {
        static bool Down(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;
        return (Down(0x11) ? Wipriss.Core.Input.Hotkey.Ctrl : 0) | (Down(0x12) ? Wipriss.Core.Input.Hotkey.Alt : 0)
               | (Down(0x10) ? Wipriss.Core.Input.Hotkey.Shift : 0) | (Down(0x5B) || Down(0x5C) ? Wipriss.Core.Input.Hotkey.Win : 0);
    }

    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vk);

    private delegate IntPtr HookProc(int code, IntPtr message, IntPtr data);

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam, lParam;
        public uint time;
        public int x, y;
    }

    [DllImport("user32.dll")] private static extern IntPtr SetWindowsHookExW(int id, HookProc proc, IntPtr module, uint threadId);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
    [DllImport("user32.dll")] private static extern int GetMessageW(out Msg msg, IntPtr hwnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool PostThreadMessageW(uint threadId, uint message, nint wParam, nint lParam);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandleW(string? name);
}
