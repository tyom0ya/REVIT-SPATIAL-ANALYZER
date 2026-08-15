using SpatialAnalyzer.Core.Domain;

namespace SpatialAnalyzer.Core.Spatial;

/// <summary>
/// What a thing found on a region's boundary is, in terms this project can
/// reason about.
///
/// Deliberately not Revit's category name. Those are localised - a French Revit
/// calls doors "Portes" - so a rule written against the text would decide
/// whether something is a room based on the language the model was authored in.
/// The adapter translates once, here, and the rules downstream compare against
/// values that cannot drift.
/// </summary>
public enum BoundaryFeatureKind
{
    /// <summary>
    /// Something the adapter could not classify. Never admits entry, and is
    /// reported so that a category worth handling shows up as a question rather
    /// than being silently ignored.
    /// </summary>
    Unknown,

    Door,

    Window,

    /// <summary>An opening cut through a bounding element.</summary>
    Opening,

    /// <summary>A panel of a curtain wall that is itself a door.</summary>
    CurtainWallDoorPanel,

    /// <summary>A panel of a curtain wall that is glazing or solid infill.</summary>
    CurtainWallPanel,

    /// <summary>A wall embedded in a bounding wall.</summary>
    EmbeddedWall,

    /// <summary>
    /// Equipment set into a bounding wall. In this model, lift doors and trash
    /// chutes, both of which look like doors and are not ways into a room.
    /// </summary>
    SpecialtyEquipment,
}

/// <summary>
/// Something found on a region's boundary that might bear on whether the region
/// is a room.
///
/// Being on the boundary is a geometric claim, not a claim that some bounding
/// wall happens to contain the element. A long wall may host six doors and bound
/// four spaces; only the doors that actually open onto this region belong here.
/// Establishing that is the adapter's job, because it needs Revit's own curve
/// arithmetic, and getting it wrong once already gave a four square metre
/// cupboard eight doors.
/// </summary>
public sealed record BoundaryFeature(ElementDescriptor Element, BoundaryFeatureKind Kind);
