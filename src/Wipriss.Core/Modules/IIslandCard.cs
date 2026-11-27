using System.Windows;
using System.Windows.Media;

namespace Wipriss.Core.Modules;

/// <summary>What is going on right now, for the closed pill.</summary>
/// <param name="Text">A few words, e.g. "Делает замеры".</param>
/// <param name="State">Busy shows a spinner, Done a tick, Info nothing.</param>
/// <param name="Icon">Small mark on the left (a geometry in a 24×24 box), or null.</param>
/// <param name="IconBrush">Its colour.</param>
public sealed record IslandActivity(string Text, ActivityState State, Geometry? Icon, Brush? IconBrush);

public enum ActivityState { Info, Busy, Done, Waiting }

/// <summary>
/// A module that shows a live card on the island's home screen. Everything it knows must come from
/// Windows events (file changes, media events), never from polling.
/// </summary>
public interface IIslandCard
{
    /// <summary>Status for the closed pill, or null when nothing is going on.</summary>
    IslandActivity? Activity { get; }

    /// <summary>Whether the card has something to show now. Empty cards are hidden.</summary>
    bool HasContent { get; }

    /// <summary>Activity or content changed. Raised on the UI thread.</summary>
    event Action? Changed;

    /// <summary>Builds the card. Called when the island opens; the card is dropped when it closes.</summary>
    FrameworkElement CreateCard();
}

/// <summary>A module with its own page in the Settings window.</summary>
public interface ISettingsPage
{
    /// <summary>Builds the page. Called when the page is opened; dropped with the window.</summary>
    FrameworkElement CreateSettings();
}
