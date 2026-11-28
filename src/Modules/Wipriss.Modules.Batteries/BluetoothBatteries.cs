using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;

namespace Wipriss.Modules.Batteries;

/// <summary>
/// Bluetooth mice, keyboards and headphones. Windows itself keeps their charge in a device property (the same
/// figure Settings shows); we read it with cfgmgr32 and show only devices connected right now. Connecting and
/// disconnecting come as events from a device watcher, so nothing is polled for that.
/// The approach follows HaloBattery (github.com/HeyOkay/HaloBattery, MIT), rewritten in C#.
/// </summary>
internal sealed partial class BluetoothBatteries : IDisposable
{
    private static readonly string[] Properties = ["System.Devices.Aep.IsConnected", "System.Devices.Aep.DeviceAddress"];

    private readonly object _gate = new();
    private readonly Dictionary<string, DeviceInformation> _devices = [];
    private readonly List<DeviceWatcher> _watchers = [];
    private readonly Dictionary<string, DeviceKind?> _kinds = [];

    /// <summary>A device came, went, connected or disconnected. Raised on a worker thread.</summary>
    public event Action? Changed;

    public void Start()
    {
        foreach (var selector in new[]
                 {
                     BluetoothDevice.GetDeviceSelectorFromPairingState(true),
                     BluetoothLEDevice.GetDeviceSelectorFromPairingState(true),
                 })
        {
            try
            {
                var watcher = DeviceInformation.CreateWatcher(selector, Properties, DeviceInformationKind.AssociationEndpoint);
                watcher.Added += (_, info) => { lock (_gate) _devices[info.Id] = info; Changed?.Invoke(); };
                watcher.Updated += (_, update) =>
                {
                    lock (_gate)
                    {
                        if (!_devices.TryGetValue(update.Id, out var info)) return;
                        info.Update(update);
                    }
                    if (update.Properties.ContainsKey(Properties[0])) Changed?.Invoke();
                };
                watcher.Removed += (_, update) => { lock (_gate) _devices.Remove(update.Id); Changed?.Invoke(); };
                watcher.Start();
                _watchers.Add(watcher);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // No Bluetooth radio or the service is off: nothing to show.
            }
        }
    }

    public bool AnyConnected
    {
        get { lock (_gate) return _devices.Values.Any(IsConnected); }
    }

    /// <summary>Connected devices that report a charge. Runs on a worker thread.</summary>
    public List<BatteryDevice> Read()
    {
        List<DeviceInformation> connected;
        lock (_gate) connected = _devices.Values.Where(IsConnected).ToList();
        if (connected.Count == 0) return [];

        var charge = ChargeByAddress();
        var result = new List<BatteryDevice>();
        foreach (var info in connected)
        {
            string mac = Address(info);
            if (mac.Length != 12 || !charge.TryGetValue(mac, out int percent)) continue;
            var kind = KindOf(info);
            if (kind is null) continue; // a phone or a computer
            string name = string.IsNullOrWhiteSpace(info.Name) ? "Bluetooth" : info.Name.Trim();
            result.Add(new BatteryDevice("bt-" + Hash(mac), name, kind.Value, percent, false));
        }
        // One device paired both as classic and LE shows up twice.
        return result.GroupBy(d => d.Key).Select(g => g.First()).ToList();
    }

    private static bool IsConnected(DeviceInformation info) =>
        info.Properties.TryGetValue(Properties[0], out var v) && v is true;

    private static string Address(DeviceInformation info) =>
        info.Properties.TryGetValue(Properties[1], out var v) && v is string s ? s.Replace(":", "").Replace("-", "").ToUpperInvariant() : "";

    private DeviceKind? KindOf(DeviceInformation info)
    {
        lock (_gate)
            if (_kinds.TryGetValue(info.Id, out var known)) return known;
        DeviceKind? kind = DeviceKind.Other;
        try
        {
            if (info.Id.StartsWith("BluetoothLE", StringComparison.OrdinalIgnoreCase))
            {
                using var le = BluetoothLEDevice.FromIdAsync(info.Id).AsTask().GetAwaiter().GetResult();
                if (le is not null) kind = BluetoothKinds.FromAppearance(le.Appearance.RawValue);
            }
            else
            {
                using var classic = BluetoothDevice.FromIdAsync(info.Id).AsTask().GetAwaiter().GetResult();
                if (classic is not null) kind = BluetoothKinds.FromClassOfDevice(classic.ClassOfDevice.RawValue);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { }
        lock (_gate) _kinds[info.Id] = kind;
        return kind;
    }

    /// <summary>A short stable key, so the Bluetooth address itself is not kept around in the UI.</summary>
    private static string Hash(string mac) => Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(mac)))[..12].ToLowerInvariant();

    // ---- The charge: DEVPKEY_Bluetooth_Battery on the device's nodes ----

    private static DEVPROPKEY BatteryKey = new() { fmtid = new Guid("104EA319-6EE2-4701-BD47-8DDBF425BBE5"), pid = 2 };
    private const int CM_GETIDLIST_FILTER_ENUMERATOR = 0x1, CM_GETIDLIST_FILTER_PRESENT = 0x100;
    private const uint DEVPROP_TYPE_BYTE = 0x3;

    [GeneratedRegex(@"DEV_([0-9A-F]{12})", RegexOptions.IgnoreCase)]
    private static partial Regex DevAddress();

    [GeneratedRegex(@"([0-9A-F]{12})_C[0-9A-F]+$", RegexOptions.IgnoreCase)]
    private static partial Regex ServiceAddress();

    /// <summary>The Bluetooth address in a device id, upper-case without separators, or "".</summary>
    public static string AddressInId(string id)
    {
        var m = DevAddress().Match(id);
        if (!m.Success) m = ServiceAddress().Match(id);
        return m.Success ? m.Groups[1].Value.ToUpperInvariant() : "";
    }

    private static Dictionary<string, int> ChargeByAddress()
    {
        var result = new Dictionary<string, int>();
        foreach (var enumerator in new[] { "BTHENUM", "BTHLE", "BTHLEDEVICE" })
        {
            int flags = CM_GETIDLIST_FILTER_ENUMERATOR | CM_GETIDLIST_FILTER_PRESENT;
            if (CM_Get_Device_ID_List_SizeW(out int size, enumerator, flags) != 0 || size <= 1) continue;
            var buffer = new char[size];
            if (CM_Get_Device_ID_ListW(enumerator, buffer, size, flags) != 0) continue;
            foreach (var id in new string(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries))
            {
                string mac = AddressInId(id);
                if (mac.Length == 0 || CM_Locate_DevNodeW(out uint node, id, 0) != 0) continue;
                byte value = 0;
                uint length = 1;
                if (CM_Get_DevNode_PropertyW(node, ref BatteryKey, out uint type, ref value, ref length, 0) != 0 || type != DEVPROP_TYPE_BYTE) continue;
                if (value <= 100) result[mac] = value;
            }
        }
        return result;
    }

    public void Dispose()
    {
        foreach (var watcher in _watchers)
        {
            try
            {
                if (watcher.Status is DeviceWatcherStatus.Started or DeviceWatcherStatus.EnumerationCompleted) watcher.Stop();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { }
        }
        _watchers.Clear();
        lock (_gate) _devices.Clear();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DEVPROPKEY
    {
        public Guid fmtid;
        public uint pid;
    }

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_ID_List_SizeW(out int length, string filter, int flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_ID_ListW(string filter, char[] buffer, int length, int flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Locate_DevNodeW(out uint node, string id, int flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_DevNode_PropertyW(uint node, ref DEVPROPKEY key, out uint type, ref byte buffer, ref uint size, int flags);
}
