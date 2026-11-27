using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace Wipriss.Cleaner;

/// <summary>
/// The copy of registry records made before they are deleted: «.reg» files in the format regedit and
/// «reg import» read (version 5, UTF-16), one for HKCU and one for HKLM, and the XML of deleted tasks.
/// Values are copied exactly (type and bytes, read with RegQueryValueEx).
/// </summary>
internal static class RegFile
{
    public const string UserFile = "hkcu.reg", MachineFile = "hklm.reg", TasksFolder = "tasks", InfoFile = "info.txt";
    private const string Header = "Windows Registry Editor Version 5.00";

    /// <summary>Writes the copy into a new sub-folder of <paramref name="backupRoot"/>; null when there is nothing to copy.</summary>
    public static string? Save(string backupRoot, string programName, PurgeJob job)
    {
        if (job.Keys.Count + job.Values.Count + job.Services.Count + job.Tasks.Count == 0) return null;
        var root = PathText.Normalize(backupRoot);
        if (root is null) return null;
        var safe = new string(programName.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray()).Trim();
        if (safe.Length > 60) safe = safe[..60].Trim();
        var folder = Path.Combine(root, $"{DateTime.Now:yyyy-MM-dd HH-mm-ss} {safe}");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, InfoFile), programName, Encoding.UTF8);

        var user = new StringBuilder(Header + "\r\n");
        var machine = new StringBuilder(Header + "\r\n");
        foreach (var line in job.Keys)
        {
            var parts = line.Split('|');
            if (parts.Length != 2 || RegRoot.Parse(parts[0]) is not { } where) continue;
            using var key = where.Open(parts[1]);
            if (key is not null) WriteKey(where.IsMachine ? machine : user, key, where.Full(parts[1]));
        }
        foreach (var line in job.Values)
        {
            var parts = line.Split('|');
            if (parts.Length != 3 || RegRoot.Parse(parts[0]) is not { } where) continue;
            using var key = where.Open(parts[1]);
            if (key is null || Raw(key, parts[2]) is not { } raw) continue;
            var text = where.IsMachine ? machine : user;
            text.Append("\r\n[").Append(where.Full(parts[1])).Append("]\r\n");
            text.Append(FormatValue(parts[2], raw.Type, raw.Data)).Append("\r\n");
        }
        foreach (var name in job.Services)
        {
            using var key = RegRoot.Machine64.Open(@"SYSTEM\CurrentControlSet\Services\" + name);
            if (key is not null) WriteKey(machine, key, RegRoot.Machine64.Full(@"SYSTEM\CurrentControlSet\Services\" + name));
        }
        if (user.Length > Header.Length + 2) File.WriteAllText(Path.Combine(folder, UserFile), user.ToString(), Encoding.Unicode);
        if (machine.Length > Header.Length + 2) File.WriteAllText(Path.Combine(folder, MachineFile), machine.ToString(), Encoding.Unicode);

        int n = 0;
        foreach (var task in job.Tasks)
        {
            var xml = Query(task);
            if (xml is null) continue;
            var tasks = Directory.CreateDirectory(Path.Combine(folder, TasksFolder)).FullName;
            File.WriteAllText(Path.Combine(tasks, $"{++n}.name"), task, Encoding.UTF8);
            File.WriteAllText(Path.Combine(tasks, $"{n}.xml"), xml, Encoding.Unicode);
        }
        return folder;
    }

    private static void WriteKey(StringBuilder text, RegistryKey key, string fullPath, int depth = 0)
    {
        if (depth > 32) return;
        text.Append("\r\n[").Append(fullPath).Append("]\r\n");
        string[] names;
        try { names = key.GetValueNames(); } catch (Exception ex) when (Reg.Expected(ex)) { names = []; }
        foreach (var name in names)
            if (Raw(key, name) is { } raw) text.Append(FormatValue(name, raw.Type, raw.Data)).Append("\r\n");
        foreach (var sub in Reg.SubKeys(key))
        {
            using var child = Reg.SafeOpen(key, sub);
            if (child is not null) WriteKey(text, child, fullPath + "\\" + sub, depth + 1);
        }
    }

    /// <summary>One line of a .reg file: «"Name"="text"», «@=dword:0000000a», «"Bin"=hex(2):41,00,00,00».</summary>
    internal static string FormatValue(string name, int type, byte[] data)
    {
        var head = name.Length == 0 ? "@" : Quote(name);
        if (type == 1 /* REG_SZ */)
        {
            var text = Encoding.Unicode.GetString(data).TrimEnd('\0');
            if (!text.Contains('\0') && !text.Contains('\r') && !text.Contains('\n')) return $"{head}={Quote(text)}";
        }
        if (type == 4 /* REG_DWORD */ && data.Length == 4)
            return $"{head}=dword:{BitConverter.ToUInt32(data, 0).ToString("x8", CultureInfo.InvariantCulture)}";
        var kind = type == 3 ? "hex" : $"hex({type.ToString("x", CultureInfo.InvariantCulture)})";
        var line = new StringBuilder($"{head}={kind}:");
        int width = line.Length;
        for (int i = 0; i < data.Length; i++)
        {
            var b = data[i].ToString("x2", CultureInfo.InvariantCulture);
            if (i > 0) { line.Append(','); width++; }
            if (width > 76 && i < data.Length) { line.Append("\\\r\n  "); width = 2; }
            line.Append(b);
            width += 2;
        }
        return line.ToString();
    }

    private static string Quote(string text) => "\"" + text.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    private static (int Type, byte[] Data)? Raw(RegistryKey key, string name)
    {
        try
        {
            int size = 0;
            if (RegQueryValueEx(key.Handle, name, IntPtr.Zero, out int type, null, ref size) != 0) return null;
            var data = new byte[size];
            if (RegQueryValueEx(key.Handle, name, IntPtr.Zero, out type, data, ref size) != 0) return null;
            return (type, size == data.Length ? data : data[..size]);
        }
        catch (Exception ex) when (Reg.Expected(ex) || ex is ObjectDisposedException) { return null; }
    }

    private static string? Query(string task)
    {
        if (!Purge.TaskAllowed(task)) return null;
        try
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "schtasks.exe"), $"/Query /TN \"{task}\" /XML")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                StandardOutputEncoding = Encoding.UTF8,
            });
            if (process is null) return null;
            var xml = process.StandardOutput.ReadToEnd();
            process.WaitForExit(30_000);
            return process.ExitCode == 0 && xml.Contains("<Task", StringComparison.Ordinal) ? xml : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException) { return null; }
    }

    // ---------- putting it back ----------

    /// <summary>
    /// Is every key in this .reg file one a program keeps for itself (the same rules as for deleting), with
    /// nothing that deletes («[-…]», «=-»)? Only then is it imported.
    /// </summary>
    internal static bool SafeToImport(string text)
    {
        if (!text.TrimStart('﻿').StartsWith(Header, StringComparison.Ordinal)) return false;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("[-", StringComparison.Ordinal) || line.EndsWith("=-", StringComparison.Ordinal)) return false;
            if (!line.StartsWith('[')) continue;
            if (!line.EndsWith(']')) return false;
            if (!KeyLineAllowed(line[1..^1])) return false;
        }
        return true;
    }

    private static bool KeyLineAllowed(string full)
    {
        RegRoot root;
        string path;
        if (full.StartsWith(@"HKEY_CURRENT_USER\", StringComparison.OrdinalIgnoreCase)) { root = RegRoot.User; path = full[18..]; }
        else if (full.StartsWith(@"HKEY_LOCAL_MACHINE\", StringComparison.OrdinalIgnoreCase)) { root = RegRoot.Machine64; path = full[19..]; }
        else return false;
        if (root.IsMachine && path.StartsWith(@"SYSTEM\CurrentControlSet\Services\", StringComparison.OrdinalIgnoreCase))
        {
            // Only a service that is gone (one deleted with the program), never a change to one that exists.
            var parts = path.Split('\\');
            return parts.Length >= 4 && parts[3].Length > 0 && !path.Contains("..", StringComparison.Ordinal)
                   && !RegRoot.Machine64.Exists(@"SYSTEM\CurrentControlSet\Services\" + parts[3]);
        }
        if (root.IsMachine && path.StartsWith(@"SOFTWARE\WOW6432Node\", StringComparison.OrdinalIgnoreCase)) { root = RegRoot.Machine32; path = @"SOFTWARE\" + path[21..]; }
        // A sub-key of an allowed key, or a value key (autostart).
        var segments = path.Split('\\');
        for (int n = segments.Length; n >= 2; n--)
        {
            var prefix = string.Join('\\', segments.Take(n));
            if (Purge.KeyAllowed(root, prefix)) return true;
            if (n == segments.Length && Purge.ValueAllowed(prefix, "x")) return true;
        }
        return false;
    }

    /// <summary>Imports a checked .reg file with Windows' own «reg import». True when it worked.</summary>
    public static bool Import(string file)
    {
        try { if (!SafeToImport(File.ReadAllText(file))) return false; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
        return Purge.Run("reg.exe", $"import \"{file}\"") == 0;
    }

    /// <summary>Recreates the saved tasks; how many could not be.</summary>
    public static int ImportTasks(string folder)
    {
        var tasks = Path.Combine(folder, TasksFolder);
        if (!Directory.Exists(tasks)) return 0;
        int failed = 0;
        foreach (var nameFile in Directory.EnumerateFiles(tasks, "*.name"))
        {
            var name = File.ReadAllText(nameFile).Trim();
            var xml = Path.ChangeExtension(nameFile, ".xml");
            if (!Purge.TaskAllowed(name) || !File.Exists(xml) || Purge.Run("schtasks.exe", $"/Create /TN \"{name}\" /XML \"{xml}\" /F") != 0) failed++;
        }
        return failed;
    }

    /// <summary>Puts a copy back: HKCU and tasks as the owner, HKLM (and tasks that need it) through the helper. Failed count.</summary>
    public static int Restore(string folder)
    {
        int failed = 0;
        var user = Path.Combine(folder, UserFile);
        if (File.Exists(user) && !Import(user)) failed++;
        bool machine = File.Exists(Path.Combine(folder, MachineFile));
        int tasks = ImportTasks(folder);
        if (machine || tasks > 0)
        {
            int code = Purge.RunHelper($"{Purge.HelperRestore} \"{folder}\"");
            if (code != 0) failed += code is < 0 or Purge.HelperInvalid ? (machine ? 1 : 0) + tasks : code;
        }
        return failed;
    }

    /// <summary>The helper's side of <see cref="Restore"/>: HKLM and tasks, checked again.</summary>
    public static int HelperRestore(string folder)
    {
        var normal = PathText.Normalize(folder);
        if (normal is null || !Directory.Exists(normal)) return Purge.HelperInvalid;
        int failed = 0;
        var machine = Path.Combine(normal, MachineFile);
        if (File.Exists(machine) && !Import(machine)) failed++;
        failed += ImportTasks(normal);
        return failed;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegQueryValueExW")]
    private static extern int RegQueryValueEx(Microsoft.Win32.SafeHandles.SafeRegistryHandle key, string name, IntPtr reserved,
        out int type, byte[]? data, ref int size);
}
