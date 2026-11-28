using System.Runtime.InteropServices;

namespace Wipriss.Modules.Load;

/// <summary>One reading of all figures. NaN = not known (the first reading, or no GPU counters).</summary>
internal readonly record struct LoadReading(double Cpu, double Gpu, double Ram, double Down, double Up);

/// <summary>
/// Reads the figures straight from Windows, without admin rights: CPU from GetSystemTimes, RAM from
/// GlobalMemoryStatusEx, network from GetIfTable2 (physical adapters that are up), GPU from the
/// "GPU Engine" performance counters (the ones Task Manager uses). No temperatures: those need admin rights
/// and a kernel driver. Not thread-safe: one timer calls it.
/// </summary>
internal sealed class LoadSampler : IDisposable
{
    private ulong _idle, _kernel, _user;
    private ulong _in, _out;
    private long _netTicks;
    private bool _haveCpu, _haveNet;
    private IntPtr _query, _gpuCounter;
    private bool _gpuFailed;
    private byte[] _gpuBuffer = new byte[64 * 1024];

    public bool HasGpu => !_gpuFailed;

    /// <summary>Forget the previous reading (after a pause the next figures would be an average over it).</summary>
    public void Reset()
    {
        _haveCpu = _haveNet = false;
    }

    public LoadReading Read()
    {
        return new LoadReading(ReadCpu(), ReadGpu(), ReadRam(), ReadNet(out double up), up);
    }

    private double ReadCpu()
    {
        if (!GetSystemTimes(out ulong idle, out ulong kernel, out ulong user)) return double.NaN;
        double value = _haveCpu ? LoadMath.CpuPercent(_idle, _kernel, _user, idle, kernel, user) : double.NaN;
        (_idle, _kernel, _user, _haveCpu) = (idle, kernel, user, true);
        return value;
    }

    private static double ReadRam()
    {
        var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        return GlobalMemoryStatusEx(ref status) ? status.dwMemoryLoad : double.NaN;
    }

    // ---- Network: MIB_IF_TABLE2 of MIB_IF_ROW2 (x64 layout, 1352 bytes a row) ----

    private const int RowSize = 1352, RowsOffset = 8;
    private const int TypeOffset = 1128, FlagsOffset = 1152, OperStatusOffset = 1156, InOctetsOffset = 1208, OutOctetsOffset = 1280;
    private const int IfTypeLoopback = 24, IfOperStatusUp = 1;
    private const byte FlagHardware = 0x01, FlagFilter = 0x02;

    private double ReadNet(out double up)
    {
        up = double.NaN;
        if (GetIfTable2(out IntPtr table) != 0 || table == IntPtr.Zero) return double.NaN;
        ulong totalIn = 0, totalOut = 0;
        try
        {
            int count = Marshal.ReadInt32(table);
            for (int i = 0; i < count; i++)
            {
                IntPtr row = table + RowsOffset + i * RowSize;
                byte flags = Marshal.ReadByte(row, FlagsOffset);
                // Real adapters only: virtual ones and filter layers repeat the same bytes.
                if ((flags & FlagHardware) == 0 || (flags & FlagFilter) != 0) continue;
                if (Marshal.ReadInt32(row, TypeOffset) == IfTypeLoopback) continue;
                if (Marshal.ReadInt32(row, OperStatusOffset) != IfOperStatusUp) continue;
                totalIn += (ulong)Marshal.ReadInt64(row, InOctetsOffset);
                totalOut += (ulong)Marshal.ReadInt64(row, OutOctetsOffset);
            }
        }
        finally { FreeMibTable(table); }

        long now = Environment.TickCount64;
        double down = double.NaN;
        // An adapter that went away makes the sum smaller: start again rather than show nonsense.
        if (_haveNet && now > _netTicks && totalIn >= _in && totalOut >= _out)
        {
            double seconds = (now - _netTicks) / 1000.0;
            down = (totalIn - _in) / seconds;
            up = (totalOut - _out) / seconds;
        }
        (_in, _out, _netTicks, _haveNet) = (totalIn, totalOut, now, true);
        return down;
    }

    // ---- GPU: \GPU Engine(*)\Utilization Percentage ----

    private const uint PDH_FMT_DOUBLE = 0x00000200, PDH_FMT_NOCAP100 = 0x00008000;
    private const uint PDH_MORE_DATA = 0x800007D2;

    private double ReadGpu()
    {
        if (_gpuFailed) return double.NaN;
        if (_query == IntPtr.Zero)
        {
            if (PdhOpenQuery(null, IntPtr.Zero, out _query) != 0
                || PdhAddEnglishCounter(_query, @"\GPU Engine(*)\Utilization Percentage", IntPtr.Zero, out _gpuCounter) != 0)
            {
                _gpuFailed = true; // older Windows, or counters switched off
                CloseGpu();
                return double.NaN;
            }
            PdhCollectQueryData(_query);
            return double.NaN; // a rate needs two readings
        }
        if (PdhCollectQueryData(_query) != 0) return double.NaN;

        uint size = (uint)_gpuBuffer.Length;
        uint status = Format(ref size, out uint count);
        if (status == PDH_MORE_DATA)
        {
            _gpuBuffer = new byte[size + 4096];
            size = (uint)_gpuBuffer.Length;
            status = Format(ref size, out count);
        }
        if (status != 0) return double.NaN;

        var items = new List<(string, double)>((int)count);
        unsafe
        {
            fixed (byte* p = _gpuBuffer)
            {
                // PDH_FMT_COUNTERVALUE_ITEM_W: name pointer, then { CStatus, (pad), double }.
                for (int i = 0; i < count; i++)
                {
                    byte* item = p + i * 24;
                    var name = Marshal.PtrToStringUni(*(IntPtr*)item) ?? "";
                    uint cstatus = *(uint*)(item + 8);
                    double value = *(double*)(item + 16);
                    if (cstatus <= 1) items.Add((name, value)); // valid or new data
                }
            }
        }
        return LoadMath.GpuPercent(items);
    }

    private uint Format(ref uint size, out uint count)
    {
        unsafe
        {
            fixed (byte* p = _gpuBuffer)
                return PdhGetFormattedCounterArray(_gpuCounter, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, ref size, out count, (IntPtr)p);
        }
    }

    private void CloseGpu()
    {
        if (_query != IntPtr.Zero) PdhCloseQuery(_query);
        _query = _gpuCounter = IntPtr.Zero;
    }

    /// <summary>Closes the counters; they open again on the next reading (they cost a little memory while open).</summary>
    public void Dispose()
    {
        CloseGpu();
        Reset();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength, dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out ulong idle, out ulong kernel, out ulong user);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);

    [DllImport("iphlpapi.dll")]
    private static extern int GetIfTable2(out IntPtr table);

    [DllImport("iphlpapi.dll")]
    private static extern void FreeMibTable(IntPtr memory);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhOpenQuery(string? dataSource, IntPtr userData, out IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhAddEnglishCounter(IntPtr query, string path, IntPtr userData, out IntPtr counter);

    [DllImport("pdh.dll")]
    private static extern uint PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhGetFormattedCounterArray(IntPtr counter, uint format, ref uint bufferSize, out uint itemCount, IntPtr buffer);

    [DllImport("pdh.dll")]
    private static extern uint PdhCloseQuery(IntPtr query);
}
