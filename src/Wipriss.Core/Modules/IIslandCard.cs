using System.Windows;

namespace Wipriss.Core.Modules;

/// <summary>Where a card stands on the home screen.</summary>
public enum HomeArea
{
    /// <summary>The big card on the left (music and video).</summary>
    Main,

    /// <summary>Top of the right column, a short card (the timer).</summary>
    SideTop,

    /// <summary>Under it, a card that grows with its content (the to-do list).</summary>
    SideMiddle,

    /// <summary>The rest of the right column (agents).</summary>
    Side,
}

/// <summary>
/// A module that shows a live card on the island's home screen. Everything it knows must come from
/// Windows events (file changes, media events), never from polling. The closed pill is <see cref="IPillSource"/>.
/// A card may tick (a clock, a progress bar) only while it is loaded: it is dropped when the island closes.
/// </summary>
public interface IIslandCard
{
    /// <summary>Whether the card has something to show now. Empty cards are hidden.</summary>
    bool HasContent { get; }

    /// <summary>Activity or content changed. Raised on the UI thread.</summary>
    event Action? Changed;

    /// <summary>Builds the card. Called when the island opens; the card is dropped when it closes.</summary>
    FrameworkElement CreateCard();

    HomeArea Area => HomeArea.Side;
}

/// <summary>
/// A module with small figures in the top row of the open island (CPU, memory…). The view is built when the island
/// opens and dropped when it closes; the module may update it only while the view is loaded.
/// </summary>
public interface IIslandStatus
{
    FrameworkElement CreateStatus();

    /// <summary>False: the middle of the top row (figures). True: next to the settings button (devices).</summary>
    bool StatusOnRight => false;
}

/// <summary>
/// The open island never takes the keyboard (clicking it must not steal focus from your app). A card that needs
/// typing (a new task) asks for it here: the island takes the focus and stays open until the returned handle is
/// disposed. Set up by the island; without it nothing happens.
/// </summary>
public static class IslandKeyboard
{
    public static Func<IDisposable>? Provider { get; set; }

    public static IDisposable Take() => Provider?.Invoke() ?? new Nothing();

    private sealed class Nothing : IDisposable
    {
        public void Dispose() { }
    }
}

/// <summary>A module with its own page in the Settings window.</summary>
public interface ISettingsPage
{
    /// <summary>Builds the page. Called when the page is opened; dropped with the window.</summary>
    FrameworkElement CreateSettings();
}

/// <summary>
/// A part of a dock tab that opens inside the open island (like the home screen) instead of a panel window.
/// Several modules may share one tab: their parts stand one under another by <see cref="PageOrder"/>. Built when the
/// tab is chosen, dropped when the island closes or another tab is chosen.
/// </summary>
public interface IIslandPage
{
    /// <summary>The dock tab (a module id) this part belongs to.</summary>
    string PageId { get; }

    int PageOrder => 0;

    FrameworkElement CreatePage();
}
