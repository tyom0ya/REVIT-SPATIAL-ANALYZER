using System.Globalization;
using SpatialAnalyzer.Core.Domain;

namespace SpatialAnalyzer.Core.Spatial;

/// <summary>
/// A connector found on more than two boundaries.
///
/// A door has two sides, so this should not happen, and when it does the honest
/// response is to say which regions were involved rather than pick two of them.
/// The likeliest cause is the allowance used to decide that an insert lies on a
/// boundary catching a third space whose wall runs close by; whatever the cause,
/// reporting a door as joining two rooms when the evidence names three would be
/// a guess dressed as a result.
/// </summary>
public sealed record AmbiguousConnector(ElementDescriptor Element, IReadOnlyList<RegionId> Regions)
{
    public override string ToString() => string.Create(
        CultureInfo.InvariantCulture,
        $"{Element} found on {Regions.Count} boundaries: {string.Join(", ", Regions)}");
}

/// <summary>
/// Works out what each door connects, by inverting what was found on each
/// region's boundary.
///
/// Revit's own FromRoom and ToRoom are not used, and could not be: on the
/// acceptance level they name the same room on both sides for six of the
/// eighteen doors, because they describe placed rooms rather than the granular
/// spaces this project analyses. They would also be silent wherever no room has
/// been placed, which is most of the level.
///
/// Instead this reads the result of a question already answered geometrically.
/// Establishing which inserts open onto a region is work the boundary collector
/// does per region; a door that turns up on the boundaries of two regions is,
/// by that same evidence, the thing between them. Nothing new is measured here,
/// which is the point - the adjacency is exactly as trustworthy as the
/// projection test that produced it, and no second, subtly different notion of
/// "on the boundary" is introduced to disagree with the first.
///
/// What counts as a connector is the entrance rule, unchanged. Something that
/// admits a person into a space is what joins it to the space beyond.
/// </summary>
public sealed class DoorAdjacencyIndex
{
    private readonly Dictionary<RevitElementId, DoorAdjacency> _byElement;

    private DoorAdjacencyIndex(
        Dictionary<RevitElementId, DoorAdjacency> byElement,
        IReadOnlyList<AmbiguousConnector> ambiguous)
    {
        _byElement = byElement;
        Ambiguous = ambiguous;
    }

    /// <param name="featuresByRegion">
    /// What was found on each region's boundary, region by region. Only regions
    /// that were actually examined belong here: a region absent from this map is
    /// one nothing is known about, which is different from one with nothing on
    /// its boundary.
    /// </param>
    public static DoorAdjacencyIndex Build(
        IReadOnlyDictionary<RegionId, IReadOnlyList<BoundaryFeature>> featuresByRegion,
        EntranceRule entranceRule)
    {
        ArgumentNullException.ThrowIfNull(featuresByRegion);
        ArgumentNullException.ThrowIfNull(entranceRule);

        var regionsByConnector = new Dictionary<RevitElementId, SortedSet<RegionId>>();
        var connectors = new Dictionary<RevitElementId, BoundaryFeature>();

        foreach ((RegionId regionId, IReadOnlyList<BoundaryFeature> features) in
                 featuresByRegion.OrderBy(e => e.Key))
        {
            foreach (BoundaryFeature feature in features)
            {
                if (!entranceRule.Admits(feature.Kind))
                {
                    continue;
                }

                if (!regionsByConnector.TryGetValue(feature.Element.Id, out SortedSet<RegionId>? regions))
                {
                    regions = new SortedSet<RegionId>();
                    regionsByConnector[feature.Element.Id] = regions;
                    connectors[feature.Element.Id] = feature;
                }

                regions.Add(regionId);
            }
        }

        var byElement = new Dictionary<RevitElementId, DoorAdjacency>();
        var ambiguous = new List<AmbiguousConnector>();

        foreach ((RevitElementId id, SortedSet<RegionId> regions) in regionsByConnector.OrderBy(e => e.Key.Value))
        {
            ElementDescriptor element = connectors[id].Element;

            switch (regions.Count)
            {
                case 1:
                    // One side found. Whether the other is outdoors, another
                    // level, or a space this analysis rejected is not something
                    // the evidence settles, and the type does not pretend it is.
                    byElement[id] = DoorAdjacency.Resolved(element, regions.Min, null);
                    break;

                case 2:
                    byElement[id] = DoorAdjacency.Resolved(element, regions.Min, regions.Max);
                    break;

                default:
                    ambiguous.Add(new AmbiguousConnector(element, regions.ToList()));
                    break;
            }
        }

        return new DoorAdjacencyIndex(byElement, ambiguous);
    }

    /// <summary>
    /// Every connector whose sides could be stated, ordered by element id so
    /// that two runs over an unchanged model report the same thing in the same
    /// order.
    /// </summary>
    public IReadOnlyList<DoorAdjacency> Adjacencies =>
        _byElement.Values.OrderBy(a => a.Door.Id.Value).ToList();

    /// <summary>
    /// Connectors found on more than two boundaries. Reported rather than
    /// resolved, and deliberately absent from <see cref="Adjacencies"/>: naming
    /// two of three regions would be a guess.
    /// </summary>
    public IReadOnlyList<AmbiguousConnector> Ambiguous { get; }

    /// <summary>
    /// What a particular door connects, or null if it was never found on any
    /// region's boundary. Null means nothing was established, not that the door
    /// leads nowhere.
    /// </summary>
    public DoorAdjacency? For(RevitElementId doorId) =>
        _byElement.TryGetValue(doorId, out DoorAdjacency? adjacency) ? adjacency : null;

    /// <summary>
    /// The connectors on one region's boundary, with what lies on the far side
    /// of each. This is the question the brief asks of a selected door, answered
    /// from the region's point of view.
    /// </summary>
    public IReadOnlyList<DoorAdjacency> Touching(RegionId region) =>
        Adjacencies.Where(a => a.Regions.Contains(region)).ToList();
}
