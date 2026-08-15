using System.Globalization;
using SpatialAnalyzer.Core.Domain;

namespace SpatialAnalyzer.Core.Spatial;

/// <summary>
/// What a door turned out to connect.
///
/// Each case is a different fact about the building, and they are kept apart
/// because collapsing them is how a door with an unknown far side comes to be
/// reported as a door to the outside.
/// </summary>
public enum DoorConnection
{
    /// <summary>Neither side resolved to a region.</summary>
    Unresolved,

    /// <summary>
    /// One side resolved and the other did not. The door may lead outdoors, or
    /// to somewhere the analysis did not cover; this says only what was found.
    /// </summary>
    OneSideResolved,

    /// <summary>
    /// Both sides resolved to the same region, so the door separates nothing.
    /// Not an error: it is what Revit's own room parameters report for six of
    /// the eighteen doors on the acceptance level, and it can also be genuine
    /// where a wall does not divide the space it sits in.
    /// </summary>
    WithinOneRegion,

    /// <summary>The ordinary case: a door between two different regions.</summary>
    BetweenTwoRegions,
}

/// <summary>
/// A door and the regions found on either side of it.
///
/// The brief asks that selecting a door report both rooms it connects, which
/// makes the failure to find one of them a result in its own right rather than
/// an inconvenience to be smoothed over. A door whose far side could not be
/// resolved and a door that genuinely opens to the outside are different
/// findings, and this type will not let them arrive downstream looking alike.
///
/// How the sides are found is not decided here. Revit's own FromRoom and ToRoom
/// name the same room on both sides for a third of the doors on the acceptance
/// level, because those parameters describe placed rooms rather than the
/// granular spaces this project analyses, so the sides are resolved
/// geometrically elsewhere. This type records the outcome.
/// </summary>
public sealed class DoorAdjacency
{
    private DoorAdjacency(ElementDescriptor door, RegionId? sideA, RegionId? sideB)
    {
        Door = door;
        SideA = sideA;
        SideB = sideB;

        Connection = (sideA, sideB) switch
        {
            (null, null) => DoorConnection.Unresolved,
            (not null, not null) when sideA.Value == sideB.Value => DoorConnection.WithinOneRegion,
            (not null, not null) => DoorConnection.BetweenTwoRegions,
            _ => DoorConnection.OneSideResolved,
        };

        Regions = new[] { sideA, sideB }
            .Where(r => r.HasValue)
            .Select(r => r!.Value)
            .Distinct()
            .OrderBy(r => r)
            .ToList();
    }

    /// <summary>
    /// Records what was found on either side of a door.
    ///
    /// Both sides are supplied as findings and the description is derived from
    /// them, so a caller cannot state that a door joins two rooms while naming
    /// only one, or call a door exterior without having looked.
    /// </summary>
    public static DoorAdjacency Resolved(ElementDescriptor door, RegionId? sideA, RegionId? sideB)
    {
        ArgumentNullException.ThrowIfNull(door);
        return new DoorAdjacency(door, sideA, sideB);
    }

    public ElementDescriptor Door { get; }

    /// <summary>One side of the door, or null if nothing was found there.</summary>
    public RegionId? SideA { get; }

    /// <summary>The other side, or null if nothing was found there.</summary>
    public RegionId? SideB { get; }

    /// <summary>
    /// The regions this door touches, each once, in ordinal order. Sorted rather
    /// than left in the order the sides were examined, so that reports and
    /// exports read the same way for the same model however the geometry was
    /// walked.
    /// </summary>
    public IReadOnlyList<RegionId> Regions { get; }

    public DoorConnection Connection { get; }

    /// <summary>
    /// Whether this door was found to join two different regions, which is the
    /// only case in which it can be said to connect anything.
    /// </summary>
    public bool ConnectsTwoRegions => Connection == DoorConnection.BetweenTwoRegions;

    public override string ToString() => string.Create(
        CultureInfo.InvariantCulture,
        $"{Door} -> {Connection} [{string.Join(", ", Regions)}]");
}
