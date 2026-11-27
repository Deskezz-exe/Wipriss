using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Win32;

namespace Wipriss.Cleaner;

/// <summary>Everything known about one program: what the list shows and how it is removed.</summary>
internal sealed class ProgramRecord
{
    public required AppEntry Entry { get; init; }
    /// <summary>Uninstall-list programs: where their record is.</summary>
    public RegRoot Root { get; init; }
    public string? SubKey { get; init; }
    /// <summary>The folder it is installed in (checked: not a common folder), or null.</summary>
    public string? Folder { get; set; }
    public string? UninstallCommand { get; init; }
    public string? ProductCode { get; init; }
    /// <summary>Store apps: Name_Version_Arch_ResourceId_PublisherId.</summary>
    public string? PackageFullName { get; init; }
    public string NameKey => PathText.NameKey(Entry.Name);
    public string PublisherKey => PathText.NameKey(Entry.Publisher);
}

/// <summary>
/// The installed programs, as Windows' «Установленные приложения» shows them: the uninstall list (64-bit,
/// 32-bit, the user's) without updates and hidden parts, plus the user's Microsoft Store apps without
/// frameworks and parts of Windows. Read-only.
/// </summary>
internal static partial class ProgramList
{
    public const string UninstallPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";
    private const string StorePath = @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages";

    public static List<ProgramRecord> Read(Action<int>? progress, CancellationToken cancel)
    {
        var list = new List<ProgramRecord>();
        foreach (var root in RegRoot.All)
        {
            using var uninstall = root.Open(UninstallPath);
            foreach (var name in Reg.SubKeys(uninstall))
            {
                using var app = Reg.SafeOpen(uninstall!, name);
                if (app is not null && FromUninstall(app, root, name) is { } record) list.Add(record);
            }
        }
        // The same program registered twice (64- and 32-bit records of one MSI): keep one.
        list = list.GroupBy(p => (p.Entry.Name.ToLowerInvariant(), p.Entry.Version))
            .Select(g => g.OrderByDescending(p => p.Entry.HasUninstaller).ThenByDescending(p => p.Folder is not null).First())
            .ToList();
        cancel.ThrowIfCancellationRequested();
        list.AddRange(ReadStore(measure: true));

        // Sizes the record does not give: measure the folder (in parallel; the slow part).
        var measure = list.Where(p => p.Entry.Size <= 0 && p.Folder is not null).ToList();
        int done = 0;
        Parallel.ForEach(measure, new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 2, 2, 4), CancellationToken = cancel },
            p =>
            {
                var stats = FolderStats.Measure(p.Folder!, lookForPersonal: false);
                if (stats.Exists && stats.Size > 0) p.Entry.Size = stats.Size;
                progress?.Invoke(Interlocked.Increment(ref done) * 100 / Math.Max(1, measure.Count));
            });
        return list.OrderBy(p => p.Entry.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>The program with this id, read again (null if it is not installed any more).</summary>
    public static ProgramRecord? Find(string id)
    {
        if (id.StartsWith("store:", StringComparison.Ordinal))
        {
            var full = id[6..];
            return ValidPackageName(full) ? ReadStore(measure: false).FirstOrDefault(p => p.PackageFullName == full) : null;
        }
        var parts = id.Split(':', 3);
        if (parts.Length != 3 || parts[0] != "reg" || RegRoot.Parse(parts[1]) is not { } root
            || parts[2].Length == 0 || parts[2].Contains('\\')) return null;
        using var app = root.Open(UninstallPath + "\\" + parts[2]);
        return app is null ? null : FromUninstall(app, root, parts[2], keepHidden: true);
    }

    public static bool IsStillInstalled(ProgramRecord program) =>
        program.PackageFullName is { } full
            ? Registry.CurrentUser.OpenSubKey(StorePath + "\\" + full) is { } key && Dispose(key)
            : program.SubKey is not null && program.Root.Exists(UninstallPath + "\\" + program.SubKey);

    private static bool Dispose(RegistryKey key) { key.Dispose(); return true; }

    private static ProgramRecord? FromUninstall(RegistryKey app, RegRoot root, string subKey, bool keepHidden = false)
    {
        var name = Reg.Text(app, "DisplayName")?.Trim();
        if (string.IsNullOrWhiteSpace(name)) return null;
        var uninstall = Reg.Text(app, "UninstallString")?.Trim();
        var quiet = Reg.Text(app, "QuietUninstallString")?.Trim();
        if (!keepHidden)
        {
            // Hidden parts and updates, as Windows' own list hides them.
            if (app.GetValue("SystemComponent") is int system && system == 1) return null;
            if (!string.IsNullOrEmpty(Reg.Text(app, "ParentKeyName"))) return null;
            var release = Reg.Text(app, "ReleaseType") ?? "";
            if (release.Contains("Update", StringComparison.OrdinalIgnoreCase) || release.Contains("Hotfix", StringComparison.OrdinalIgnoreCase)) return null;
            if (string.IsNullOrEmpty(uninstall) && string.IsNullOrEmpty(quiet) && string.IsNullOrEmpty(Reg.Text(app, "InstallLocation"))) return null;
        }

        bool msi = app.GetValue("WindowsInstaller") is int wi && wi == 1;
        string? productCode = Guid.TryParseExact(subKey, "B", out _) ? subKey : null;
        var command = Uninstall.Command(uninstall, quiet, msi ? productCode : null);

        var icon = Reg.Text(app, "DisplayIcon");
        string? iconPath = null;
        int iconIndex = 0;
        if (!string.IsNullOrWhiteSpace(icon))
        {
            var text = icon.Trim();
            var comma = text.LastIndexOf(',');
            if (comma > 2 && int.TryParse(text[(comma + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var index))
            {
                iconIndex = index;
                text = text[..comma];
            }
            iconPath = PathText.Normalize(text);
        }

        long size = app.GetValue("EstimatedSize") is int kb && kb > 0 ? kb * 1024L : 0;
        DateTime? installed = null;
        if (DateTime.TryParseExact(Reg.Text(app, "InstallDate"), "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            installed = date;
        installed ??= Reg.LastWrite(app);

        var folder = UsableFolder(Reg.Text(app, "InstallLocation"))
                     ?? FolderOf(iconPath, name) ?? FolderOf(PathText.ExePath(uninstall), name);
        if (iconPath is null || !File.Exists(iconPath)) { iconPath = null; iconIndex = 0; }
        if (iconPath is null && msi && Reg.PackGuid(productCode) is { } packed)
        {
            // A Windows Installer product keeps its icon in its own record.
            using var product = RegRoot.Machine64.Open(@"SOFTWARE\Classes\Installer\Products\" + packed);
            var productIcon = PathText.Normalize(Reg.Text(product, "ProductIcon")?.Split(',')[0]);
            if (productIcon is not null && File.Exists(productIcon)) iconPath = productIcon;
        }
        iconPath ??= MainExe(folder, name);

        return new ProgramRecord
        {
            Entry = new AppEntry
            {
                Id = $"reg:{root.Code}:{subKey}",
                Name = name,
                Publisher = Reg.Text(app, "Publisher")?.Trim(),
                Version = Reg.Text(app, "DisplayVersion")?.Trim(),
                Installed = installed,
                Size = size,
                IconPath = iconPath,
                IconIndex = iconIndex,
                HasUninstaller = command is not null,
            },
            Root = root,
            SubKey = subKey,
            Folder = folder,
            UninstallCommand = command,
            ProductCode = productCode,
        };
    }

    /// <summary>A folder that may belong to one program: it exists and is not a common or protected one.</summary>
    internal static string? UsableFolder(string? path)
    {
        var normal = PathText.Normalize(path);
        if (normal is null || normal.Length <= 3 || Verdict.IsProtected(normal) || Deleter.IsForbidden(normal)) return null;
        if (Path.HasExtension(normal) && File.Exists(normal)) return null; // a file, not a folder
        return Directory.Exists(normal) ? normal : null;
    }

    /// <summary>The folder of the program's exe; one level up when the exe sits in «bin» of a folder named like the program.</summary>
    private static string? FolderOf(string? exe, string name)
    {
        if (exe is null) return null;
        var folder = UsableFolder(Path.GetDirectoryName(exe));
        if (folder is null) return null;
        var parent = UsableFolder(Path.GetDirectoryName(folder));
        if (parent is not null && PathText.NamesMatch(PathText.NameKey(Path.GetFileName(parent)), PathText.NameKey(name))) return parent;
        return folder;
    }

    /// <summary>The program's own exe in its folder: named like it, otherwise the only one at the top.</summary>
    private static string? MainExe(string? folder, string name)
    {
        if (folder is null) return null;
        try
        {
            var options = new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 1, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
            var exes = Directory.EnumerateFiles(folder, "*.exe", options).Take(60).ToList();
            var key = PathText.NameKey(name);
            return exes.FirstOrDefault(e => PathText.NamesMatch(PathText.NameKey(Path.GetFileNameWithoutExtension(e)), key))
                   ?? (exes.Count(e => Path.GetDirectoryName(e) == folder && !Path.GetFileName(e).StartsWith("unins", StringComparison.OrdinalIgnoreCase)) == 1
                       ? exes.First(e => Path.GetDirectoryName(e) == folder && !Path.GetFileName(e).StartsWith("unins", StringComparison.OrdinalIgnoreCase)) : null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    // ---------- Microsoft Store ----------

    [GeneratedRegex(@"^[A-Za-z0-9.\-]{3,50}_[A-Za-z0-9.]{1,30}_[A-Za-z0-9]{0,10}_[A-Za-z0-9.\-~]{0,30}_[a-z0-9]{13}$")]
    private static partial Regex PackageName();

    internal static bool ValidPackageName(string name) => PackageName().IsMatch(name);

    private static List<ProgramRecord> ReadStore(bool measure)
    {
        var list = new List<ProgramRecord>();
        RegistryKey? packages;
        try { packages = Registry.CurrentUser.OpenSubKey(StorePath); }
        catch (Exception ex) when (Reg.Expected(ex)) { return list; }
        using (packages)
        {
            foreach (var full in Reg.SubKeys(packages))
            {
                if (!ValidPackageName(full)) continue;
                using var package = Reg.SafeOpen(packages!, full);
                var root = PathText.Normalize(Reg.Text(package, "PackageRootFolder"));
                if (root is null || PathText.IsInside(root, Places.Windows) || !Directory.Exists(root)) continue;
                if (FromManifest(full, root, Reg.Text(package, "DisplayName")) is { } record) list.Add(measure ? record.WithSize(root) : record);
            }
        }
        // Several versions of one app may be registered: keep the newest.
        return list.GroupBy(p => Family(p.PackageFullName!))
            .Select(g => g.OrderByDescending(p => Version.TryParse(p.Entry.Version, out var v) ? v : new Version()).First())
            .ToList();
    }

    internal static string Family(string fullName)
    {
        var parts = fullName.Split('_');
        return parts.Length >= 5 ? parts[0] + "_" + parts[^1] : fullName;
    }

    private static ProgramRecord? FromManifest(string fullName, string root, string? registryName)
    {
        XElement manifest;
        try
        {
            var file = Path.Combine(root, "AppxManifest.xml");
            if (!File.Exists(file) || new FileInfo(file).Length > 2 * 1024 * 1024) return null;
            manifest = XDocument.Load(file).Root!;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException) { return null; }

        var properties = manifest.Elements().FirstOrDefault(e => e.Name.LocalName == "Properties");
        if (properties is null) return null;
        bool Flag(string name) => properties.Elements().Any(e => e.Name.LocalName == name && e.Value.Trim().Equals("true", StringComparison.OrdinalIgnoreCase));
        if (Flag("Framework") || Flag("ResourcePackage")) return null;
        var applications = manifest.Descendants().Where(e => e.Name.LocalName == "Application").ToList();
        if (applications.Count == 0) return null;
        var visuals = applications.Select(a => a.Descendants().FirstOrDefault(e => e.Name.LocalName == "VisualElements")).OfType<XElement>().ToList();
        // Every app of the package hidden from the Start menu: a part of Windows or of another program.
        if (visuals.Count > 0 && visuals.All(v => string.Equals((string?)v.Attribute("AppListEntry"), "none", StringComparison.OrdinalIgnoreCase))) return null;

        var packageName = fullName.Split('_')[0];
        string? Prop(string name) => properties.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value.Trim();
        var name = Resolve(fullName, packageName, registryName) ?? Resolve(fullName, packageName, Prop("DisplayName"));
        if (string.IsNullOrWhiteSpace(name)) return null;
        var publisher = Resolve(fullName, packageName, Prop("PublisherDisplayName"));

        var logo = visuals.Select(v => (string?)v.Attribute("Square44x44Logo")).FirstOrDefault(l => !string.IsNullOrEmpty(l)) ?? Prop("Logo");
        var identity = manifest.Elements().FirstOrDefault(e => e.Name.LocalName == "Identity");
        DateTime? installed = null;
        try { installed = Directory.GetCreationTime(root); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }

        return new ProgramRecord
        {
            Entry = new AppEntry
            {
                Id = "store:" + fullName,
                Name = name,
                Publisher = publisher,
                Version = (string?)identity?.Attribute("Version"),
                Installed = installed,
                IconPath = logo is null ? null : Asset(root, logo),
                Store = true,
                HasUninstaller = true,
            },
            PackageFullName = fullName,
            Folder = PathText.IsInside(root, Path.Combine(Places.ProgramFiles, "WindowsApps")) ? null : UsableFolder(root),
            // The package folder itself is Windows' to remove; only its size is measured here.
        };
    }

    private static ProgramRecord WithSize(this ProgramRecord record, string root)
    {
        if (record.Folder is null)
        {
            var stats = FolderStats.Measure(root, lookForPersonal: false);
            if (stats.Exists) record.Entry.Size = stats.Size;
        }
        return record;
    }

    /// <summary>«ms-resource:AppName» and «@{…?ms-resource://…}» to the text in the user's language.</summary>
    private static string? Resolve(string fullName, string packageName, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        string source;
        if (value.StartsWith('@')) source = value;
        else if (value.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase))
        {
            var rest = value[12..];
            source = rest.StartsWith("//", StringComparison.Ordinal) ? $"@{{{fullName}?{value}}}"
                : rest.Contains('/') ? $"@{{{fullName}?ms-resource://{packageName}/{rest.TrimStart('/')}}}"
                : $"@{{{fullName}?ms-resource://{packageName}/Resources/{rest}}}";
        }
        else return value.Trim();
        var buffer = new StringBuilder(512);
        if (SHLoadIndirectString(source, buffer, buffer.Capacity, IntPtr.Zero) != 0) return null;
        var text = buffer.ToString().Trim();
        return text.Length == 0 || text.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase) ? null : text;
    }

    /// <summary>A manifest image: the file itself or its best variant («Logo.targetsize-32.png», «Logo.scale-100.png»).</summary>
    private static string? Asset(string root, string relative)
    {
        try
        {
            var path = Path.GetFullPath(Path.Combine(root, relative));
            if (!PathText.IsInside(path, root)) return null;
            if (File.Exists(path)) return path;
            var folder = Path.GetDirectoryName(path);
            if (folder is null || !Directory.Exists(folder)) return null;
            var stem = Path.GetFileNameWithoutExtension(path);
            var ext = Path.GetExtension(path);
            var variants = Directory.GetFiles(folder, stem + ".*" + ext);
            return variants.FirstOrDefault(v => v.Contains(".targetsize-32.", StringComparison.OrdinalIgnoreCase))
                ?? variants.FirstOrDefault(v => v.Contains("targetsize-32_altform-unplated", StringComparison.OrdinalIgnoreCase))
                ?? variants.FirstOrDefault(v => v.Contains(".scale-100.", StringComparison.OrdinalIgnoreCase))
                ?? variants.FirstOrDefault(v => v.Contains("targetsize-", StringComparison.OrdinalIgnoreCase))
                ?? variants.FirstOrDefault();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return null; }
    }

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int SHLoadIndirectString(string source, StringBuilder output, int size, IntPtr reserved);
}
