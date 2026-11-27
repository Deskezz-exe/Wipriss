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

    /// <summary>Delete: which items.</summary>
    public List<CleanTarget> Targets { get; set; } = [];
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
}

[JsonSourceGenerationOptions(UseStringEnumConverter = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault)]
[JsonSerializable(typeof(CleanRequest))]
[JsonSerializable(typeof(CleanReply))]
internal sealed partial class CleanJson : JsonSerializerContext;
