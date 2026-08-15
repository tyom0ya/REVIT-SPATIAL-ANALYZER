using SpatialAnalyzer.Core.Domain;

namespace SpatialAnalyzer.Core.Spatial;

/// <summary>
/// On whose authority a way into a room is a way in.
/// </summary>
public enum EntranceAuthority
{
    /// <summary>
    /// The model said so. A door is a door, and the rule recognised it without
    /// anyone being asked.
    /// </summary>
    Rule,

    /// <summary>
    /// A person said so, having been shown what was there.
    ///
    /// Some spaces are enclosed by things the rule will not call entrances and
    /// that a person might: a shopfront of glazed curtain walling with no door
    /// modelled in it is the case this exists for. Rather than loosen the rule
    /// until such a space qualifies - which would admit every glazed wall in
    /// every future model - the space is put to whoever is running the analysis,
    /// with everything on its boundary listed, and their answer recorded.
    ///
    /// It is kept distinct from a rule decision wherever a room is reported. A
    /// room that exists because someone was asked is a different kind of claim
    /// from one the model supports on its own, and an export that blurred them
    /// would be saying the model contains something it does not.
    /// </summary>
    OperatorConfirmed,
}

/// <summary>
/// A way into a room, with what it is and who says so.
/// </summary>
public sealed record RoomEntrance(
    ElementDescriptor Element,
    BoundaryFeatureKind Kind,
    EntranceAuthority Authority)
{
    public static RoomEntrance ByRule(BoundaryFeature feature) =>
        new(feature.Element, feature.Kind, EntranceAuthority.Rule);

    public static RoomEntrance ByOperator(BoundaryFeature feature) =>
        new(feature.Element, feature.Kind, EntranceAuthority.OperatorConfirmed);

    public override string ToString() => Authority == EntranceAuthority.Rule
        ? $"{Kind} {Element}"
        : $"{Kind} {Element} (confirmed by the operator)";
}
