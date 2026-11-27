using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Wipriss.Cleaner;

/// <summary>A registry hive in one view: HKLM 64-bit, HKLM 32-bit (WOW6432Node) or HKCU.</summary>
internal readonly record struct RegRoot(RegistryHive Hive, RegistryView View)
{
    public static readonly RegRoot Machine64 = new(RegistryHive.LocalMachine, RegistryView.Registry64);
    public static readonly RegRoot Machine32 = new(RegistryHive.LocalMachine, RegistryView.Registry32);
    public static readonly RegRoot User = new(RegistryHive.CurrentUser, RegistryView.Registry64);

    public static readonly RegRoot[] All = [Machine64, Machine32, User];

    public bool IsMachine => Hive == RegistryHive.LocalMachine;

    public string Code => !IsMachine ? "HKCU" : View == RegistryView.Registry32 ? "HKLM32" : "HKLM64";

    public static RegRoot? Parse(string code) => code switch
    {
        "HKCU" => User,
        "HKLM64" => Machine64,
        "HKLM32" => Machine32,
        _ => null,
    };

    /// <summary>The path as regedit shows it: «HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Foo».</summary>
    public string Full(string path)
    {
        var hive = IsMachine ? "HKEY_LOCAL_MACHINE" : "HKEY_CURRENT_USER";
        if (IsMachine && View == RegistryView.Registry32 && path.StartsWith(@"SOFTWARE\", StringComparison.OrdinalIgnoreCase)
            && !path.StartsWith(@"SOFTWARE\WOW6432Node\", StringComparison.OrdinalIgnoreCase))
            path = @"SOFTWARE\WOW6432Node\" + path[9..];
        return hive + "\\" + path;
    }

    /// <summary>Short form for the list: «HKLM\SOFTWARE\Foo», «HKCU\Software\Foo».</summary>
    public string Short(string path) => Full(path).Replace("HKEY_LOCAL_MACHINE", "HKLM").Replace("HKEY_CURRENT_USER", "HKCU");

    public RegistryKey OpenBase() => RegistryKey.OpenBaseKey(Hive, View);

    public RegistryKey? Open(string path, bool writable = false)
    {
        try
        {
            using var root = OpenBase();
            return root.OpenSubKey(path, writable);
        }
        catch (Exception ex) when (Reg.Expected(ex)) { return null; }
    }

    public bool Exists(string path)
    {
        using var key = Open(path);
        return key is not null;
    }
}

internal static class Reg
{
    public static bool Expected(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException;

    public static RegistryKey? SafeOpen(RegistryKey parent, string name)
    {
        try { return parent.OpenSubKey(name); }
        catch (Exception ex) when (Expected(ex)) { return null; }
    }

    public static string[] SubKeys(RegistryKey? key)
    {
        if (key is null) return [];
        try { return key.GetSubKeyNames(); }
        catch (Exception ex) when (Expected(ex)) { return []; }
    }

    public static string? Text(RegistryKey? key, string? name)
    {
        try { return key?.GetValue(name, null, RegistryValueOptions.None) as string; }
        catch (Exception ex) when (Expected(ex)) { return null; }
    }

    /// <summary>When the key was last written (local time), or null.</summary>
    public static DateTime? LastWrite(RegistryKey key)
    {
        try
        {
            if (RegQueryInfoKey(key.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                    IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, out long time) != 0) return null;
            return DateTime.FromFileTime(time);
        }
        catch (Exception ex) when (Expected(ex) || ex is ObjectDisposedException) { return null; }
    }

    /// <summary>
    /// The packed form Windows Installer uses for a product code in its own keys:
    /// {12345678-ABCD-EF01-2345-6789ABCDEF01} → 87654321DCBA10FE32547698BADCFE10.
    /// </summary>
    public static string? PackGuid(string? code)
    {
        if (!Guid.TryParseExact(code?.Trim(), "B", out var guid)) return null;
        var hex = guid.ToString("N").ToUpperInvariant();
        static string Rev(string s) => new(s.Reverse().ToArray());
        static string Pairs(string s)
        {
            var chars = new char[s.Length];
            for (int i = 0; i < s.Length; i += 2) { chars[i] = s[i + 1]; chars[i + 1] = s[i]; }
            return new string(chars);
        }
        return Rev(hex[..8]) + Rev(hex[8..12]) + Rev(hex[12..16]) + Pairs(hex[16..]);
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegQueryInfoKey(Microsoft.Win32.SafeHandles.SafeRegistryHandle key, IntPtr className, IntPtr classLength,
        IntPtr reserved, IntPtr subKeys, IntPtr maxSubKeyLength, IntPtr maxClassLength, IntPtr values, IntPtr maxValueName,
        IntPtr maxValueLength, IntPtr securityDescriptor, out long lastWrite);
}
