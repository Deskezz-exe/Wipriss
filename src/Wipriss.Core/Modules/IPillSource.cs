using System.Windows;

namespace Wipriss.Core.Modules;

/// <summary>Blocks of the closed pill, left to right. Thin dividers stand between groups.</summary>
public enum PillGroup { Agents = 0, Media = 1, Load = 2, Battery = 3 }

/// <summary>
/// One block of the closed pill (a song, the CPU figure, one device's battery…). The module builds the
/// view and keeps it up to date; the island only lays the blocks out. When the pill is short of room the
/// block with the lowest <see cref="Priority"/> goes away whole, so nothing is ever cut in the middle.
/// </summary>
/// <param name="Id">Stable and unique, e.g. "load.cpu" or "battery.3f2a…". The view is kept while the id stays.</param>
/// <param name="Kind">What the user can switch off in Settings, e.g. "load.cpu" or "battery" (all devices).</param>
/// <param name="Group">Where it stands.</param>
/// <param name="Priority">Higher stays longer when room is short.</param>
/// <param name="Create">Builds the view. Its width may only change together with <see cref="IPillSource.PillChanged"/>.</param>
public sealed record PillSlot(string Id, string Kind, PillGroup Group, int Priority, Func<FrameworkElement> Create);

/// <summary>
/// A short wider message (e.g. "Claude Code ждёт подтверждения"): the pill opens up for it for a moment,
/// then goes back to its blocks. A newer peek with the same key replaces a waiting one.
/// </summary>
public sealed record PillPeek(string Key, Func<FrameworkElement> Create, TimeSpan Duration);

/// <summary>A module that shows blocks in the closed pill.</summary>
public interface IPillSource
{
    /// <summary>The blocks it wants to show now (the island may leave some out for room or by the user's choice).</summary>
    IReadOnlyList<PillSlot> PillSlots { get; }

    /// <summary>Blocks came or went, or a view changed its width. Raised on the UI thread.</summary>
    event Action? PillChanged;

    /// <summary>Asks for a moment of attention. Raised on the UI thread.</summary>
    event Action<PillPeek>? PillPeek;

    /// <summary>
    /// Whether any of its blocks is on screen right now. False while the island is open or hidden, the screen
    /// is off or locked, or none of its blocks fit. Anything that ticks (figures, animations) runs only while true.
    /// </summary>
    void SetPillShown(bool shown);
}
