using System.Text.Json.Serialization;

namespace Wipriss.Cleaner;

// What Wipriss and Wipriss.Cleaner.exe say to each other. This file is compiled into both.
// Wipriss writes one CleanRequest line to stdin; the scanner answers with CleanReply lines on stdout
// (progress, then one Group line per program, then Done) and exits.
// Delete never takes paths from Wipriss: it names items (group id + title), the scanner searches again
// and deletes only what the fresh search finds under those names, never a red or Windows-only item.

public enum CleanCommand
{
    /// <summary>Take the inventory, look through the known places and the leftovers, report, exit.</summary>
    Scan,
    /// <summary>Search again, then delete the items named in <see cref="CleanRequest.Targets"/>.</summary>
    Delete,
    /// <summary>List the installed programs (<see cref="CleanReplyKind.Program"/> lines), then Done.</summary>
    Programs,
    /// <summary>
    /// Remove the program <see cref="CleanRequest.ProgramId"/>: snapshot, its own uninstaller (skipped when
    /// <see cref="CleanRequest.Forced"/>), then the tails. The process stays and waits for the next lines
    /// (<see cref="Continue"/>, <see cref="Force"/>, <see cref="DeleteTails"/>, <see cref="Quit"/>).
    /// </summary>
    Uninstall,
    /// <summary>Stop waiting for the uninstaller (it closed, but something of it keeps running).</summary>
    Continue,
    /// <summary>The uninstaller did not remove the program: remove it by force (folder and records).</summary>
    Force,
    /// <summary>Delete the tails named in <see cref="CleanRequest.Targets"/> (by <see cref="TailItem.Id"/>, found again first).</summary>
    DeleteTails,
    /// <summary>Put back the registry records saved in <see cref="CleanRequest.BackupDir"/>.</summary>
    Restore,
    Quit,
}

/// <summary>Where the uninstall is now.</summary>
public enum UninstallStage
{
    /// <summary>The program's own uninstaller runs: follow its windows.</summary>
    Running,
    /// <summary>The uninstaller finished, but the program is still installed (cancelled or broken).</summary>
    NotRemoved,
    /// <summary>The program has no uninstaller that works: only forced removal is possible.</summary>
    NoUninstaller,
    /// <summary>The program is gone; the tails are being looked for.</summary>
    Removed,
}

/// <summary>What a tail is, and so how it is deleted.</summary>
public enum TailKind { Folder, File, RegistryKey, RegistryValue, Service, Task }

/// <summary>One installed program (from the uninstall list, or a Microsoft Store app).</summary>
public sealed class AppEntry
{
    /// <summary>«reg:HKLM64:{name}», «reg:HKLM32:{name}», «reg:HKCU:{name}» or «store:{PackageFullName}».</summary>
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Publisher { get; set; }
    public string? Version { get; set; }
    public DateTime? Installed { get; set; }
    /// <summary>Bytes; 0 when unknown.</summary>
    public long Size { get; set; }
    /// <summary>A file to take the icon from (an .exe/.ico, or a Store .png) and its index in it.</summary>
    public string? IconPath { get; set; }
    public int IconIndex { get; set; }
    public bool Store { get; set; }
    /// <summary>False: no uninstaller is registered, only forced removal.</summary>
    public bool HasUninstaller { get; set; }
}

/// <summary>Something the removed program left: a folder, a file, a registry key or value, a service, a task.</summary>
public sealed class TailItem
{
    /// <summary>Stable: kind and place («F|C:\Program Files\Foo», «K|HKLM64|SOFTWARE\Foo»).</summary>
    public string Id { get; set; } = "";
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public TailKind Kind { get; set; }
    /// <summary>What to show: the path, «HKCU\Software\Foo», «Служба Foo».</summary>
    public string Title { get; set; } = "";
    public long Size { get; set; }
    public int Files { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public CleanColor Color { get; set; }
    /// <summary>Why it is thought to be the program's.</summary>
    public List<string> Evidence { get; set; } = [];
    /// <summary>Only with administrator rights (asked once, through UAC).</summary>
    public bool NeedsAdmin { get; set; }
    /// <summary>A folder's sub-folders and files, each deletable on its own.</summary>
    public List<TailItem> Children { get; set; } = [];
}

public enum CleanReplyKind
{
    /// <summary>What is being looked at now: <see cref="CleanReply.Text"/> and <see cref="CleanReply.Percent"/>.</summary>
    Progress,
    /// <summary>One program (or part of Windows) with what it keeps: <see cref="CleanReply.Group"/>.</summary>
    Group,
    /// <summary>Everything was reported.</summary>
    Done,
    /// <summary>Delete finished: <see cref="CleanReply.Bytes"/> freed, <see cref="CleanReply.Count"/> files could not be deleted.</summary>
    Deleted,
    Error,
    /// <summary>One installed program: <see cref="CleanReply.Program"/>.</summary>
    Program,
    /// <summary>Uninstall moved on: <see cref="CleanReply.Stage"/>.</summary>
    Stage,
    /// <summary>What the removed program left: <see cref="CleanReply.Tails"/>. The process waits for DeleteTails or Quit.</summary>
    Tails,
    /// <summary>Tails deleted: <see cref="CleanReply.Bytes"/> freed, <see cref="CleanReply.Count"/> not deleted,
    /// <see cref="CleanReply.BackupDir"/>. The process exits.</summary>
    TailsDeleted,
    /// <summary>Restore finished: <see cref="CleanReply.Count"/> files that could not be put back.</summary>
    Restored,
}

/// <summary>
/// Green: safe to delete (caches, temporary files, sure leftovers). Yellow: look yourself (settings,
/// long downloads, personal files, fresh changes). Red: do not touch; cannot be selected.
/// </summary>
public enum CleanColor { Green, Yellow, Red }

/// <summary>Sections of the list, in the order they are shown.</summary>
public enum CleanCategory { Windows, Browsers, Apps, Graphics, Developer, Projects, Leftovers, Installers, Models }

/// <summary>How an item is deleted.</summary>
public enum CleanAction
{
    /// <summary>Files are deleted; of a folder only what is inside (the folder stays: Temp, caches).</summary>
    Contents,
    /// <summary>Files and folders are deleted with the folders themselves (leftovers, node_modules, a model).</summary>
    Whole,
    /// <summary>The Recycle Bin, emptied by Windows itself.</summary>
    RecycleBin,
    /// <summary>Only Windows' own tools may clean it (see <see cref="CleanItem.HowToClean"/>); cannot be selected.</summary>
    WindowsOnly,
}

public sealed class CleanItem
{
    /// <summary>Short name: «Кэш», «Пароли», «Папка программы (Program Files)».</summary>
    public string Title { get; set; } = "";

    /// <summary>Files and folders this item is made of (a cache can live in several profiles).</summary>
    public List<string> Paths { get; set; } = [];

    /// <summary>Bytes; -1 when it could not be measured (no access without administrator rights).</summary>
    public long Size { get; set; }

    public int Files { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public CleanColor Color { get; set; }

    /// <summary>What it is, in plain words.</summary>
    public string What { get; set; } = "";

    /// <summary>What happens if it is deleted.</summary>
    public string IfDeleted { get; set; } = "";

    /// <summary>When only Windows' own tools may clean it: where to find them.</summary>
    public string? HowToClean { get; set; }

    /// <summary>Why we think so: what was found and what was not.</summary>
    public List<string> Evidence { get; set; } = [];

    /// <summary>The newest change inside (local time).</summary>
    public DateTime? LastChanged { get; set; }

    /// <summary>«высокая», «средняя», «низкая».</summary>
    public string Confidence { get; set; } = "";

    /// <summary>True when the scanner could not look inside everything.</summary>
    public bool Denied { get; set; }

    public CleanAction Action { get; set; }
}

public sealed class CleanGroup
{
    /// <summary>Stable id: «browser:firefox», «leftover:somefolder».</summary>
    public string Id { get; set; } = "";

    public string Title { get; set; } = "";

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public CleanCategory Category { get; set; }

    /// <summary>«установлен · 2 профиля», «не установлена», «часть Windows».</summary>
    public string Status { get; set; } = "";

    public string? Publisher { get; set; }

    public List<CleanItem> Items { get; set; } = [];
}

public sealed class CleanRequest
{
    public CleanCommand Command { get; set; }

    /// <summary>Paths the owner asked never to show (with everything under them).</summary>
    public List<string> Hidden { get; set; } = [];

    /// <summary>Delete: which items. DeleteTails: <see cref="CleanTarget.Title"/> is a <see cref="TailItem.Id"/>.</summary>
    public List<CleanTarget> Targets { get; set; } = [];

    /// <summary>Uninstall: which program (<see cref="AppEntry.Id"/>).</summary>
    public string? ProgramId { get; set; }

    /// <summary>Uninstall: skip the program's own uninstaller (it has none or it is broken).</summary>
    public bool Forced { get; set; }

    /// <summary>Uninstall: the folder for registry copies (a new sub-folder per removal). Restore: that sub-folder.</summary>
    public string? BackupDir { get; set; }
}

/// <summary>An item named by its group id and title (not by its paths).</summary>
public sealed class CleanTarget
{
    public string GroupId { get; set; } = "";
    public string Title { get; set; } = "";
}

public sealed class CleanReply
{
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public CleanReplyKind Kind { get; set; }
    public string? Text { get; set; }
    public int Percent { get; set; }
    public CleanGroup? Group { get; set; }
    public long Bytes { get; set; }
    public int Count { get; set; }
    public AppEntry? Program { get; set; }
    public UninstallStage Stage { get; set; }
    public List<TailItem>? Tails { get; set; }
    /// <summary>TailsDeleted: the sub-folder with the registry copy (null when no registry record was deleted).</summary>
    public string? BackupDir { get; set; }
}

[JsonSourceGenerationOptions(UseStringEnumConverter = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault)]
[JsonSerializable(typeof(CleanRequest))]
[JsonSerializable(typeof(CleanReply))]
internal sealed partial class CleanJson : JsonSerializerContext;
