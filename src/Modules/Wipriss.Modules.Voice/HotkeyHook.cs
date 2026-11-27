using System.Runtime.InteropServices;

namespace Wipriss.Modules.Voice;

/// <summary>
/// The decisions of the dictation hotkey, without Windows: which key press fires, which one is kept from
/// the program in front, and how a new hotkey is captured. Called on the hook thread only.
/// </summary>
internal sealed class HotkeyMatcher(Func<int, bool> stillDown)
{
    private readonly HashSet<int> _down = [];
    private readonly HashSet<int> _swallowUp = [];
    private readonly List<int> _captured = [];

    public enum Verdict { Pass, Swallow }

    public IReadOnlyList<int> Keys { get; set; } = HotkeyKeys.Default;

    /// <summary>The microphone is open: Esc cancels.</summary>
    public bool Listening { get; set; }

    public bool Capturing { get; private set; }

    public event Action? Pressed;
    public event Action? Cancelled;

    /// <summary>The captured keys, or null when Esc cancelled the capture.</summary>
    public event Action<List<int>?>? Captured;

    public void BeginCapture()
    {
        _captured.Clear();
        Capturing = true;
    }

    public void CancelCapture() => Capturing = false;

    public bool AltOrWinDown => _down.Any(k => HotkeyKeys.Generic(k) is HotkeyKeys.Alt or HotkeyKeys.Win);

    public Verdict Down(int vk)
    {
        // A key-up can be lost (Ctrl+Alt+Del, the lock screen): forget keys that are not really held.
        _down.RemoveWhere(k => k != vk && !stillDown(k));
        bool repeat = !_down.Add(vk);

        if (Capturing)
        {
            if (vk == HotkeyKeys.Escape)
            {
                Capturing = false;
                _swallowUp.Add(vk);
                Captured?.Invoke(null);
            }
            else if (!repeat && _captured.Count < 3 && !_captured.Contains(vk))
            {
                _captured.Add(vk);
            }
            return Verdict.Swallow; // while choosing a key nothing reaches other programs
        }

        if (vk == HotkeyKeys.Escape && Listening)
        {
            if (!repeat) Cancelled?.Invoke();
            _swallowUp.Add(vk);
            return Verdict.Swallow;
        }

        if (!Keys.Any(k => HotkeyKeys.Matches(k, vk))) return Verdict.Pass;
        if (repeat) return _swallowUp.Contains(vk) ? Verdict.Swallow : Verdict.Pass;

        bool complete = Keys.All(k => _down.Any(d => HotkeyKeys.Matches(k, d)))
                        && _down.All(d => Keys.Any(k => HotkeyKeys.Matches(k, d)));
        if (!complete) return Verdict.Pass;

        Pressed?.Invoke();
        // The last key of a combination (Space in Ctrl+Alt+Space) must not type into the program.
        // Modifiers of a combination pass, so Ctrl and Alt are never left "stuck".
        if (Keys.Count == 1 || !HotkeyKeys.IsModifier(vk))
        {
            _swallowUp.Add(vk);
            return Verdict.Swallow;
        }
        return Verdict.Pass;
    }

    /// <returns>True when the key-up must be kept from other programs.</returns>
    public bool Up(int vk)
    {
        _down.Remove(vk);
        if (Capturing)
        {
            if (_down.Count == 0 && _captured.Count > 0)
            {
                Capturing = false;
                Captured?.Invoke(HotkeyKeys.FromCaptured(_captured));
            }
            return true;
        }
        return _swallowUp.Remove(vk);
    }
}

/// <summary>
/// A low-level keyboard hook (like the PrtSc one) for the dictation hotkey. It reads only the key code of
/// each press, keeps nothing and sends nothing; keys that are not the hotkey pass untouched. It lives on its
/// own thread with its own message loop, so a busy Wipriss can never delay typing. Exists only while dictation
/// is switched on and the model is downloaded.
/// </summary>
internal sealed class HotkeyHook : IDisposable
{
    private const int WH_KEYBOARD_LL = 13, WM_QUIT = 0x0012;
    private const int WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101, WM_SYSKEYDOWN = 0x0104, WM_SYSKEYUP = 0x0105;
    private const uint LLKHF_INJECTED = 0x10;
    /// <summary>An unassigned key: pressing it while Alt or Win is held stops the menu bar or Start from opening.</summary>
    private const ushort MaskKey = 0xE8;

    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new();
    private HookProc? _proc;
    private IntPtr _hook;
    private uint _threadId;

    public HotkeyHook()
    {
        Matcher = new HotkeyMatcher(vk => (GetAsyncKeyState(vk) & 0x8000) != 0);
        _thread = new Thread(Run) { IsBackground = true, Name = "Wipriss voice key" };
        _thread.Start();
        _ready.Wait(TimeSpan.FromSeconds(2));
    }

    /// <summary>Touch only from the hook thread, or before/after it runs; its events fire on the hook thread.</summary>
    public HotkeyMatcher Matcher { get; }

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
        while (GetMessageW(out _, IntPtr.Zero, 0, 0) > 0) { }
        UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }

    private IntPtr OnKey(int code, IntPtr message, IntPtr data)
    {
        if (code >= 0)
        {
            // Only the key code and the "injected" flag are read (KBDLLHOOKSTRUCT: vkCode, scanCode, flags).
            int vk = Marshal.ReadInt32(data);
            uint flags = (uint)Marshal.ReadInt32(data, 8);
            if ((flags & LLKHF_INJECTED) == 0) // our own Ctrl+V and other programs' typing pass untouched
            {
                int msg = (int)message;
                if (msg is WM_KEYDOWN or WM_SYSKEYDOWN)
                {
                    bool capturing = Matcher.Capturing;
                    if (Matcher.Down(vk) == HotkeyMatcher.Verdict.Swallow)
                    {
                        if (!capturing && Matcher.AltOrWinDown) PressMaskKey();
                        return 1;
                    }
                }
                else if (msg is WM_KEYUP or WM_SYSKEYUP && Matcher.Up(vk))
                {
                    return 1;
                }
            }
        }
        return CallNextHookEx(IntPtr.Zero, code, message, data);
    }

    private static void PressMaskKey()
    {
        var inputs = new Input[2];
        inputs[0] = new Input { Type = 1, Key = new KeyInput { Vk = MaskKey } };
        inputs[1] = new Input { Type = 1, Key = new KeyInput { Vk = MaskKey, Flags = 2 } }; // key up
        SendInput(2, inputs, Marshal.SizeOf<Input>());
    }

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

    [StructLayout(LayoutKind.Sequential)]
    internal struct KeyInput
    {
        public ushort Vk;
        public ushort Scan;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    /// <summary>INPUT with the keyboard member; padded to the size of the biggest member (MOUSEINPUT).</summary>
    [StructLayout(LayoutKind.Explicit, Size = 40)]
    internal struct Input
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(8)] public KeyInput Key;
    }

    [DllImport("user32.dll")] internal static extern uint SendInput(uint count, Input[] inputs, int size);
    [DllImport("user32.dll")] internal static extern short GetAsyncKeyState(int vk);
    [DllImport("user32.dll")] private static extern IntPtr SetWindowsHookExW(int id, HookProc proc, IntPtr module, uint threadId);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
    [DllImport("user32.dll")] private static extern int GetMessageW(out Msg msg, IntPtr hwnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool PostThreadMessageW(uint threadId, uint message, nint wParam, nint lParam);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandleW(string? name);
}
