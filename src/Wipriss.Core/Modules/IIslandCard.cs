using System.Windows;

namespace Wipriss.Core.Modules;

/// <summary>
/// A module that shows a live card on the island's home screen. Everything it knows must come from
/// Windows events (file changes, media events), never from polling. The closed pill is <see cref="IPillSource"/>.
/// </summary>
public interface IIslandCard
{
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
