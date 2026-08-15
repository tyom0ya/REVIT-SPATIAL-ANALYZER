using System.Globalization;
using SpatialAnalyzer.Core.Geometry;

namespace SpatialAnalyzer.Core.Spatial;

/// <summary>
/// A region of the plan that the model encloses, before anything has decided
/// whether it is a room.
///
/// This type is deliberately neutral. A lift shaft, a duct chase, a sliver
/// between two walls and a bedroom all arrive here identically, because at this
/// stage they are identical: bounded areas the walls happen to produce. Deciding
/// which of them a person would call a room is a separate job done by rules that
/// can be read, tested and argued with, and keeping that decision out of this
/// type is what makes those rules inspectable.
///
/// It is also a separate type from <see cref="GranularRoom"/> on purpose. A
/// candidate cannot be exported as a room by mistake, because the export takes
/// rooms and nothing turns one into the other except the qualification step.
///
/// All measurements are in Revit's internal units: feet and square feet.
/// </summary>
public sealed class CandidateRegion
{
    /// <param name="loops">
    /// Every boundary loop of the region, in the order extracted. A region with
    /// an interior void reports the void as a further loop, and the model
    /// contains several, so all of them are kept.
    /// </param>
    /// <param name="closureToleranceInternalFeet">
    /// What this region's caller counts as one physical location recorded twice.
    /// Recorded here so that every area and enclosure claim the region makes
    /// stays attached to the tolerance that justifies it.
    /// </param>
    public CandidateRegion(RegionId id, IReadOnlyList<BoundaryLoop> loops, double closureToleranceInternalFeet)
    {
        ArgumentNullException.ThrowIfNull(loops);

        if (loops.Count == 0)
        {
            throw new ArgumentException("A region needs at least one boundary loop.", nameof(loops));
        }

        if (closureToleranceInternalFeet < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(closureToleranceInternalFeet),
                closureToleranceInternalFeet,
                "Tolerance cannot be negative.");
        }

        Id = id;
        Loops = loops;
        ClosureToleranceInternalFeet = closureToleranceInternalFeet;

        var areas = new AreaMeasurement[loops.Count];
        for (int i = 0; i < loops.Count; i++)
        {
            areas[i] = PlanArea.OfLoop(loops[i], closureToleranceInternalFeet);
        }

        LoopAreas = areas;
        IsEnclosed = areas.All(a => a.IsMeasured);

        if (!IsEnclosed)
        {
            // Without a measurable area there is no basis for saying which loop
            // encloses the others, so no outer loop is offered rather than one
            // being guessed from extraction order.
            OuterLoop = null;
            InnerLoops = Array.Empty<BoundaryLoop>();
            NetArea = AreaMeasurement.NotEnclosed(closureToleranceInternalFeet, LargestGapInternalFeet);
            return;
        }

        int outerIndex = IndexOfLargest(areas);
        OuterLoop = loops[outerIndex];
        InnerLoops = loops.Where((_, i) => i != outerIndex).ToList();

        double net = areas[outerIndex].InternalSquareFeet;
        for (int i = 0; i < areas.Length; i++)
        {
            if (i != outerIndex)
            {
                net -= areas[i].InternalSquareFeet;
            }
        }

        if (net < 0)
        {
            // Interior voids sit inside the loop that encloses them, so they
            // cannot together exceed it. Reaching this means the assumption that
            // the largest loop is the outer one does not hold for this region,
            // and the honest response is to say so rather than to report an area
            // derived from a classification known to be wrong.
            throw new InvalidOperationException(string.Create(
                CultureInfo.InvariantCulture,
                $"Region {Id} has interior loops totalling more area than its largest loop ({net:0.######} sq ft net), so its loops cannot be classified by size."));
        }

        NetArea = AreaMeasurement.Measured(
            net,
            areas[outerIndex].Winding,
            closureToleranceInternalFeet,
            LargestGapInternalFeet);
    }

    public RegionId Id { get; }

    /// <summary>Every boundary loop, in the order extracted.</summary>
    public IReadOnlyList<BoundaryLoop> Loops { get; }

    /// <summary>Each loop's own enclosed area, parallel to <see cref="Loops"/>.</summary>
    public IReadOnlyList<AreaMeasurement> LoopAreas { get; }

    public double ClosureToleranceInternalFeet { get; }

    /// <summary>
    /// Whether every loop closes within the stated tolerance. A region that does
    /// not is not thereby disqualified - that is a rule's decision - but it has
    /// no area, and the openings are available as evidence.
    /// </summary>
    public bool IsEnclosed { get; }

    /// <summary>
    /// The loop that encloses the region, or null when enclosure could not be
    /// established.
    ///
    /// Identified as the loop of greatest area. Revit conventionally returns the
    /// outer loop first and counter-clockwise, but ordering and winding are
    /// conventions rather than guarantees, whereas a void being smaller than
    /// what contains it is a geometric fact. The winding is measured too, so the
    /// convention can be checked against real models rather than relied on.
    /// </summary>
    public BoundaryLoop? OuterLoop { get; }

    /// <summary>
    /// The remaining loops: interior voids. Empty when enclosure could not be
    /// established, since the classification depends on it.
    /// </summary>
    public IReadOnlyList<BoundaryLoop> InnerLoops { get; }

    /// <summary>
    /// The area actually occupied: the outer loop less its interior voids.
    /// Unmeasured when the region is not enclosed, so an unknown area can never
    /// be read as zero.
    /// </summary>
    public AreaMeasurement NetArea { get; }

    /// <summary>The widest discontinuity anywhere in the region's boundary.</summary>
    public double LargestGapInternalFeet => Loops.Max(l => l.LargestGapInternalFeet);

    /// <summary>
    /// The loops that do not close within the stated tolerance, as evidence for
    /// why the region is not enclosed.
    /// </summary>
    public IReadOnlyList<BoundaryLoop> OpenLoops =>
        Loops.Where(l => !l.IsClosedWithin(ClosureToleranceInternalFeet)).ToList();

    /// <summary>
    /// Boundary Revit attributed to no element, across every loop. Real edges
    /// that cannot be reported as model elements, surfaced rather than dropped.
    /// </summary>
    public IReadOnlyList<BoundarySegment> UnattributedSegments =>
        Loops.SelectMany(l => l.UnattributedSegments).ToList();

    /// <summary>
    /// The distinct elements that bound this region, in the order they first
    /// appear along the boundary.
    ///
    /// First-appearance order rather than sorted, because it is derived from the
    /// extracted geometry and so is reproducible, and because it keeps a wall's
    /// position in the boundary meaningful. Unattributed segments are excluded
    /// here and reported separately; they are boundary, but not elements.
    /// </summary>
    public IReadOnlyList<BoundaryReference> BoundingReferences
    {
        get
        {
            var seen = new HashSet<BoundaryReference>();
            var ordered = new List<BoundaryReference>();

            foreach (BoundaryLoop loop in Loops)
            {
                foreach (BoundarySegment segment in loop.Segments)
                {
                    if (segment.Reference.IsAttributed && seen.Add(segment.Reference))
                    {
                        ordered.Add(segment.Reference);
                    }
                }
            }

            return ordered;
        }
    }

    private static int IndexOfLargest(IReadOnlyList<AreaMeasurement> areas)
    {
        int largest = 0;
        for (int i = 1; i < areas.Count; i++)
        {
            if (areas[i].InternalSquareFeet > areas[largest].InternalSquareFeet)
            {
                largest = i;
            }
        }

        return largest;
    }

    public override string ToString() => string.Create(
        CultureInfo.InvariantCulture,
        $"{Id}: {Loops.Count} loop(s), {NetArea}");
}
