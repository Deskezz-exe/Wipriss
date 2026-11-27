namespace Wipriss.Core.Modules;

/// <summary>Where a module lives on the island.</summary>
public enum ModulePlace
{
    /// <summary>A button in the dock at the bottom of the open island.</summary>
    Dock,

    /// <summary>A live card on the home screen (and a status in the closed pill).</summary>
    Card,

    /// <summary>No button: works by a key (PrtSc, a hotkey) and is set up in Settings.</summary>
    Background,
}

/// <summary>Describes a module: what the island shows for it and when it is planned.</summary>
/// <param name="Id">Stable id used in settings.</param>
/// <param name="Title">Short name shown on the tile.</param>
/// <param name="Glyph">Segoe Fluent Icons / MDL2 glyph.</param>
/// <param name="Description">One line shown as a tooltip.</param>
/// <param name="Stage">Build stage in which the module arrives.</param>
/// <param name="Place">Dock button, home-screen card or background only.</param>
public sealed record ModuleInfo(string Id, string Title, string Glyph, string Description, int Stage,
    ModulePlace Place = ModulePlace.Dock)
{
    public string StageText => $"скоро · этап {Stage}";
}
