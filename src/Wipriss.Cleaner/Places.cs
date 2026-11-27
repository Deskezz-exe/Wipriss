using System.IO;
using System.Runtime.InteropServices;

namespace Wipriss.Cleaner;

/// <summary>The folders the scanner looks at, resolved for the current user.</summary>
internal static class Places
{
    public static string Local { get; } = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    public static string Roaming { get; } = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    public static string LocalLow { get; } = Local + "Low";
    public static string ProgramData { get; } = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
    public static string Profile { get; } = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    public static string Windows { get; } = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
    public static string ProgramFiles { get; } = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
    public static string ProgramFilesX86 { get; } = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
    public static string SystemDrive { get; } = Path.GetPathRoot(Windows) ?? @"C:\";
    public static string Temp { get; } = Path.GetTempPath().TrimEnd('\\');

    /// <summary>Other fixed drives that are ready («D:\», «E:\»), without the system drive.</summary>
    public static IReadOnlyList<string> OtherDrives { get; } = FindOtherDrives();

    private static List<string> FindOtherDrives()
    {
        var list = new List<string>();
        try
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                try
                {
                    if (drive.DriveType == DriveType.Fixed && drive.IsReady
                        && !drive.Name.Equals(SystemDrive, StringComparison.OrdinalIgnoreCase))
                        list.Add(drive.Name);
                }
                catch (IOException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return list;
    }

    public static string Downloads { get; } = KnownFolder(new Guid("374DE290-123F-4565-9164-39C4925E467B"))
                                             ?? Path.Combine(Profile, "Downloads");

    private static string? KnownFolder(Guid id)
    {
        try
        {
            if (SHGetKnownFolderPath(id, 0, IntPtr.Zero, out var pointer) != 0) return null;
            try { return Marshal.PtrToStringUni(pointer); }
            finally { Marshal.FreeCoTaskMem(pointer); }
        }
        catch (Exception) { return null; }
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid id, uint flags, IntPtr token, out IntPtr path);

    /// <summary>Bytes in all Recycle Bins, or -1.</summary>
    public static long RecycleBinSize(out long items)
    {
        items = 0;
        var info = new RecycleBinInfo { Size = Marshal.SizeOf<RecycleBinInfo>() };
        if (SHQueryRecycleBin(null, ref info) != 0) return -1;
        items = info.Items;
        return info.Bytes;
    }

    // shellapi.h packs this structure to 8 bytes on 64-bit Windows (the default here), to 1 on 32-bit.
    [StructLayout(LayoutKind.Sequential)]
    private struct RecycleBinInfo
    {
        public int Size;
        public long Bytes;
        public long Items;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHQueryRecycleBin(string? root, ref RecycleBinInfo info);
}
