using System.Runtime.InteropServices;

namespace Wipriss.ProcessTools;

/// <summary>Turns kernel paths ("\device\harddiskvolume3\program files\…") into normal ones ("C:\program files\…").</summary>
public static class DevicePaths
{
    private static readonly Lazy<List<(string Device, string Drive)>> Drives = new(ReadDrives);

    public static string? ToDosPath(string devicePath)
    {
        if (!devicePath.StartsWith(@"\device\", StringComparison.OrdinalIgnoreCase)) return null;
        foreach (var (device, drive) in Drives.Value)
            if (devicePath.Length > device.Length && devicePath.StartsWith(device, StringComparison.OrdinalIgnoreCase)
                && devicePath[device.Length] == '\\')
                return drive + devicePath[device.Length..];
        return null;
    }

    private static List<(string, string)> ReadDrives()
    {
        var list = new List<(string, string)>();
        var buffer = new char[1024];
        foreach (string root in Environment.GetLogicalDrives())
        {
            string drive = root.TrimEnd('\\');
            int length = QueryDosDeviceW(drive, buffer, buffer.Length);
            if (length > 0) list.Add((new string(buffer, 0, Array.IndexOf(buffer, '\0')), drive));
        }

        return list;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int QueryDosDeviceW(string deviceName, char[] targetPath, int max);
}
