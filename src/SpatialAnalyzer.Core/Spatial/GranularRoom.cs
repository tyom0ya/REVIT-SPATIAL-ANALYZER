using System.Globalization;
using SpatialAnalyzer.Core.Domain;
using SpatialAnalyzer.Core.Geometry;

namespace SpatialAnalyzer.Core.Spatial;

/// <summary>
/// A region that has been judged to be a room.
///
/// This is the unit the whole application reports in: what an element belongs
/// to, what a door connects, what the export lists. It exists as a type distinct
/// from <see cref="CandidateRegion"/> so that nothing can be reported as a room
/// without having passed through the qualification step - the compiler enforces
/// what would otherwise be a convention.
///
/// Two things are required of every room here, and both are refusals rather than
/// corrections.
///
/// It must be enclosed. A space open to the one next door is not a smaller room,
/// it is part of a larger one, and the only way to make it a room would be to
/// close the opening - which this project does not do. So a region whose
/// boundary is interrupted cannot become a room at all.
///
/// Something must let you in. A void that no one can reach is not a room in the
/// sense the brief means, and a room is required to name the entrances that
/// qualified it, so the evidence travels with the conclusion. What counts as an
/// entrance is not decided here: that is a rule, kept where it can be read and
/// argued with. This type only insists that the question was answered.
/// </summary>
public sealed class GranularRoom
{
    /// <param name="entrances">
    /// The elements found to admit entry - doors in the ordinary case, and in
    /// this model also walls embedded in other walls. Recorded as the evidence
    /// for why this region is a room.
    /// </param>
    public GranularRoom(CandidateRegion region, IReadOnlyList<ElementDescriptor> entrances)
    {
        ArgumentNullException.ThrowIfNull(region);
        ArgumentNullException.ThrowIfNull(entrances);

        if (!region.IsEnclosed)
        {
            throw new ArgumentException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Region {region.Id} is open by {region.LargestGapInternalFeet:0.######} ft at a tolerance of {region.ClosureToleranceInternalFeet:0.######} ft, so it is part of the space beyond that opening rather than a room of its own."),
                nameof(region));
        }

        if (entrances.Count == 0)
        {
            throw new ArgumentException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Region {region.Id} has no entrance, so it cannot be reported as a room. A region that qualified some other way must say what qualified it."),
                nameof(entrances));
        }

        Region = region;
        Entrances = entrances;
    }

    /// <summary>
    /// The region this room is. Rooms and candidates share one numbering, so a
    /// room and the candidate it came from can be matched up by eye in a report
    /// that lists both the rooms found and the candidates rejected.
    /// </summary>
    public RegionId Id => Region.Id;

    public CandidateRegion Region { get; }

    public IReadOnlyList<ElementDescriptor> Entrances { get; }

    /// <summary>
    /// The floor area, with interior voids already taken out. Always measured,
    /// because an unenclosed region cannot become a room.
    /// </summary>
    public AreaMeasurement Area => Region.NetArea;

    public IReadOnlyList<BoundaryLoop> Loops => Region.Loops;

    /// <summary>The distinct elements that enclose this room.</summary>
    public IReadOnlyList<BoundaryReference> BoundingReferences => Region.BoundingReferences;

    public override string ToString() => string.Create(
        CultureInfo.InvariantCulture,
        $"{Id}: {Area}, {Entrances.Count} entrance(s)");
}
