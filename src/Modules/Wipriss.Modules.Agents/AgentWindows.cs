using System.Runtime.InteropServices;
using System.Text;

namespace Wipriss.Modules.Agents;

/// <summary>
/// Brings an agent's window to the front when its block on the home screen is clicked. A terminal agent
/// (claude.exe, codex.exe) has no window of its own: we walk up its parents (shell → terminal) to the first one
/// with a window. Claude Desktop is found the same way (its own claude.exe has the window). A window hidden in the
/// tray or minimised is restored. Windows lets us take the foreground because the click on the island was the last input.
/// Only process names and window handles are read; nothing else.
/// </summary>
internal static class AgentWindows
{
    private const int MaxDepth = 8;

    /// <summary>Returns false when no window was found (the agent is not running).</summary>
    public static bool Focus(string agent)
    {
        try
        {
            var processes = Snapshot();
            string[] names = agent == "claude" ? ["claude.exe"] : ["codex.exe", "codex-x86_64-pc-windows-msvc.exe"];
            var self = Environment.ProcessId;

            // Newest first: the session the user touched last is the likeliest one they want.
            foreach (var start in processes.Values
                         .Where(p => names.Any(n => p.Name.Equals(n, StringComparison.OrdinalIgnoreCase)))
                         .OrderByDescending(p => p.Id))
            {
                var p = start;
                for (int depth = 0; depth < MaxDepth && p is not null && p.Id != self; depth++)
                {
                    if (WindowOf(p.Id) is var hwnd && hwnd != IntPtr.Zero)
                    {
                        Show(hwnd);
                        return true;
                    }
                    // The old console (not Windows Terminal): the window belongs to conhost.exe, a child of the shell.
                    foreach (var host in processes.Values.Where(c => c.Parent == p.Id && c.Name.Equals("conhost.exe", StringComparison.OrdinalIgnoreCase)))
                        if (WindowOf(host.Id, visibleOnly: true) is var console && console != IntPtr.Zero)
                        {
                            Show(console);
                            return true;
                        }
                    if (p.Parent == 0 || p.Parent == p.Id || !processes.TryGetValue(p.Parent, out var parent)) break;
                    // explorer.exe started it: going further would open the desktop.
                    if (parent.Name.Equals("explorer.exe", StringComparison.OrdinalIgnoreCase)) break;
                    p = parent;
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { }
        return false;
    }

    private static void Show(IntPtr hwnd)
    {
        if (IsIconic(hwnd)) ShowWindow(hwnd, SW_RESTORE);
        else if (!IsWindowVisible(hwnd)) ShowWindow(hwnd, SW_SHOW);
        SetForegroundWindow(hwnd);
    }

    /// <summary>A top-level, titled, unowned window of the process: visible ones first, then hidden (tray) ones.</summary>
    private static IntPtr WindowOf(int pid, bool visibleOnly = false)
    {
        IntPtr visible = IntPtr.Zero, hidden = IntPtr.Zero;
        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out uint owner);
            if (owner != pid || GetWindow(hwnd, GW_OWNER) != IntPtr.Zero || GetWindowTextLengthW(hwnd) == 0) return true;
            long ex = GetWindowLongPtrW(hwnd, GWL_EXSTYLE);
            if ((ex & WS_EX_TOOLWINDOW) != 0) return true;
            if (IsWindowVisible(hwnd)) { visible = hwnd; return false; }
            if (!visibleOnly && hidden == IntPtr.Zero && Title(hwnd).Length > 1) hidden = hwnd;
            return true;
        }, IntPtr.Zero);
        return visible != IntPtr.Zero ? visible : hidden;
    }

    private static string Title(IntPtr hwnd)
    {
        var sb = new StringBuilder(64);
        GetWindowTextW(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    private sealed record Proc(int Id, int Parent, string Name);

    private static Dictionary<int, Proc> Snapshot()
    {
        var result = new Dictionary<int, Proc>();
        var snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snap == INVALID_HANDLE) return result;
        try
        {
            var entry = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>() };
            if (!Process32FirstW(snap, ref entry)) return result;
            do result[(int)entry.th32ProcessID] = new Proc((int)entry.th32ProcessID, (int)entry.th32ParentProcessID, entry.szExeFile);
            while (Process32NextW(snap, ref entry));
        }
        finally { CloseHandle(snap); }
        return result;
    }

    private const uint TH32CS_SNAPPROCESS = 0x2;
    private static readonly IntPtr INVALID_HANDLE = new(-1);
    private const int SW_RESTORE = 9, SW_SHOW = 5, GW_OWNER = 4, GWL_EXSTYLE = -20;
    private const long WS_EX_TOOLWINDOW = 0x80;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32W
    {
        public uint dwSize, cntUsage, th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID, cntThreads, th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
    }

    private delegate bool EnumProc(IntPtr hwnd, IntPtr param);

    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool Process32FirstW(IntPtr snap, ref PROCESSENTRY32W entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool Process32NextW(IntPtr snap, ref PROCESSENTRY32W entry);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc proc, IntPtr param);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hwnd, int cmd);
    [DllImport("user32.dll")] private static extern int GetWindowTextLengthW(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextW(IntPtr hwnd, StringBuilder text, int max);
    [DllImport("user32.dll")] private static extern long GetWindowLongPtrW(IntPtr hwnd, int index);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int cmd);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
}
