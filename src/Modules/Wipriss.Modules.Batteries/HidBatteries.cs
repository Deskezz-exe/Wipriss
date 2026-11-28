using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Wipriss.Modules.Batteries;

/// <summary>A device with a battery that is connected now.</summary>
/// <param name="Key">Stable id (no personal data): vendor, product and slot, or a hash of the Bluetooth address.</param>
public sealed record BatteryDevice(string Key, string Name, DeviceKind Kind, int Percent, bool Charging);

/// <summary>
/// Wireless receivers and headsets on USB (2.4 GHz). Only devices of the vendors we know are opened, only their
/// vendor-specific collections, and only "how charged are you" questions are sent. Runs on a worker thread.
/// </summary>
internal static class HidBatteries
{
    private static readonly Guid HidInterface = new("4D1E55B2-F16F-11CF-88CB-001111000030");
    private static readonly TimeSpan ReplyTimeout = TimeSpan.FromMilliseconds(600);
    private static readonly ushort[] Vendors =
        [LogitechHidpp.Vendor, RazerProtocol.Vendor, HyperXProtocol.Vendor, SteelSeriesProtocol.Vendor, CorsairProtocol.Vendor];

    private sealed record Hid(string Path, ushort Vid, ushort Pid, ushort UsagePage, ushort Usage, int InLen, int OutLen, int FeatureLen, string Product);

    public static List<BatteryDevice> Read()
    {
        var found = new List<BatteryDevice>();
        var all = Enumerate();
        foreach (var hid in all)
        {
            try
            {
                switch (hid.Vid)
                {
                    case LogitechHidpp.Vendor when hid.UsagePage == 0xFF00 && hid.Usage == 2 && hid.OutLen == LogitechHidpp.LongLength:
                    case LogitechHidpp.Vendor when hid.UsagePage == 0xFF43 && hid.Usage == 0x0202 && hid.OutLen == LogitechHidpp.LongLength:
                        found.AddRange(Logitech(hid));
                        break;
                    case RazerProtocol.Vendor when hid.FeatureLen == RazerProtocol.ReportLength + 1:
                        if (Razer(hid, all) is { } razer && found.All(f => f.Key != razer.Key)) found.Add(razer);
                        break;
                    case HyperXProtocol.Vendor when hid.UsagePage == HyperXProtocol.UsagePage && hid.Usage == HyperXProtocol.Usage:
                        if (HyperX(hid) is { } hyperx) found.Add(hyperx);
                        break;
                    case SteelSeriesProtocol.Vendor when hid.UsagePage == SteelSeriesProtocol.UsagePage
                                                       && hid.Product.Contains("Arctis", StringComparison.OrdinalIgnoreCase):
                        if (SteelSeries(hid) is { } steel) found.Add(steel);
                        break;
                    case CorsairProtocol.Vendor when hid.UsagePage == CorsairProtocol.UsagePage
                                                   && (hid.Product.Contains("HS", StringComparison.Ordinal) || hid.Product.Contains("VOID", StringComparison.OrdinalIgnoreCase)):
                        if (Corsair(hid) is { } corsair) found.Add(corsair);
                        break;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException or TimeoutException) { }
        }
        return found;
    }

    // ---- Logitech: up to six devices behind one receiver, or one cabled device ----

    private static IEnumerable<BatteryDevice> Logitech(Hid hid)
    {
        using var io = Open(hid);
        if (io is null) yield break;
        byte swid = 0x0A;
        byte NextSwid() { swid = swid >= 0x0F ? (byte)0x0A : (byte)(swid + 1); return swid; }

        // A request and its reply, or null (error, nothing in the slot, no answer).
        byte[]? Ask(byte slot, byte index, byte function, params byte[] p)
        {
            byte id = NextSwid();
            io.Write(LogitechHidpp.Request(slot, index, function, id, p));
            return io.ReadUntil(r => LogitechHidpp.IsReply(r, slot, index, function, id) || LogitechHidpp.IsError(r, slot), ReplyTimeout) is { } reply
                   && !LogitechHidpp.IsError(reply, slot) ? reply : null;
        }

        byte Index(byte slot, ushort feature) =>
            Ask(slot, 0, 0, (byte)(feature >> 8), (byte)feature) is { } r ? r[4] : (byte)0;

        foreach (byte slot in new byte[] { 0xFF, 1, 2, 3, 4, 5, 6 })
        {
            Charge? charge = null;
            byte unified = Index(slot, LogitechHidpp.UnifiedBattery);
            if (unified != 0 && Ask(slot, unified, 1) is { } u) charge = LogitechHidpp.ParseUnified(u.AsSpan(4));
            if (charge is null && Index(slot, LogitechHidpp.BatteryStatus) is var status and not 0 && Ask(slot, status, 0) is { } s)
                charge = LogitechHidpp.ParseStatus(s.AsSpan(4));
            if (charge is null && Index(slot, LogitechHidpp.BatteryVoltage) is var volt and not 0 && Ask(slot, volt, 0) is { } v)
                charge = LogitechHidpp.ParseVoltage(v.AsSpan(4));
            if (charge is not { } c) continue;

            var kind = DeviceKind.Other;
            string name = "";
            byte names = Index(slot, LogitechHidpp.NameFeature);
            if (names != 0)
            {
                if (Ask(slot, names, 2) is { } t) kind = LogitechHidpp.KindOf(t[4]);
                if (Ask(slot, names, 0) is { } count)
                {
                    var text = new System.Text.StringBuilder();
                    for (int at = 0; at < Math.Min((int)count[4], 64);)
                    {
                        if (Ask(slot, names, 1, (byte)at) is not { } part) break;
                        int n = 0;
                        for (int i = 4; i < part.Length && at < count[4]; i++, at++, n++)
                            if (part[i] != 0) text.Append((char)part[i]);
                        if (n == 0) break;
                    }
                    name = text.ToString().Trim();
                }
            }
            if (hid.UsagePage == 0xFF43) kind = DeviceKind.Headset;
            yield return new BatteryDevice($"logi-{hid.Pid:x4}-{slot:x2}", name.Length > 0 ? name : "Logitech", kind, c.Percent, c.Charging);
        }
    }

    // ---- Razer: one feature report, the level as 0..255 ----

    private static BatteryDevice? Razer(Hid hid, List<Hid> all)
    {
        using var handle = OpenHandle(hid.Path);
        if (handle is null) return null;
        foreach (byte tid in RazerProtocol.TransactionIds)
        {
            if (Feature(handle, RazerProtocol.Request(tid, RazerProtocol.LevelCommand), RazerProtocol.LevelCommand) is not { } level) continue;
            bool charging = Feature(handle, RazerProtocol.Request(tid, RazerProtocol.ChargingCommand), RazerProtocol.ChargingCommand) == 1;
            var kind = tid == 0x3F ? DeviceKind.Headset : KindFromSiblings(hid, all) ?? DeviceKind.Mouse;
            return new BatteryDevice($"razer-{hid.Pid:x4}", Named(hid, "Razer"), kind, RazerProtocol.LevelToPercent(level), charging);
        }
        return null;
    }

    private static byte? Feature(SafeFileHandle handle, byte[] request, byte command)
    {
        var buffer = new byte[RazerProtocol.ReportLength + 1]; // report id 0 first
        request.CopyTo(buffer, 1);
        if (!HidD_SetFeature(handle, buffer, buffer.Length)) return null;
        Thread.Sleep(60); // the device needs a moment to prepare the answer
        Array.Clear(buffer);
        if (!HidD_GetFeature(handle, buffer, buffer.Length)) return null;
        return RazerProtocol.Value(buffer.AsSpan(1), command);
    }

    // ---- Headsets: one output report, one reply ----

    private static BatteryDevice? HyperX(Hid hid)
    {
        if (hid.OutLen < HyperXProtocol.RequestLength) return null;
        using var io = Open(hid);
        if (io is null) return null;
        io.Write(HyperXProtocol.Request(HyperXProtocol.BatteryCommand));
        if (io.ReadUntil(r => HyperXProtocol.IsReplyTo(r, HyperXProtocol.BatteryCommand), ReplyTimeout) is not { } level
            || HyperXProtocol.Percent(level) is not { } percent) return null;
        io.Write(HyperXProtocol.Request(HyperXProtocol.ChargingCommand));
        bool charging = io.ReadUntil(r => HyperXProtocol.IsReplyTo(r, HyperXProtocol.ChargingCommand), ReplyTimeout) is { } c && HyperXProtocol.Charging(c);
        return new BatteryDevice($"hyperx-{hid.Pid:x4}", Named(hid, "HyperX"), DeviceKind.Headset, percent, charging);
    }

    private static BatteryDevice? SteelSeries(Hid hid)
    {
        using var io = Open(hid);
        if (io is null) return null;
        io.Write(SteelSeriesProtocol.Request);
        if (io.ReadUntil(r => SteelSeriesProtocol.Parse(r) is not null, ReplyTimeout) is not { } reply
            || SteelSeriesProtocol.Parse(reply) is not { } c) return null;
        return new BatteryDevice($"steelseries-{hid.Pid:x4}", Named(hid, "SteelSeries"), DeviceKind.Headset, c.Percent, c.Charging);
    }

    private static BatteryDevice? Corsair(Hid hid)
    {
        using var io = Open(hid);
        if (io is null) return null;
        io.Write(CorsairProtocol.Request);
        if (io.ReadUntil(r => CorsairProtocol.Percent(r) is not null, ReplyTimeout) is not { } reply
            || CorsairProtocol.Percent(reply) is not { } percent) return null;
        return new BatteryDevice($"corsair-{hid.Pid:x4}", Named(hid, "Corsair"), DeviceKind.Headset, percent, false);
    }

    private static string Named(Hid hid, string fallback) => hid.Product.Length > 0 ? hid.Product : fallback;

    /// <summary>A mouse or a keyboard by the device's other collections (generic desktop page).</summary>
    private static DeviceKind? KindFromSiblings(Hid hid, List<Hid> all)
    {
        var siblings = all.Where(h => h.Vid == hid.Vid && h.Pid == hid.Pid && h.UsagePage == 1).ToList();
        if (siblings.Any(h => h.Usage == 2)) return DeviceKind.Mouse;
        if (siblings.Any(h => h.Usage == 6)) return DeviceKind.Keyboard;
        return null;
    }

    // ---- Windows HID ----

    private static List<Hid> Enumerate()
    {
        var list = new List<Hid>();
        var guid = HidInterface;
        if (CM_Get_Device_Interface_List_SizeW(out int size, ref guid, null, 0) != 0 || size <= 1) return list;
        var buffer = new char[size];
        if (CM_Get_Device_Interface_ListW(ref guid, null, buffer, size, 0) != 0) return list;
        foreach (var path in new string(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            // Cheap filter by the path first: only the vendors we know are opened at all.
            if (!Vendors.Any(v => path.Contains($"vid_{v:x4}", StringComparison.OrdinalIgnoreCase))) continue;
            using var h = CreateFileW(path, 0, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            if (h.IsInvalid) continue;
            var attributes = new HIDD_ATTRIBUTES { Size = Marshal.SizeOf<HIDD_ATTRIBUTES>() };
            if (!HidD_GetAttributes(h, ref attributes) || !HidD_GetPreparsedData(h, out IntPtr data)) continue;
            try
            {
                if (HidP_GetCaps(data, out HIDP_CAPS caps) != HIDP_STATUS_SUCCESS) continue;
                var product = new char[128];
                string name = HidD_GetProductString(h, product, product.Length * 2) ? new string(product).TrimEnd('\0').Trim() : "";
                list.Add(new Hid(path, attributes.VendorID, attributes.ProductID, caps.UsagePage, caps.Usage,
                    caps.InputReportByteLength, caps.OutputReportByteLength, caps.FeatureReportByteLength, name));
            }
            finally { HidD_FreePreparsedData(data); }
        }
        return list;
    }

    private static SafeFileHandle? OpenHandle(string path)
    {
        var h = CreateFileW(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_OVERLAPPED, IntPtr.Zero);
        if (!h.IsInvalid) return h;
        h.Dispose();
        return null;
    }

    private static HidIo? Open(Hid hid) => OpenHandle(hid.Path) is { } h ? new HidIo(h, hid.InLen, hid.OutLen) : null;

    /// <summary>Output reports and input reports with a timeout. Replies come with the report id first, like hidapi gives them.</summary>
    private sealed class HidIo(SafeFileHandle handle, int inLen, int outLen) : IDisposable
    {
        private readonly FileStream _stream = new(handle, FileAccess.ReadWrite, 0, isAsync: true);

        public void Write(byte[] report)
        {
            var buffer = new byte[outLen];
            Array.Copy(report, buffer, Math.Min(report.Length, outLen));
            _stream.Write(buffer, 0, buffer.Length);
        }

        public byte[]? ReadUntil(Func<byte[], bool> isAnswer, TimeSpan timeout)
        {
            using var cancel = new CancellationTokenSource(timeout);
            var buffer = new byte[Math.Max(inLen, 1)];
            try
            {
                while (true)
                {
                    int n = _stream.ReadAsync(buffer, cancel.Token).AsTask().GetAwaiter().GetResult();
                    if (n <= 0) return null;
                    // Report id 0 means "no ids": hidapi drops that byte, and the offsets in the notes assume so.
                    var reply = buffer[0] == 0 ? buffer[1..n] : buffer[..n];
                    if (isAnswer(reply)) return reply;
                }
            }
            catch (OperationCanceledException) { return null; }
        }

        public void Dispose() => _stream.Dispose();
    }

    private const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000;
    private const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, OPEN_EXISTING = 3, FILE_FLAG_OVERLAPPED = 0x40000000;
    private const int HIDP_STATUS_SUCCESS = 0x00110000;

    [StructLayout(LayoutKind.Sequential)]
    private struct HIDD_ATTRIBUTES
    {
        public int Size;
        public ushort VendorID, ProductID, VersionNumber;
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct HIDP_CAPS
    {
        public ushort Usage, UsagePage, InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
        public fixed ushort Reserved[17];
        public ushort NumberLinkCollectionNodes, NumberInputButtonCaps, NumberInputValueCaps, NumberInputDataIndices,
            NumberOutputButtonCaps, NumberOutputValueCaps, NumberOutputDataIndices,
            NumberFeatureButtonCaps, NumberFeatureValueCaps, NumberFeatureDataIndices;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

    [DllImport("hid.dll")]
    private static extern bool HidD_GetAttributes(SafeFileHandle device, ref HIDD_ATTRIBUTES attributes);

    [DllImport("hid.dll")]
    private static extern bool HidD_GetPreparsedData(SafeFileHandle device, out IntPtr data);

    [DllImport("hid.dll")]
    private static extern bool HidD_FreePreparsedData(IntPtr data);

    [DllImport("hid.dll")]
    private static extern int HidP_GetCaps(IntPtr data, out HIDP_CAPS caps);

    [DllImport("hid.dll", CharSet = CharSet.Unicode)]
    private static extern bool HidD_GetProductString(SafeFileHandle device, char[] buffer, int bufferBytes);

    [DllImport("hid.dll")]
    private static extern bool HidD_SetFeature(SafeFileHandle device, byte[] report, int length);

    [DllImport("hid.dll")]
    private static extern bool HidD_GetFeature(SafeFileHandle device, byte[] report, int length);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_Interface_List_SizeW(out int length, ref Guid interfaceClass, string? deviceId, int flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_Interface_ListW(ref Guid interfaceClass, string? deviceId, char[] buffer, int length, int flags);
}
