using System.IO;
using Microsoft.Win32;

namespace Wipriss.Core;

/// <summary>Creates the Wipriss folder, moves data from older versions into it, and moves it to a new place.</summary>
public static class DataFolder
{
    private static readonly string[] Parts = ["Служебное", "Скриншоты", "Записи"];

    /// <summary>Run once at startup, before anything reads or writes data.</summary>
    public static void Prepare()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDir);
            Directory.CreateDirectory(AppPaths.ScreenshotsDir);
            MoveLegacyData();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ErrorLog.Write(ex);
        }
    }

    /// <summary>
    /// Moves the whole Wipriss folder to <paramref name="newRoot"/> and remembers the new place.
    /// The app must stop its modules first and restart afterwards, because paths are read at startup.
    /// </summary>
    /// <returns>Null on success, otherwise why it could not be done, in words for the user.</returns>
    public static string? MoveTo(string newRoot)
    {
        if (Check(newRoot) is { } problem) return problem;
        newRoot = Path.GetFullPath(newRoot);
        string oldRoot = Path.GetFullPath(AppPaths.Root);

        try
        {
            Directory.CreateDirectory(newRoot);
            foreach (string part in Parts)
            {
                string from = Path.Combine(oldRoot, part);
                if (Directory.Exists(from)) MoveDirectory(from, Path.Combine(newRoot, part));
            }

            using (var key = Registry.CurrentUser.CreateSubKey(AppPaths.RegistryKey))
                key.SetValue(AppPaths.RegistryValue, newRoot, RegistryValueKind.String);

            TryDeleteIfEmpty(oldRoot);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ErrorLog.Write(ex);
            return "Не получилось перенести: нет доступа к папке или файл занят.";
        }
    }

    /// <summary>Why the folder can't go there, in words for the user, or null if it can.</summary>
    public static string? Check(string newRoot)
    {
        newRoot = Path.GetFullPath(newRoot);
        string oldRoot = Path.GetFullPath(AppPaths.Root);
        if (string.Equals(newRoot.TrimEnd('\\'), oldRoot.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            return "Это та же папка.";
        if (newRoot.StartsWith(oldRoot.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
            return "Новая папка не может лежать внутри старой.";
        if (Path.GetPathRoot(newRoot)?.TrimEnd('\\').Length == newRoot.TrimEnd('\\').Length)
            return "Выберите папку, а не корень диска.";
        if (Directory.Exists(newRoot) && Directory.EnumerateFileSystemEntries(newRoot).Any(e => !Parts.Contains(Path.GetFileName(e))))
            return "Папка не пустая. Выберите пустую или новую папку.";
        return null;
    }

    private static void MoveLegacyData()
    {
        if (File.Exists(AppPaths.LegacySettingsFile) && !File.Exists(AppPaths.SettingsFile))
            File.Move(AppPaths.LegacySettingsFile, AppPaths.SettingsFile);
        TryDeleteIfEmpty(Path.GetDirectoryName(AppPaths.LegacySettingsFile)!);

        if (!Directory.Exists(AppPaths.LegacyDataDir)) return;
        foreach (string entry in Directory.EnumerateFileSystemEntries(AppPaths.LegacyDataDir).ToList())
        {
            string target = Path.Combine(AppPaths.DataDir, Path.GetFileName(entry));
            if (File.Exists(target) || Directory.Exists(target)) continue; // never overwrite newer data
            if (Directory.Exists(entry)) MoveDirectory(entry, target);
            else File.Move(entry, target);
        }

        TryDeleteIfEmpty(AppPaths.LegacyDataDir);
    }

    /// <summary>Directory.Move only works on one drive; across drives copy, then delete.</summary>
    private static void MoveDirectory(string from, string to)
    {
        if (string.Equals(Path.GetPathRoot(from), Path.GetPathRoot(to), StringComparison.OrdinalIgnoreCase) && !Directory.Exists(to))
        {
            Directory.Move(from, to);
            return;
        }

        foreach (string dir in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, dir)));
        Directory.CreateDirectory(to);
        foreach (string file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(to, Path.GetRelativePath(from, file)), overwrite: false);
        Directory.Delete(from, recursive: true);
    }

    private static void TryDeleteIfEmpty(string dir)
    {
        try
        {
            if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
