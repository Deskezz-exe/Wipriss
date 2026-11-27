using System.IO;
using System.Text;
using Microsoft.Win32;

namespace Wipriss.Cleaner;

/// <summary>What is known about a program before it is removed: its names, its folder, its programs.</summary>
internal sealed class ProgramSnapshot
{
    public required ProgramRecord Program { get; init; }
    public string NameKey => Program.NameKey;
    public string PublisherKey => Program.PublisherKey;
    public string? Folder => Program.Folder;
    /// <summary>File names of its programs («foo.exe»), only distinctive ones.</summary>
    public HashSet<string> Exes { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>The uninstall record («HKLM64:{name}»), to leave out of the inventory when it is removed by force.</summary>
    public string? RecordCode => Program.SubKey is null ? null : Program.Root.Code + ":" + Program.SubKey;

    /// <summary>Names every installer uses: they prove nothing about whose folder it is.</summary>
    private static readonly HashSet<string> Generic = new(StringComparer.OrdinalIgnoreCase)
    {
        "uninstall.exe", "uninst.exe", "unins000.exe", "unins001.exe", "uninstaller.exe", "setup.exe", "install.exe", "installer.exe",
        "update.exe", "updater.exe", "launcher.exe", "crashreporter.exe", "crashpad_handler.exe", "helper.exe", "elevate.exe",
        "maintenancetool.exe", "notification_helper.exe", "app.exe", "main.exe", "start.exe", "run.exe", "service.exe", "server.exe",
        "7z.exe", "python.exe", "pythonw.exe", "java.exe", "javaw.exe", "node.exe", "git.exe", "ffmpeg.exe", "ffprobe.exe",
        "vc_redist.x64.exe", "vc_redist.x86.exe", "dxsetup.exe", "unitycrashhandler64.exe", "unitycrashhandler32.exe",
        "cefsharp.browsersubprocess.exe", "qtwebengineprocess.exe", "squirrel.exe",
    };

    public static ProgramSnapshot Take(ProgramRecord program)
    {
        var snapshot = new ProgramSnapshot { Program = program };
        if (program.Entry.IconPath is { } icon && icon.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) snapshot.AddExe(icon);
        if (program.Folder is { } folder)
        {
            try
            {
                var options = new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 2, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
                foreach (var exe in Directory.EnumerateFiles(folder, "*.exe", options).Take(80)) snapshot.AddExe(exe);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return snapshot;
    }

    private void AddExe(string path)
    {
        var name = Path.GetFileName(path);
        if (name.Length >= 6 && !Generic.Contains(name) && !name.StartsWith("unins", StringComparison.OrdinalIgnoreCase)) Exes.Add(name);
    }

    public bool InFolder(string? path) => path is not null && Folder is not null && PathText.IsInside(path, Folder);

    /// <summary>A command or path that points into the program's folder, or to one of its programs that is gone.</summary>
    public bool PointsHere(string? path)
    {
        var normal = PathText.Normalize(path);
        if (normal is null) return false;
        if (InFolder(normal)) return true;
        return Exes.Contains(Path.GetFileName(normal)) && !File.Exists(normal);
    }
}

/// <summary>
/// What a removed program left: folders named like it (or its publisher, or with its programs inside) in
/// Program Files, ProgramData, AppData, Documents and the Start menu; shortcuts to it; registry keys named
/// like it; records that point into its old folder (CLSID, TypeLib, AppID, App Paths, autostart, file
/// types, services, scheduled tasks); and with a forced removal its own uninstall and Windows Installer
/// records. Nothing another installed program uses is offered. Read-only.
/// </summary>
internal static class Tails
{
    private sealed record DiskRoot(string Path, string[] Skip, bool Documents = false);

    private static IEnumerable<DiskRoot> DiskRoots()
    {
        string[] none = [];
        yield return new(Places.ProgramFiles, none);
        yield return new(Places.ProgramFilesX86, none);
        foreach (var drive in Places.OtherDrives)
        {
            yield return new(Path.Combine(drive, "Program Files"), none);
            yield return new(Path.Combine(drive, "Program Files (x86)"), none);
        }
        yield return new(Path.Combine(Places.Local, "Programs"), ["Common"]);
        yield return new(Places.ProgramData, Leftovers.AppDataSkip);
        yield return new(Places.Roaming, Leftovers.AppDataSkip);
        yield return new(Places.Local, Leftovers.AppDataSkip);
        yield return new(Places.LocalLow, Leftovers.AppDataSkip);
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (documents.Length > 0)
        {
            yield return new(documents, ["My Games", "My Music", "My Pictures", "My Videos", "Wipriss"], Documents: true);
            yield return new(Path.Combine(documents, "My Games"), none, Documents: true);
        }
        yield return new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs"), ["Startup", "Accessibility", "Accessories", "Administrative Tools", "System Tools", "Maintenance"]);
        yield return new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), "Programs"), ["Startup", "Accessibility", "Accessories", "Administrative Tools", "System Tools", "Maintenance"]);
    }

    public static List<TailItem> Find(ProgramSnapshot snapshot, bool forced, Action<string, int> progress, CancellationToken cancel)
    {
        var inventory = Inventory.Take(forced ? snapshot.RecordCode : null);
        var others = inventory.Programs.Where(p => !IsThis(p, snapshot)).ToList();
        var found = new List<TailItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        progress("Ищу папки и ярлыки", 10);
        foreach (var tail in DiskTails(snapshot, inventory, others, cancel))
            if (seen.Add(tail.Id)) found.Add(tail);
        cancel.ThrowIfCancellationRequested();

        progress("Ищу записи в реестре", 55);
        foreach (var tail in RegistryTails(snapshot, others, forced, cancel))
            if (seen.Add(tail.Id)) found.Add(tail);
        progress("Готово", 100);
        // A shortcut inside a folder that is offered anyway is not shown twice.
        var folders = found.Where(t => t.Kind == TailKind.Folder).Select(t => t.Title).ToList();
        found.RemoveAll(t => t.Kind == TailKind.File && folders.Any(f => PathText.IsInside(t.Title, f)));
        return found;
    }

    /// <summary>
    /// Is a folder or key with this name the program's? Stricter than the leftover search: the name is the
    /// program's, contains it («obs-studio-portable»), or is most of it («GeForce Experience» of «NVIDIA
    /// GeForce Experience», but not «Steam» of «Steam Audio»).
    /// </summary>
    internal static bool NameFits(string key, string programKey)
    {
        if (key.Length < 3 || programKey.Length < 3) return false;
        if (key == programKey) return true;
        if (programKey.Length >= 4 && key.Contains(programKey, StringComparison.Ordinal)) return true;
        return key.Length >= 4 && programKey.Contains(key, StringComparison.Ordinal) && key.Length * 10 >= programKey.Length * 7;
    }

    /// <summary>Another installed program the name fits at least as well as ours (a longer, more exact name).</summary>
    private static InstalledProgram? LooksLikeOther(string key, List<InstalledProgram> others, string programKey) =>
        others.FirstOrDefault(p => p.NameKey.Length >= programKey.Length && NameFits(key, p.NameKey));

    private static bool IsThis(InstalledProgram p, ProgramSnapshot s) =>
        p.NameKey == s.NameKey && string.Equals(p.Name, s.Program.Entry.Name, StringComparison.OrdinalIgnoreCase);

    // ---------- disks ----------

    private static IEnumerable<TailItem> DiskTails(ProgramSnapshot s, Inventory inventory, List<InstalledProgram> others, CancellationToken cancel)
    {
        var candidates = new List<(string Path, string Reason, bool Sure, bool Documents)>();
        if (s.Folder is { } folder && Directory.Exists(folder)) candidates.Add((folder, "Сюда была установлена программа", true, false));

        bool publisherAlone = s.PublisherKey.Length >= 4 && !others.Any(p => PathText.NamesMatch(p.PublisherKey, s.PublisherKey));
        foreach (var root in DiskRoots())
        {
            cancel.ThrowIfCancellationRequested();
            foreach (var child in Finds.Children(root.Path))
            {
                var name = Path.GetFileName(child);
                if (SkipName(name, root.Skip)) continue;
                var key = PathText.NameKey(name);
                if (NameFits(key, s.NameKey))
                {
                    candidates.Add((child, "Папка названа как программа", true, root.Documents));
                    continue;
                }
                if (s.PublisherKey.Length >= 4 && key.Length >= 4 && PathText.NamesMatch(key, s.PublisherKey))
                {
                    // The publisher's folder: what inside is named like the program; the whole folder when
                    // nothing else of this publisher is installed and nothing else is inside.
                    var inside = Finds.Children(child).ToList();
                    var mine = inside.Where(c => NameFits(PathText.NameKey(Path.GetFileName(c)), s.NameKey)).ToList();
                    if (publisherAlone && inside.Count == mine.Count && !HasLooseFiles(child))
                        candidates.Add((child, "Папка издателя программы, других его программ нет", inside.Count > 0, root.Documents));
                    else
                        candidates.AddRange(mine.Select(c => (c, "В папке издателя, названа как программа", true, root.Documents)));
                    continue;
                }
                if (s.Exes.Count > 0 && HasExe(child, s.Exes) is { } exe)
                    candidates.Add((child, $"Внутри {exe}: такой файл был у программы", false, root.Documents));
            }
        }

        foreach (var (path, reason, sure, documents) in candidates)
        {
            cancel.ThrowIfCancellationRequested();
            if (!Purge.Offerable(path) || IsLink(path)) continue;
            if (!s.InFolder(path) && candidates.Any(c => c.Path != path && PathText.IsInside(path, c.Path))) continue; // inside another candidate
            if (UsedByOther(path, inventory, s) is { } user) { _ = user; continue; }
            yield return FolderTail(path, reason, sure, documents, others, s.NameKey);
        }

        foreach (var shortcut in Shortcuts(s)) yield return shortcut;
    }

    private static bool SkipName(string name, string[] skip) =>
        name.StartsWith('.') || name.StartsWith('$') || name.StartsWith('{')
        || name.StartsWith("Windows", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Package Cache", StringComparison.OrdinalIgnoreCase) || name.Equals("Wipriss", StringComparison.OrdinalIgnoreCase)
        || skip.Contains(name, StringComparer.OrdinalIgnoreCase);

    private static bool HasLooseFiles(string folder)
    {
        try { return Directory.EnumerateFiles(folder).Any(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return true; }
    }

    private static string? HasExe(string folder, HashSet<string> exes)
    {
        try
        {
            var options = new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 1, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
            foreach (var file in Directory.EnumerateFiles(folder, "*.exe", options).Take(200))
                if (exes.Contains(Path.GetFileName(file))) return Path.GetFileName(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return null;
    }

    private static bool IsLink(string path)
    {
        try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return true; }
    }

    /// <summary>Another installed program lives there (or inside it).</summary>
    private static string? UsedByOther(string path, Inventory inventory, ProgramSnapshot s)
    {
        foreach (var (used, program) in inventory.ProgramPaths)
        {
            if (IsThis(program, s) || s.InFolder(used)) continue;
            if (PathText.IsInside(used, path) || PathText.IsInside(path, used)) return program.Name;
        }
        if (PathText.Normalize(AppContext.BaseDirectory) is { } self && (PathText.IsInside(self, path) || PathText.IsInside(path, self))) return "Wipriss";
        return null;
    }

    private static TailItem FolderTail(string path, string reason, bool sure, bool documents, List<InstalledProgram> others, string programKey)
    {
        var tail = new TailItem { Id = "F|" + path, Kind = TailKind.Folder, Title = path, NeedsAdmin = NeedsAdmin(path) };
        tail.Evidence.Add(reason);

        // Its contents, each deletable on its own (when there are a few).
        List<string> entries;
        try { entries = Directory.EnumerateFileSystemEntries(path).Take(41).ToList(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { entries = []; }
        var total = new FolderStats();
        if (entries.Count is >= 2 and <= 40)
        {
            foreach (var entry in entries.OrderBy(e => e, StringComparer.OrdinalIgnoreCase))
            {
                if (IsLink(entry) || Deleter.IsForbidden(entry)) continue;
                bool isFolder = Directory.Exists(entry);
                var stats = FolderStats.Measure(entry);
                if (!stats.Exists) continue;
                var child = new TailItem
                {
                    Id = (isFolder ? "F|" : "f|") + entry,
                    Kind = isFolder ? TailKind.Folder : TailKind.File,
                    Title = Path.GetFileName(entry),
                    Size = stats.Size,
                    Files = stats.Files,
                    NeedsAdmin = tail.NeedsAdmin,
                };
                Colour(child, stats, sure, documents, path, others, programKey);
                tail.Children.Add(child);
                total.Size += stats.Size;
                total.Files += stats.Files;
                total.PersonalFiles += stats.PersonalFiles;
                total.LooksLikeSaves |= stats.LooksLikeSaves;
                total.Denied |= stats.Denied;
            }
            total.Exists = true;
        }
        else total = FolderStats.Measure(path);

        tail.Size = total.Size;
        tail.Files = total.Files;
        Colour(tail, total, sure, documents, path, others, programKey);
        if (tail.Children.Any(c => c.Color == CleanColor.Yellow) && tail.Color == CleanColor.Green) tail.Color = CleanColor.Yellow;
        return tail;
    }

    private static void Colour(TailItem tail, FolderStats stats, bool sure, bool documents, string path, List<InstalledProgram> others, string programKey)
    {
        var yellow = new List<string>();
        if (stats.PersonalFiles > 0) yellow.Add($"Внутри ваши файлы (документы, фото, видео): {stats.PersonalFiles}");
        if (stats.LooksLikeSaves) yellow.Add("Похоже на сохранения игры");
        if (documents) yellow.Add("Папка в «Документах»: там бывают ваши файлы");
        if (!sure) yellow.Add("Совпало не имя, а файлы внутри: посмотрите, что это");
        if (stats.Denied) yellow.Add("Не ко всем файлам есть доступ");
        if (LooksLikeOther(PathText.NameKey(Path.GetFileName(path)), others, programKey) is { } other)
            yellow.Add($"Похоже и на установленную программу «{other.Name}»");
        if (stats.Files == 0 && !stats.Denied && tail.Children.Count == 0) tail.Evidence.Add("Папка пустая");
        tail.Evidence.AddRange(yellow);
        tail.Color = yellow.Count == 0 ? CleanColor.Green : CleanColor.Yellow;
    }

    private static bool NeedsAdmin(string path) =>
        !PathText.IsInside(path, Places.Profile)
        && (PathText.IsInside(path, Places.ProgramFiles) || PathText.IsInside(path, Places.ProgramFilesX86)
            || PathText.IsInside(path, Places.ProgramData) || Places.OtherDrives.Any(d => path.StartsWith(d, StringComparison.OrdinalIgnoreCase)));

    /// <summary>Shortcuts (Start menu, desktop, taskbar, autostart) that lead to the program.</summary>
    private static IEnumerable<TailItem> Shortcuts(ProgramSnapshot s)
    {
        var places = new (string Folder, bool Recurse)[]
        {
            (Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), true),
            (Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), true),
            (Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), false),
            (Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), false),
            (Path.Combine(Places.Roaming, @"Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar"), false),
            (Path.Combine(Places.Roaming, @"Microsoft\Internet Explorer\Quick Launch"), false),
        };
        foreach (var (folder, recurse) in places)
        {
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) continue;
            List<string> files;
            try
            {
                files = Directory.EnumerateFiles(folder, "*.lnk", new EnumerationOptions { RecurseSubdirectories = recurse, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            foreach (var file in files)
            {
                string? target;
                try { target = new FileInfo(file).Length > 64 * 1024 ? null : PathText.ShortcutTarget(File.ReadAllBytes(file)); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
                if (!s.PointsHere(target) || !Purge.Offerable(file)) continue;
                yield return new TailItem
                {
                    Id = "f|" + file,
                    Kind = TailKind.File,
                    Title = file,
                    Size = SafeLength(file),
                    Files = 1,
                    Color = CleanColor.Green,
                    Evidence = [$"Ярлык ведёт в папку программы ({target})"],
                    NeedsAdmin = !PathText.IsInside(file, Places.Profile),
                };
            }
        }
    }

    private static long SafeLength(string file)
    {
        try { return new FileInfo(file).Length; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return 0; }
    }

    // ---------- registry ----------

    private static readonly string[] SoftwareSkip =
    [
        "Classes", "Clients", "Policies", "RegisteredApplications", "WOW6432Node", "Wow6432Node", "Intel", "ODBC", "Khronos",
        "Setup", "DefaultUserEnvironment", "Partner", "Description", "Wipriss", "AppDataLow", "System", "Chromium",
    ];

    private static IEnumerable<TailItem> RegistryTails(ProgramSnapshot s, List<InstalledProgram> others, bool forced, CancellationToken cancel)
    {
        bool publisherAlone = s.PublisherKey.Length >= 4 && !others.Any(p => PathText.NamesMatch(p.PublisherKey, s.PublisherKey));

        // Keys named like the program (its settings), in HKCU and both views of HKLM.
        foreach (var root in RegRoot.All)
        {
            using var software = root.Open("Software");
            foreach (var name in Reg.SubKeys(software))
            {
                if (SkipName(name, SoftwareSkip)) continue;
                var key = PathText.NameKey(name);
                if (NameFits(key, s.NameKey))
                {
                    yield return KeyTail(root, "Software\\" + name, "Раздел назван как программа", others, key, s.NameKey);
                    continue;
                }
                if (s.PublisherKey.Length < 4 || key.Length < 4 || !PathText.NamesMatch(key, s.PublisherKey)) continue;
                using var publisher = Reg.SafeOpen(software!, name);
                var inside = Reg.SubKeys(publisher);
                var mine = inside.Where(n => NameFits(PathText.NameKey(n), s.NameKey)).ToList();
                if (publisherAlone && mine.Count == inside.Length && inside.Length > 0)
                    yield return KeyTail(root, "Software\\" + name, "Раздел издателя программы, других его программ нет", others, key, s.NameKey);
                else
                    foreach (var n in mine) yield return KeyTail(root, $"Software\\{name}\\{n}", "В разделе издателя, назван как программа", others, PathText.NameKey(n), s.NameKey);
            }
        }
        cancel.ThrowIfCancellationRequested();

        // Its own records, when it is removed by force.
        if (forced && s.Program.SubKey is { } sub && s.Program.Root.Exists(ProgramList.UninstallPath + "\\" + sub))
            yield return Sure(TailKind.RegistryKey, s.Program.Root, ProgramList.UninstallPath + "\\" + sub, "Запись в списке установленных программ");
        if (forced && Reg.PackGuid(s.Program.ProductCode) is { } packed)
        {
            foreach (var (root, path) in new[]
            {
                (RegRoot.Machine64, @"SOFTWARE\Classes\Installer\Products\" + packed),
                (RegRoot.Machine64, @"SOFTWARE\Classes\Installer\Features\" + packed),
                (RegRoot.Machine64, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Installer\UserData\S-1-5-18\Products\" + packed),
                (RegRoot.User, @"Software\Microsoft\Installer\Products\" + packed),
                (RegRoot.User, @"Software\Microsoft\Installer\Features\" + packed),
            })
                if (root.Exists(path)) yield return Sure(TailKind.RegistryKey, root, path, "Запись установщика Windows (MSI) об этой программе");
        }

        // App Paths: «foo.exe» typed in Win+R.
        foreach (var root in RegRoot.All)
        {
            const string appPaths = @"Software\Microsoft\Windows\CurrentVersion\App Paths";
            using var list = root.Open(appPaths);
            foreach (var name in Reg.SubKeys(list))
            {
                using var app = Reg.SafeOpen(list!, name);
                var target = PathText.ExePath(Reg.Text(app, null)) ?? Reg.Text(app, null);
                if (s.PointsHere(target) || (s.Exes.Contains(name) && !File.Exists(PathText.Normalize(target) ?? "")))
                    yield return Sure(TailKind.RegistryKey, root, appPaths + "\\" + name, $"Запуск «{name}» через Win+R ведёт в папку программы");
            }
        }

        // Autostart values (and the «turned off» marks Windows keeps for them).
        foreach (var root in RegRoot.All)
            foreach (var run in new[] { @"Software\Microsoft\Windows\CurrentVersion\Run", @"Software\Microsoft\Windows\CurrentVersion\RunOnce" })
            {
                using var key = root.Open(run);
                if (key is null) continue;
                string[] names;
                try { names = key.GetValueNames(); } catch (Exception ex) when (Reg.Expected(ex)) { continue; }
                foreach (var name in names)
                {
                    if (!s.PointsHere(PathText.ExePath(Reg.Text(key, name)))) continue;
                    yield return Value(root, run, name, "Автозапуск программы, которой больше нет");
                    foreach (var approved in new[] { @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run", @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run32" })
                    {
                        using var marks = root.Open(approved);
                        if (marks?.GetValue(name) is not null) yield return Value(root, approved, name, "Отметка Windows для этого автозапуска");
                    }
                }
            }
        cancel.ThrowIfCancellationRequested();

        // Classes: COM objects, type libraries, AppIDs and file types that point into the old folder.
        foreach (var tail in ClassTails(s, cancel)) yield return tail;

        // Services and scheduled tasks that start a file from the old folder.
        using (var services = RegRoot.Machine64.Open(@"SYSTEM\CurrentControlSet\Services"))
        {
            foreach (var name in Reg.SubKeys(services))
            {
                using var service = Reg.SafeOpen(services!, name);
                using var parameters = service is null ? null : Reg.SafeOpen(service, "Parameters");
                var image = PathText.ExePath(Reg.Text(service, "ImagePath")) ?? PathText.ExePath(Reg.Text(parameters, "ServiceDll"));
                if (image is null || PathText.IsInside(image, Places.Windows) || !s.PointsHere(image)) continue;
                var display = Reg.Text(service, "DisplayName");
                yield return new TailItem
                {
                    Id = "S|" + name,
                    Kind = TailKind.Service,
                    Title = display is null || display.StartsWith('@') ? $"Служба {name}" : $"Служба «{display}» ({name})",
                    Color = CleanColor.Green,
                    Evidence = [$"Запускает файл из папки программы ({image})", "Будет остановлена и удалена"],
                    NeedsAdmin = true,
                };
            }
        }
        foreach (var tail in TaskTails(s)) yield return tail;
    }

    private static TailItem KeyTail(RegRoot root, string path, string reason, List<InstalledProgram> others, string key, string programKey)
    {
        var tail = Sure(TailKind.RegistryKey, root, path, reason);
        if (LooksLikeOther(key, others, programKey) is { } other)
        {
            tail.Color = CleanColor.Yellow;
            tail.Evidence.Add($"Похоже и на установленную программу «{other.Name}»");
        }
        return tail;
    }

    private static TailItem Sure(TailKind kind, RegRoot root, string path, string reason) => new()
    {
        Id = $"K|{root.Code}|{path}",
        Kind = kind,
        Title = root.Short(path),
        Color = CleanColor.Green,
        Evidence = [reason],
        NeedsAdmin = root.IsMachine,
    };

    private static TailItem Value(RegRoot root, string key, string name, string reason) => new()
    {
        Id = $"V|{root.Code}|{key}|{name}",
        Kind = TailKind.RegistryValue,
        Title = $"{root.Short(key)} → «{name}»",
        Color = CleanColor.Green,
        Evidence = [reason],
        NeedsAdmin = root.IsMachine,
    };

    private static IEnumerable<TailItem> ClassTails(ProgramSnapshot s, CancellationToken cancel)
    {
        if (s.Folder is null && s.Exes.Count == 0) yield break;
        var stems = s.Exes.Select(Path.GetFileNameWithoutExtension).OfType<string>().ToList();
        foreach (var (root, classes) in new[] { (RegRoot.User, @"Software\Classes"), (RegRoot.Machine64, @"SOFTWARE\Classes"), (RegRoot.Machine32, @"SOFTWARE\Classes") })
        {
            cancel.ThrowIfCancellationRequested();
            using var top = root.Open(classes);
            if (top is null) continue;

            bool redirectedOnly = root == RegRoot.Machine32; // in the 32-bit view only CLSID differs; the rest is shared
            if (s.Folder is not null)
            {
                using (var clsid = Reg.SafeOpen(top, "CLSID"))
                    foreach (var id in Reg.SubKeys(clsid))
                    {
                        using var entry = Reg.SafeOpen(clsid!, id);
                        if (entry is null) continue;
                        foreach (var server in new[] { "InprocServer32", "LocalServer32" })
                        {
                            using var where = Reg.SafeOpen(entry, server);
                            var file = PathText.ExePath(Reg.Text(where, null)) ?? PathText.Normalize(Reg.Text(where, null));
                            if (!s.InFolder(file)) continue;
                            yield return Sure(TailKind.RegistryKey, root, $"{classes}\\CLSID\\{id}", $"Объект COM из папки программы ({Path.GetFileName(file)})");
                            break;
                        }
                    }
                if (redirectedOnly) continue;
                using (var typelib = Reg.SafeOpen(top, "TypeLib"))
                    foreach (var id in Reg.SubKeys(typelib))
                        if (TypeLibPointsHere(typelib!, id, s))
                            yield return Sure(TailKind.RegistryKey, root, $"{classes}\\TypeLib\\{id}", "Библиотека типов из папки программы");
            }

            if (redirectedOnly) continue;
            using (var appid = Reg.SafeOpen(top, "AppID"))
                foreach (var name in Reg.SubKeys(appid))
                {
                    if (!s.Exes.Contains(name)) continue;
                    yield return Sure(TailKind.RegistryKey, root, $"{classes}\\AppID\\{name}", $"Запись COM о программе {name}");
                    using var entry = Reg.SafeOpen(appid!, name);
                    if (Reg.Text(entry, "AppID") is { } guid && Guid.TryParseExact(guid, "B", out _) && root.Exists($"{classes}\\AppID\\{guid}"))
                        yield return Sure(TailKind.RegistryKey, root, $"{classes}\\AppID\\{guid}", $"Запись COM о программе {name}");
                }

            // File types and links («Foo.Document», «foo»): only those named like the program or its programs.
            if (s.Folder is null) continue;
            foreach (var name in Reg.SubKeys(top))
            {
                if (name.StartsWith('.') || Purge.SystemClasses.Contains(name) || name.StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase)
                    || name.StartsWith("Windows", StringComparison.OrdinalIgnoreCase)) continue;
                var key = PathText.NameKey(name);
                bool named = (key.Length >= 4 && key.Contains(s.NameKey.Length >= 4 ? s.NameKey : "\0", StringComparison.Ordinal))
                             || stems.Any(stem => name.Equals(stem, StringComparison.OrdinalIgnoreCase) || name.StartsWith(stem + ".", StringComparison.OrdinalIgnoreCase));
                if (!named) continue;
                using var command = Reg.SafeOpen(top, name + @"\shell\open\command");
                using var icon = Reg.SafeOpen(top, name + @"\DefaultIcon");
                var target = PathText.ExePath(Reg.Text(command, null)) ?? PathText.ExePath(Reg.Text(icon, null)?.Split(',')[0]);
                if (s.InFolder(target))
                    yield return Sure(TailKind.RegistryKey, root, $"{classes}\\{name}", "Тип файлов или ссылок, который открывала программа");
            }
        }
    }

    private static bool TypeLibPointsHere(RegistryKey typelib, string id, ProgramSnapshot s)
    {
        using var lib = Reg.SafeOpen(typelib, id);
        foreach (var version in Reg.SubKeys(lib))
        {
            using var v = Reg.SafeOpen(lib!, version);
            foreach (var lcid in Reg.SubKeys(v))
            {
                using var l = Reg.SafeOpen(v!, lcid);
                foreach (var platform in new[] { "win32", "win64" })
                {
                    using var p = l is null ? null : Reg.SafeOpen(l, platform);
                    if (s.InFolder(PathText.Normalize(Reg.Text(p, null)))) return true;
                }
            }
        }
        return false;
    }

    private static IEnumerable<TailItem> TaskTails(ProgramSnapshot s)
    {
        using var tasks = RegRoot.Machine64.Open(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Schedule\TaskCache\Tasks");
        foreach (var id in Reg.SubKeys(tasks))
        {
            using var task = Reg.SafeOpen(tasks!, id);
            if (task?.GetValue("Actions") is not byte[] actions) continue;
            var path = Reg.Text(task, "Path");
            if (string.IsNullOrEmpty(path) || !Purge.TaskAllowed(path)) continue;
            var text = Encoding.Unicode.GetString(actions);
            var file = PathText.PathInText().Matches(text).Select(m => m.Value).FirstOrDefault(s.PointsHere);
            if (file is null) continue;
            yield return new TailItem
            {
                Id = "T|" + path,
                Kind = TailKind.Task,
                Title = $"Задача планировщика «{path.TrimStart('\\')}»",
                Color = CleanColor.Green,
                Evidence = [$"Запускает файл из папки программы ({file})"],
                NeedsAdmin = true,
            };
        }
    }
}
