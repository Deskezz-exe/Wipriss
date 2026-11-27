using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace Wipriss.Cleaner;

/// <summary>One installed program from the uninstall list (or the Store).</summary>
internal sealed record InstalledProgram(string Name, string? Publisher, string NameKey, string PublisherKey);

/// <summary>A path that proves something uses a folder, with the reason in plain words.</summary>
internal sealed record UsedPath(string Path, string Reason);

/// <summary>
/// What is installed and what is really used: the uninstall lists, Store apps, services, running
/// processes, scheduled tasks, autostart, shortcuts, PATH, file associations, game libraries.
/// Everything is read-only. A source that cannot be read is simply missing; if the uninstall list itself
/// cannot be read, <see cref="Complete"/> is false and no leftovers are reported at all.
/// </summary>
internal sealed class Inventory
{
    public List<InstalledProgram> Programs { get; } = [];
    public List<UsedPath> Used { get; } = [];

    /// <summary>Paths of installed programs themselves (uninstall records, Store packages, games), with the program.</summary>
    public List<(string Path, InstalledProgram Program)> ProgramPaths { get; } = [];

    /// <summary>Store package families (Name_PublisherId), lower case.</summary>
    public HashSet<string> StoreFamilies { get; } = new(StringComparer.OrdinalIgnoreCase);

    public bool Complete { get; private set; }
    public bool StoreComplete { get; private set; }

    /// <summary>Roots too wide to count as proof (an install location of «C:\» or the user profile).</summary>
    private readonly HashSet<string> _tooWide = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="skipUninstall">An uninstall record to leave out («HKLM64:{name}»): the program being removed by force.</param>
    public static Inventory Take(string? skipUninstall = null)
    {
        var inventory = new Inventory();
        inventory.FillTooWide();
        inventory.ReadUninstall(skipUninstall);
        inventory.ReadStore();
        inventory.ReadServices();
        inventory.ReadProcesses();
        inventory.ReadAutostart();
        inventory.ReadShortcuts();
        inventory.ReadPath();
        inventory.ReadTasks();
        inventory.ReadAssociations();
        inventory.ReadGameLibraries();
        inventory.Add(AppContext.BaseDirectory, "Здесь установлен сам Wipriss");
        return inventory;
    }

    public bool IsInstalled(string nameKey) =>
        nameKey.Length > 0 && Programs.Any(p => PathText.NamesMatch(nameKey, p.NameKey));

    public InstalledProgram? FindByName(string nameKey) =>
        nameKey.Length == 0 ? null
        : Programs.FirstOrDefault(p => PathText.NamesMatch(nameKey, p.NameKey))
          ?? Programs.FirstOrDefault(p => PathText.NamesMatch(nameKey, p.PublisherKey));

    /// <summary>Proof that something uses <paramref name="folder"/>: a used path inside it, or the folder
    /// inside an install location.</summary>
    public UsedPath? UsedIn(string folder)
    {
        foreach (var used in Used)
            if (PathText.IsInside(used.Path, folder) || PathText.IsInside(folder, used.Path)) return used;
        return null;
    }

    internal void Add(string? path, string reason, InstalledProgram? program = null)
    {
        var normal = PathText.Normalize(path);
        if (normal is null || _tooWide.Contains(normal)) return;
        // A file: the folder it lives in is what is used.
        if (Path.HasExtension(normal) && !Directory.Exists(normal)) normal = Path.GetDirectoryName(normal);
        if (normal is null || normal.Length <= 3 || _tooWide.Contains(normal)) return;
        Used.Add(new UsedPath(normal, reason));
        if (program is not null) ProgramPaths.Add((normal, program));
    }

    private void FillTooWide()
    {
        foreach (var folder in new[]
        {
            Environment.SpecialFolder.Windows, Environment.SpecialFolder.System, Environment.SpecialFolder.SystemX86,
            Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86,
            Environment.SpecialFolder.CommonApplicationData, Environment.SpecialFolder.ApplicationData,
            Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolder.UserProfile,
            Environment.SpecialFolder.CommonProgramFiles, Environment.SpecialFolder.CommonProgramFilesX86,
            Environment.SpecialFolder.Desktop, Environment.SpecialFolder.MyDocuments,
        })
        {
            var path = PathText.Normalize(Environment.GetFolderPath(folder));
            if (path is not null) _tooWide.Add(path);
        }
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        foreach (var extra in new[] { local + "Low", Path.Combine(local, "Programs"), Path.Combine(local, "Temp"),
                                      Path.GetTempPath(), Places.Downloads, Path.GetDirectoryName(local) ?? "",
                                      Path.GetDirectoryName(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)) ?? "",
                                      Path.Combine(local, "Microsoft"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Microsoft"),
                                      Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Microsoft") })
        {
            var path = PathText.Normalize(extra);
            if (path is not null) _tooWide.Add(path);
        }
        foreach (var drive in DriveInfo.GetDrives()) _tooWide.Add(drive.Name.TrimEnd('\\'));
    }

    private void ReadUninstall(string? skip)
    {
        const string key = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";
        int read = 0;
        foreach (var (hive, view) in new[]
        {
            (RegistryHive.LocalMachine, RegistryView.Registry64), (RegistryHive.LocalMachine, RegistryView.Registry32),
            (RegistryHive.CurrentUser, RegistryView.Registry64),
        })
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, view);
                using var list = root.OpenSubKey(key);
                if (list is null) continue;
                read++;
                var code = new RegRoot(hive, view).Code;
                foreach (var name in list.GetSubKeyNames())
                {
                    if (skip is not null && skip == code + ":" + name) continue;
                    using var app = SafeOpen(list, name);
                    if (app is null) continue;
                    var display = app.GetValue("DisplayName") as string;
                    if (string.IsNullOrWhiteSpace(display)) continue;
                    var publisher = app.GetValue("Publisher") as string;
                    var program = new InstalledProgram(display.Trim(), publisher?.Trim(), PathText.NameKey(display), PathText.NameKey(publisher));
                    Programs.Add(program);
                    var reason = $"Установлена программа «{display.Trim()}»";
                    Add(app.GetValue("InstallLocation") as string, reason, program);
                    Add(PathText.ExePath(app.GetValue("DisplayIcon") as string is { } icon ? icon.Split(',')[0] : null), reason, program);
                    Add(PathText.ExePath(app.GetValue("UninstallString") as string), reason, program);
                    Add(PathText.ExePath(app.GetValue("QuietUninstallString") as string), reason, program);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        }
        Complete = read >= 2 && Programs.Count > 0;
    }

    private void ReadStore()
    {
        const string key = @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages";
        try
        {
            using var list = Registry.CurrentUser.OpenSubKey(key);
            if (list is null) return;
            foreach (var fullName in list.GetSubKeyNames())
            {
                // Name_Version_Arch_ResourceId_PublisherId → family Name_PublisherId
                var parts = fullName.Split('_');
                if (parts.Length < 5) continue;
                StoreFamilies.Add(parts[0] + "_" + parts[^1]);
                using var package = SafeOpen(list, fullName);
                var display = package?.GetValue("DisplayName") as string;
                var app = new InstalledProgram(!string.IsNullOrWhiteSpace(display) && !display.StartsWith('@') ? display : parts[0], null, PathText.NameKey(display), "");
                if (!string.IsNullOrWhiteSpace(display) && !display.StartsWith('@')) Programs.Add(app);
                Add(package?.GetValue("PackageRootFolder") as string, "Установлено приложение из Microsoft Store", app);
            }
            StoreComplete = StoreFamilies.Count > 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
    }

    private void ReadServices()
    {
        try
        {
            using var services = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services");
            if (services is null) return;
            foreach (var name in services.GetSubKeyNames())
            {
                using var service = SafeOpen(services, name);
                if (service is null) continue;
                var reason = $"Служба или драйвер «{name}» запускает файл отсюда";
                Add(PathText.ExePath(service.GetValue("ImagePath") as string), reason);
                using var parameters = SafeOpen(service, "Parameters");
                Add(PathText.ExePath(parameters?.GetValue("ServiceDll") as string), reason);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
    }

    private void ReadProcesses()
    {
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                var path = ProcessPath(process.Id);
                if (path is not null) Add(path, $"Сейчас работает программа {Path.GetFileName(path)}");
            }
        }
    }

    private void ReadAutostart()
    {
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
                foreach (var key in new[] { @"Software\Microsoft\Windows\CurrentVersion\Run", @"Software\Microsoft\Windows\CurrentVersion\RunOnce" })
                {
                    try
                    {
                        using var root = RegistryKey.OpenBaseKey(hive, view);
                        using var run = root.OpenSubKey(key);
                        if (run is null) continue;
                        foreach (var name in run.GetValueNames())
                            Add(PathText.ExePath(run.GetValue(name) as string), $"Запускается вместе с Windows («{name}»)");
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
                }
        foreach (var folder in new[] { Environment.SpecialFolder.Startup, Environment.SpecialFolder.CommonStartup })
            ReadShortcutsIn(Environment.GetFolderPath(folder), "Ярлык в автозагрузке ведёт сюда", recurse: false);
    }

    private void ReadShortcuts()
    {
        ReadShortcutsIn(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Ярлык в меню «Пуск» ведёт сюда", true);
        ReadShortcutsIn(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), "Ярлык в меню «Пуск» ведёт сюда", true);
        ReadShortcutsIn(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Ярлык на рабочем столе ведёт сюда", false);
        ReadShortcutsIn(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), "Ярлык на рабочем столе ведёт сюда", false);
        var taskbar = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            @"Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar");
        ReadShortcutsIn(taskbar, "Значок на панели задач ведёт сюда", false);
    }

    private void ReadShortcutsIn(string folder, string reason, bool recurse)
    {
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return;
        try
        {
            var options = new EnumerationOptions { RecurseSubdirectories = recurse, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
            foreach (var file in Directory.EnumerateFiles(folder, "*.lnk", options))
            {
                try
                {
                    var info = new FileInfo(file);
                    if (info.Length > 64 * 1024) continue;
                    Add(PathText.ShortcutTarget(File.ReadAllBytes(file)), reason);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private void ReadPath()
    {
        var all = new List<string?>
        {
            Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine),
            Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User),
            Environment.GetEnvironmentVariable("PATH"),
        };
        foreach (var value in all)
            foreach (var part in (value ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                AddFolder(part, "Папка прописана в PATH: ей пользуются другие программы и командная строка");
    }

    /// <summary>A folder (not a file), even if its name has a dot («node-v20.1»).</summary>
    private void AddFolder(string? path, string reason)
    {
        var normal = PathText.Normalize(path);
        if (normal is null || normal.Length <= 3 || _tooWide.Contains(normal)) return;
        Used.Add(new UsedPath(normal, reason));
    }

    private void ReadTasks()
    {
        // The task scheduler's cache in the registry holds every task's actions; it is readable without
        // administrator rights (unlike the XML files in System32\Tasks).
        try
        {
            using var tasks = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Schedule\TaskCache\Tasks");
            if (tasks is null) return;
            foreach (var id in tasks.GetSubKeyNames())
            {
                using var task = SafeOpen(tasks, id);
                if (task?.GetValue("Actions") is not byte[] actions) continue;
                var name = task.GetValue("Path") as string ?? "";
                var text = Encoding.Unicode.GetString(actions);
                foreach (System.Text.RegularExpressions.Match match in PathText.PathInText().Matches(text))
                    Add(match.Value, $"Задача планировщика «{name.TrimStart('\\')}» запускает файл отсюда");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
    }

    private void ReadAssociations()
    {
        foreach (var root in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            try
            {
                using var classes = root.OpenSubKey(@"Software\Classes");
                if (classes is null) continue;
                foreach (var name in classes.GetSubKeyNames())
                {
                    if (name.StartsWith('.')) continue;
                    using var command = SafeOpen(classes, name + @"\shell\open\command");
                    if (command is null) continue;
                    Add(PathText.ExePath(command.GetValue(null) as string), $"Открывает файлы (тип «{name}»)");
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        }
    }

    private void ReadGameLibraries()
    {
        // Steam: every library folder and every installed game.
        try
        {
            using var steam = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            var steamPath = PathText.Normalize((steam?.GetValue("SteamPath") as string)?.Replace('/', '\\'));
            if (steamPath is not null)
            {
                AddFolder(steamPath, "Здесь установлен Steam");
                var vdf = Path.Combine(steamPath, @"steamapps\libraryfolders.vdf");
                if (File.Exists(vdf))
                    foreach (var line in File.ReadLines(vdf))
                    {
                        var trimmed = line.Trim();
                        if (!trimmed.StartsWith("\"path\"", StringComparison.OrdinalIgnoreCase)) continue;
                        var value = trimmed[6..].Trim().Trim('"').Replace(@"\\", @"\");
                        AddFolder(value, "Библиотека игр Steam");
                    }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }

        // Epic Games: one manifest per installed game.
        try
        {
            var manifests = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                @"Epic\EpicGamesLauncher\Data\Manifests");
            if (Directory.Exists(manifests))
                foreach (var file in Directory.EnumerateFiles(manifests, "*.item"))
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(file));
                    if (doc.RootElement.TryGetProperty("InstallLocation", out var location) && location.ValueKind == JsonValueKind.String)
                    {
                        var game = doc.RootElement.TryGetProperty("DisplayName", out var dn) ? dn.GetString() : null;
                        AddFolder(location.GetString(), $"Установлена игра Epic Games «{game}»");
                        if (PathText.Normalize(location.GetString()) is { } where && !string.IsNullOrWhiteSpace(game))
                            ProgramPaths.Add((where, new InstalledProgram(game, "Epic Games", PathText.NameKey(game), "epicgames")));
                        if (!string.IsNullOrWhiteSpace(game))
                            Programs.Add(new InstalledProgram(game, "Epic Games", PathText.NameKey(game), "epicgames"));
                    }
                }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
    }

    private static RegistryKey? SafeOpen(RegistryKey parent, string name)
    {
        try { return parent.OpenSubKey(name); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return null; }
    }

    private static string? ProcessPath(int pid)
    {
        const int QueryLimitedInformation = 0x1000;
        var handle = OpenProcess(QueryLimitedInformation, false, pid);
        if (handle == IntPtr.Zero) return null;
        try
        {
            var buffer = new StringBuilder(1024);
            int size = buffer.Capacity;
            return QueryFullProcessImageName(handle, 0, buffer, ref size) ? buffer.ToString(0, size) : null;
        }
        finally { CloseHandle(handle); }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(int access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name, ref int size);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
}
