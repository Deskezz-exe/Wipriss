using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Wipriss.ProcessTools;

/// <summary>Details about one process that the snapshot doesn't include. Read-only access only.</summary>
public static class ProcessQuery
{
    internal const int PROCESS_TERMINATE = 0x0001;
    internal const int PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const int ProcessCommandLineInformation = 60;
    private const int StatusInfoLengthMismatch = unchecked((int)0xC0000004);
    private const int StatusBufferTooSmall = unchecked((int)0xC0000023);
    private const int StatusBufferOverflow = unchecked((int)0x80000005);

    /// <summary>The Windows session of this app: the signed-in user's desktop.</summary>
    public static int CurrentSessionId { get; } = SessionOf(Environment.ProcessId);

    public static int SessionOf(int pid) => ProcessIdToSessionId((uint)pid, out uint session) ? (int)session : -1;

    /// <summary>Full path of the program file, or null when Windows won't say (protected processes).</summary>
    public static string? ImagePath(int pid)
    {
        using var handle = Open(pid, PROCESS_QUERY_LIMITED_INFORMATION);
        if (handle.IsInvalid) return null;

        var chars = new char[1024];
        int length = chars.Length;
        return QueryFullProcessImageNameW(handle, 0, chars, ref length) ? new string(chars, 0, length) : null;
    }

    /// <summary>The command line the process was started with, or null if it can't be read.</summary>
    public static string? CommandLine(int pid)
    {
        using var handle = Open(pid, PROCESS_QUERY_LIMITED_INFORMATION);
        if (handle.IsInvalid) return null;

        int size = 2048;
        for (int attempt = 0; attempt < 4; attempt++)
        {
            IntPtr buffer = Marshal.AllocHGlobal(size);
            try
            {
                int status = NtQueryInformationProcess(handle, ProcessCommandLineInformation, buffer, size, out int needed);
                if (status is StatusInfoLengthMismatch or StatusBufferTooSmall or StatusBufferOverflow)
                {
                    size = Math.Max(size * 2, needed);
                    continue;
                }

                if (status < 0) return null;
                // The buffer starts with a UNICODE_STRING that points into the same buffer.
                var text = Marshal.PtrToStructure<UnicodeString>(buffer);
                return text.Buffer == IntPtr.Zero ? "" : Marshal.PtrToStringUni(text.Buffer, text.Length / 2);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        return null;
    }

    internal static SafeProcessHandle Open(int pid, int access) => OpenProcess(access, false, pid);

    /// <summary>True if the handle still points at the process that started at this time.</summary>
    internal static bool StartedAt(SafeProcessHandle handle, long createTime) =>
        GetProcessTimes(handle, out long created, out _, out _, out _) && created == createTime;

    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(int access, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageNameW(SafeProcessHandle process, int flags, char[] exeName, ref int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessTimes(SafeProcessHandle process, out long creation, out long exit, out long kernel, out long user);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ProcessIdToSessionId(uint processId, out uint sessionId);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(SafeProcessHandle process, int infoClass, IntPtr info, int length, out int returnLength);
}
