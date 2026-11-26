using System.IO;
using System.Runtime.InteropServices;

namespace Wipriss.Modules.Converter;

/// <summary>
/// "Заменить оригиналы": after a picture was converted, its original goes to the Recycle Bin and the result takes
/// the original's name (with the new extension). Nothing is ever deleted for good:
/// <list type="bullet">
/// <item>only on local fixed drives (USB sticks and network folders have no Recycle Bin);</item>
/// <item>only when the result was saved next to the original;</item>
/// <item>if Windows can't recycle (the Bin is off on that drive), it asks the owner itself instead of deleting;</item>
/// <item>any failure keeps both files.</item>
/// </list>
/// </summary>
internal static class OriginalReplacer
{
    /// <returns>The path of the result after the swap, and a short note for the owner if the original was kept.</returns>
    public static (string Output, string? Note) Replace(string original, string output)
    {
        try
        {
            if (!Path.IsPathFullyQualified(original) || !Path.IsPathFullyQualified(output)) return (output, "оригинал оставлен");
            original = Path.GetFullPath(original);
            output = Path.GetFullPath(output);
            if (string.Equals(original, output, StringComparison.OrdinalIgnoreCase) || !File.Exists(original) || !File.Exists(output))
                return (output, "оригинал оставлен");

            string folder = Path.GetDirectoryName(original)!;
            if (!string.Equals(Path.GetDirectoryName(output), folder, StringComparison.OrdinalIgnoreCase))
                return (output, "оригинал оставлен: папка только для чтения");
            if (!HasRecycleBin(original)) return (output, "оригинал оставлен: у этого диска нет Корзины");

            if (!Recycle(original) || File.Exists(original)) return (output, "оригинал оставлен: Windows не убрала его в Корзину");

            // The result takes the original's name when that name is free now.
            string target = Path.Combine(folder, Path.GetFileNameWithoutExtension(original) + Path.GetExtension(output));
            if (string.Equals(target, output, StringComparison.OrdinalIgnoreCase) || File.Exists(target) || Directory.Exists(target))
                return (output, null);
            try
            {
                File.Move(output, target, overwrite: false);
                return (target, null);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return (output, null);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return (output, "оригинал оставлен");
        }
    }

    internal static bool HasRecycleBin(string path)
    {
        string? root = Path.GetPathRoot(path);
        if (string.IsNullOrEmpty(root) || root.StartsWith(@"\\", StringComparison.Ordinal)) return false;
        try { return new DriveInfo(root).DriveType == DriveType.Fixed; }
        catch (Exception ex) when (ex is ArgumentException or IOException) { return false; }
    }

    private static bool Recycle(string path)
    {
        var operation = new FileOperation
        {
            Func = FoDelete,
            From = path + "\0\0",
            // To the Bin, no "are you sure", no progress window. WANTNUKEWARNING: if Windows can't recycle,
            // it asks the owner before deleting for good rather than doing it silently.
            Flags = FofAllowUndo | FofNoConfirmation | FofSilent | FofNoErrorUi | FofWantNukeWarning,
        };
        return SHFileOperationW(ref operation) == 0 && !operation.AnyOperationsAborted;
    }

    private const uint FoDelete = 3;
    private const ushort FofSilent = 0x4, FofNoConfirmation = 0x10, FofAllowUndo = 0x40, FofNoErrorUi = 0x400, FofWantNukeWarning = 0x4000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct FileOperation
    {
        public nint Window;
        public uint Func;
        [MarshalAs(UnmanagedType.LPWStr)] public string From;
        [MarshalAs(UnmanagedType.LPWStr)] public string? To;
        public ushort Flags;
        [MarshalAs(UnmanagedType.Bool)] public bool AnyOperationsAborted;
        public nint NameMappings;
        [MarshalAs(UnmanagedType.LPWStr)] public string? ProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHFileOperationW(ref FileOperation operation);
}
