using System.Net;
using System.Runtime.InteropServices;

namespace Wipriss.ProcessTools;

public enum PortProtocol { Tcp, Udp }

/// <summary>A port a process holds open.</summary>
public sealed record PortEntry(PortProtocol Protocol, int Port, IPAddress Address, int Pid)
{
    public bool IsLoopback => IPAddress.IsLoopback(Address);

    public bool IsAnyAddress => Address.Equals(IPAddress.Any) || Address.Equals(IPAddress.IPv6Any);
}

/// <summary>Who holds which port, straight from the Windows IP helper. Read-only, takes about a millisecond.</summary>
public static class PortTable
{
    private const int AF_INET = 2, AF_INET6 = 23;
    private const int TCP_TABLE_OWNER_PID_LISTENER = 3;
    private const int UDP_TABLE_OWNER_PID = 1;
    private const int ERROR_INSUFFICIENT_BUFFER = 122;

    /// <summary>TCP ports waiting for connections (servers), IPv4 and IPv6.</summary>
    public static List<PortEntry> TcpListeners()
    {
        var list = new List<PortEntry>();
        // MIB_TCPROW_OWNER_PID: state, local addr, local port, remote addr, remote port, pid (6 DWORDs)
        Read(AF_INET, tcp: true, TCP_TABLE_OWNER_PID_LISTENER, rowSize: 24, (row) =>
            list.Add(new PortEntry(PortProtocol.Tcp, PortAt(row, 8), Ipv4At(row, 4), Marshal.ReadInt32(row, 20))));
        // MIB_TCP6ROW_OWNER_PID: local addr[16], scope, port, remote addr[16], scope, port, state, pid
        Read(AF_INET6, tcp: true, TCP_TABLE_OWNER_PID_LISTENER, rowSize: 56, (row) =>
            list.Add(new PortEntry(PortProtocol.Tcp, PortAt(row, 20), Ipv6At(row, 0), Marshal.ReadInt32(row, 52))));
        return list;
    }

    /// <summary>Bound UDP ports, IPv4 and IPv6.</summary>
    public static List<PortEntry> Udp()
    {
        var list = new List<PortEntry>();
        // MIB_UDPROW_OWNER_PID: local addr, local port, pid
        Read(AF_INET, tcp: false, UDP_TABLE_OWNER_PID, rowSize: 12, (row) =>
            list.Add(new PortEntry(PortProtocol.Udp, PortAt(row, 4), Ipv4At(row, 0), Marshal.ReadInt32(row, 8))));
        // MIB_UDP6ROW_OWNER_PID: local addr[16], scope, port, pid
        Read(AF_INET6, tcp: false, UDP_TABLE_OWNER_PID, rowSize: 28, (row) =>
            list.Add(new PortEntry(PortProtocol.Udp, PortAt(row, 20), Ipv6At(row, 0), Marshal.ReadInt32(row, 24))));
        return list;
    }

    private static void Read(int family, bool tcp, int tableClass, int rowSize, Action<IntPtr> onRow)
    {
        int size = 16 * 1024;
        for (int attempt = 0; attempt < 5; attempt++)
        {
            IntPtr buffer = Marshal.AllocHGlobal(size);
            try
            {
                int error = tcp
                    ? GetExtendedTcpTable(buffer, ref size, false, family, tableClass, 0)
                    : GetExtendedUdpTable(buffer, ref size, false, family, tableClass, 0);
                if (error == ERROR_INSUFFICIENT_BUFFER) continue; // size now holds what is needed
                if (error != 0) return; // for example, IPv6 is turned off

                int count = Marshal.ReadInt32(buffer);
                for (int i = 0; i < count; i++)
                    onRow(buffer + 4 + i * rowSize);
                return;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }

    // Ports are stored in network byte order in the low 16 bits.
    private static int PortAt(IntPtr row, int offset)
    {
        int raw = Marshal.ReadInt32(row, offset);
        return ((raw & 0xFF) << 8) | ((raw >> 8) & 0xFF);
    }

    private static IPAddress Ipv4At(IntPtr row, int offset) => new((uint)Marshal.ReadInt32(row, offset));

    private static IPAddress Ipv6At(IntPtr row, int offset)
    {
        var bytes = new byte[16];
        Marshal.Copy(row + offset, bytes, 0, 16);
        return new IPAddress(bytes);
    }

    [DllImport("iphlpapi.dll")]
    private static extern int GetExtendedTcpTable(IntPtr table, ref int size, bool sort, int family, int tableClass, int reserved);

    [DllImport("iphlpapi.dll")]
    private static extern int GetExtendedUdpTable(IntPtr table, ref int size, bool sort, int family, int tableClass, int reserved);
}
