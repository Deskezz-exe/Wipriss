using System.IO;
using System.Text;

namespace Wipriss.App.Shell;

/// <summary>
/// A selection too long for a command line: WiprissExplorerMenu.dll writes the paths into
/// <c>%TEMP%\Wipriss-convert-*.txt</c> (one per line, UTF-8) and passes that file. Only such a file is read,
/// and it is deleted right after.
/// </summary>
internal static class ExplorerList
{
    private const string Prefix = "Wipriss-convert-"; // LIST_PREFIX in WiprissExplorerMenu.c
    private const long MaxBytes = 4 * 1024 * 1024;

    public static List<string> Read(string path)
    {
        try
        {
            if (!IsOurs(path)) return [];
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > MaxBytes) return [];
            var lines = File.ReadAllLines(path, new UTF8Encoding(false));
            File.Delete(path);
            return lines.Select(l => l.Trim()).Where(l => l.Length > 0 && Path.IsPathFullyQualified(l)).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return [];
        }
    }

    internal static bool IsOurs(string path)
    {
        if (!Path.IsPathFullyQualified(path)) return false;
        string full = Path.GetFullPath(path);
        string temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        string name = Path.GetFileName(full);
        return string.Equals(Path.GetDirectoryName(full), temp, StringComparison.OrdinalIgnoreCase)
               && name.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)
               && name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase);
    }
}
