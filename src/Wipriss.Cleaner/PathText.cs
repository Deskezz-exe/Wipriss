using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace Wipriss.Cleaner;

/// <summary>Plain text helpers of the inventory: paths out of command lines, names for matching, .lnk targets.</summary>
internal static partial class PathText
{
    /// <summary>
    /// The program a command line starts: <c>"C:\A b\x.exe" -arg</c>, <c>C:\A b\x.exe -arg</c> (no quotes),
    /// <c>\??\C:\x.sys</c>, <c>\SystemRoot\x.sys</c>, <c>%ProgramFiles%\x.exe</c>, <c>rundll32 C:\x.dll,Entry</c>.
    /// Null when there is no full path.
    /// </summary>
    public static string? ExePath(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        var text = Environment.ExpandEnvironmentVariables(command.Trim());
        if (text.StartsWith(@"\??\", StringComparison.Ordinal)) text = text[4..];
        if (text.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase))
            text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), text[12..]);
        if (text.StartsWith("system32\\", StringComparison.OrdinalIgnoreCase))
            text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), text);

        string? first;
        if (text.StartsWith('"'))
        {
            int end = text.IndexOf('"', 1);
            first = end > 1 ? text[1..end] : null;
        }
        else
        {
            var match = ProgramPath().Match(text);
            // No full path: maybe a bare host name («rundll32.exe C:\x.dll,Entry»).
            first = match.Success ? match.Value : text.Split(' ', 2)[0];
        }
        if (first is not null && IsHost(first))
        {
            // rundll32 / cmd / wscript: the real program is the argument.
            var rest = text[(text.IndexOf(first, StringComparison.OrdinalIgnoreCase) + first.Length)..].TrimStart('"', ' ');
            var inner = ExePath(rest.Split(',')[0]);
            if (inner is not null) return inner;
        }
        return first is not null && IsFull(first) ? first : null;
    }

    private static bool IsHost(string exe)
    {
        var name = Path.GetFileName(exe);
        return name.Equals("rundll32.exe", StringComparison.OrdinalIgnoreCase)
            || name.Equals("cmd.exe", StringComparison.OrdinalIgnoreCase)
            || name.Equals("wscript.exe", StringComparison.OrdinalIgnoreCase)
            || name.Equals("powershell.exe", StringComparison.OrdinalIgnoreCase)
            || name.Equals("pwsh.exe", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsFull(string path) =>
        path.Length > 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] == '\\';

    /// <summary>A path ready for comparing: full, no trailing slash, no doubled slashes.</summary>
    public static string? Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var text = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
        if (!IsFull(text)) return null;
        try { text = Path.GetFullPath(text); } catch (Exception) { return null; }
        return text.TrimEnd('\\');
    }

    /// <summary>Is <paramref name="path"/> the folder <paramref name="folder"/> or somewhere inside it?</summary>
    public static bool IsInside(string path, string folder) =>
        path.Length >= folder.Length
        && path.StartsWith(folder, StringComparison.OrdinalIgnoreCase)
        && (path.Length == folder.Length || path[folder.Length] == '\\');

    /// <summary>A name for matching: letters and digits only, lower case, no version numbers and no
    /// installer words. «Mozilla Firefox (x64 ru)» → «mozillafirefox».</summary>
    public static string NameKey(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";
        var text = name.ToLowerInvariant();
        text = Noise().Replace(text, " ");
        var sb = new StringBuilder(text.Length);
        foreach (var c in text) if (char.IsLetterOrDigit(c)) sb.Append(c);
        return sb.ToString();
    }

    /// <summary>Does a folder or file name belong to an installed name or publisher? Short names (3 letters)
    /// only match exactly, so «App» does not match every «...app...».</summary>
    public static bool NamesMatch(string folderKey, string otherKey)
    {
        if (folderKey.Length == 0 || otherKey.Length == 0) return false;
        if (folderKey == otherKey) return true;
        if (folderKey.Length >= 4 && otherKey.Contains(folderKey, StringComparison.Ordinal)) return true;
        return otherKey.Length >= 5 && folderKey.Contains(otherKey, StringComparison.Ordinal);
    }

    // Unquoted command line: everything up to the first program extension.
    [GeneratedRegex(@"^[A-Za-z]:\\.*?\.(exe|dll|sys|cpl|scr|com|bat|cmd|ps1|vbs|js|msc)(?=$|[\s,""])", RegexOptions.IgnoreCase)]
    private static partial Regex ProgramPath();

    [GeneratedRegex(@"\(.*?\)|\b(x64|x86|amd64|arm64|64-bit|32-bit|64bit|32bit|setup|installer|install|portable|version|v?\d+(\.\d+)+[a-z]?|inc|llc|ltd|gmbh|corp|corporation|co|the)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Noise();

    [GeneratedRegex(@"(?:[A-Za-z]:|%\w+%)\\[^\x00-\x1F""<>|*?]*?\.(?:exe|dll|cmd|bat|ps1|vbs|js)", RegexOptions.IgnoreCase)]
    internal static partial Regex PathInText();

    /// <summary>
    /// The target of a .lnk shortcut, read from the file itself (MS-SHLLINK: the LinkInfo local base path,
    /// Unicode when present), without COM. Null for shortcuts to non-file places.
    /// </summary>
    public static string? ShortcutTarget(byte[] data)
    {
        try
        {
            if (data.Length < 0x4C || BitConverter.ToInt32(data, 0) != 0x4C) return null;
            int flags = BitConverter.ToInt32(data, 0x14);
            int pos = 0x4C;
            if ((flags & 0x1) != 0) pos += 2 + BitConverter.ToUInt16(data, pos); // LinkTargetIDList
            if ((flags & 0x2) == 0) return null; // no LinkInfo
            int infoStart = pos;
            int headerSize = BitConverter.ToInt32(data, infoStart + 4);
            int infoFlags = BitConverter.ToInt32(data, infoStart + 8);
            if ((infoFlags & 0x1) == 0) return null; // not on a local volume
            if (headerSize >= 0x24)
            {
                int unicodeOffset = BitConverter.ToInt32(data, infoStart + 0x1C);
                if (unicodeOffset > 0) return ReadUnicodeZ(data, infoStart + unicodeOffset);
            }
            int ansiOffset = BitConverter.ToInt32(data, infoStart + 0x10);
            return ReadAnsiZ(data, infoStart + ansiOffset);
        }
        catch (ArgumentException) { return null; }
        catch (IndexOutOfRangeException) { return null; }
    }

    private static string? ReadUnicodeZ(byte[] data, int start)
    {
        int end = start;
        while (end + 1 < data.Length && (data[end] != 0 || data[end + 1] != 0)) end += 2;
        return end > start ? Encoding.Unicode.GetString(data, start, end - start) : null;
    }

    private static string? ReadAnsiZ(byte[] data, int start)
    {
        int end = Array.IndexOf(data, (byte)0, start);
        if (end <= start) return null;
        return Encoding.Latin1.GetString(data, start, end - start);
    }
}
