using System.Runtime.InteropServices;

namespace Wipriss.ProcessTools;

/// <summary>One process at the moment of the snapshot.</summary>
/// <param name="Name">Image name as Windows reports it, for example "chrome.exe".</param>
/// <param name="CreateTime">Start time as a FILETIME (UTC). Together with the PID it identifies the process even after its PID is reused.</param>
/// <param name="CpuTime">Kernel plus user time so far, in 100 ns units.</param>
/// <param name="PrivateBytes">Private working set: the "Memory" column of Task Manager.</param>
public sealed record ProcessEntry(int Pid, int ParentPid, string Name, int SessionId, long CreateTime, long CpuTime, long PrivateBytes)
{
    public DateTime StartedUtc => CreateTime > 0 ? DateTime.FromFileTimeUtc(CreateTime) : DateTime.MinValue;

    public ProcessRef Ref => new(Pid, CreateTime, Name);
}

/// <summary>A process to act on: its PID plus its start time, so a reused PID never hits a different process.</summary>
public readonly record struct ProcessRef(int Pid, long CreateTime, string Name);

public static class ProcessSnapshot
{
    private const int SystemProcessInformationClass = 5;
    private const int StatusInfoLengthMismatch = unchecked((int)0xC0000004);

    /// <summary>All processes in one system call, the same source Task Manager uses. Takes a few milliseconds.</summary>
    public static List<ProcessEntry> Take()
    {
        int size = 512 * 1024;
        for (int attempt = 0; attempt < 6; attempt++)
        {
            IntPtr buffer = Marshal.AllocHGlobal(size);
            try
            {
                int status = NtQuerySystemInformation(SystemProcessInformationClass, buffer, size, out int needed);
                if (status == StatusInfoLengthMismatch)
                {
                    size = Math.Max(size * 2, needed + 64 * 1024);
                    continue;
                }

                if (status < 0) throw new InvalidOperationException($"Reading the process list failed (0x{status:X8}).");
                return Parse(buffer);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        throw new InvalidOperationException("The process list kept growing while it was read.");
    }

    private static List<ProcessEntry> Parse(IntPtr buffer)
    {
        var list = new List<ProcessEntry>(400);
        IntPtr entry = buffer;
        while (true)
        {
            var info = Marshal.PtrToStructure<SystemProcessInformation>(entry);
            int pid = (int)info.UniqueProcessId;
            string name = info.ImageNameBuffer != IntPtr.Zero && info.ImageNameLength > 0
                ? Marshal.PtrToStringUni(info.ImageNameBuffer, info.ImageNameLength / 2)
                : pid == 0 ? "Idle" : "System";

            list.Add(new ProcessEntry(
                pid,
                (int)info.InheritedFromUniqueProcessId,
                name,
                (int)info.SessionId,
                info.CreateTime,
                info.KernelTime + info.UserTime,
                info.WorkingSetPrivateSize));

            if (info.NextEntryOffset == 0) break;
            entry += (int)info.NextEntryOffset;
        }

        return list;
    }

    // The documented head of SYSTEM_PROCESS_INFORMATION (stable since Windows Vista). Only the fields we read.
    [StructLayout(LayoutKind.Sequential)]
    private struct SystemProcessInformation
    {
        public uint NextEntryOffset;
        public uint NumberOfThreads;
        public long WorkingSetPrivateSize;
        public uint HardFaultCount;
        public uint NumberOfThreadsHighWatermark;
        public ulong CycleTime;
        public long CreateTime;
        public long UserTime;
        public long KernelTime;
        public ushort ImageNameLength;
        public ushort ImageNameMaximumLength;
        public IntPtr ImageNameBuffer;
        public int BasePriority;
        public IntPtr UniqueProcessId;
        public IntPtr InheritedFromUniqueProcessId;
        public uint HandleCount;
        public uint SessionId;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQuerySystemInformation(int infoClass, IntPtr buffer, int length, out int returnLength);
}
