namespace Wipriss.Core.Modules;

/// <summary>Describes a module: what the island shows for it and when it is planned.</summary>
/// <param name="Id">Stable id used in settings.</param>
/// <param name="Title">Short name shown on the tile.</param>
/// <param name="Glyph">Segoe Fluent Icons / MDL2 glyph.</param>
/// <param name="Description">One line shown as a tooltip.</param>
/// <param name="Stage">Build stage in which the module arrives.</param>
public sealed record ModuleInfo(string Id, string Title, string Glyph, string Description, int Stage)
{
    public string StageText => $"скоро · этап {Stage}";
}
