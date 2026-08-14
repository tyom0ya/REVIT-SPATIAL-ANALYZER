namespace SpatialAnalyzer.Core.Geometry;

/// <summary>One curve of a boundary together with the element that produced it.</summary>
public sealed record BoundarySegment(BoundaryCurve Curve, BoundaryReference Reference);

/// <summary>
/// An ordered chain of boundary segments, as extracted.
///
/// This type measures whether the chain closes and by how much it misses. It
/// offers no way to make it close, and that absence is the design.
///
/// The rule this enforces is the one the project cannot compromise on. Two
/// regions separated by a real opening are connected, and no amount of
/// convenience justifies inserting an edge to separate them. A discontinuity
/// found here is evidence about the building: it may be a drafting artefact of
/// a fraction of a millimetre, or it may be a doorway-sized hole, and those
/// require opposite responses. Deciding which is a judgement made in the open,
/// against a tolerance the caller states, not something this type does quietly
/// on everyone's behalf.
///
/// Segment order is preserved exactly as extracted, because the chain's
/// sequence is what makes the gap measurements meaningful.
/// </summary>
public sealed class BoundaryLoop
{
    public BoundaryLoop(IReadOnlyList<BoundarySegment> segments)
    {
        if (segments.Count == 0)
        {
            throw new ArgumentException("A boundary loop needs at least one segment.", nameof(segments));
        }

        Segments = segments;
        JunctionGaps = MeasureJunctionGaps(segments);
    }

    public IReadOnlyList<BoundarySegment> Segments { get; }

    /// <summary>
    /// The distance between the end of each segment and the start of the next,
    /// wrapping from the last segment back to the first. One entry per
    /// junction, in segment order, so a discontinuity can be pointed at rather
    /// than merely counted.
    /// </summary>
    public IReadOnlyList<double> JunctionGaps { get; }

    public double LargestGapInternalFeet => JunctionGaps.Max();

    /// <summary>
    /// Whether every junction closes within a tolerance the caller supplies.
    ///
    /// There is no default. What counts as closed depends on what the caller is
    /// deciding: a tolerance suited to reconciling how one location is
    /// represented is not one that should determine whether a room is enclosed.
    /// Requiring the number to be stated at the call site keeps that choice
    /// visible.
    /// </summary>
    public bool IsClosedWithin(double tolerance)
    {
        if (tolerance < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(tolerance), tolerance, "Tolerance cannot be negative.");
        }

        return LargestGapInternalFeet <= tolerance;
    }

    /// <summary>
    /// The junctions that exceed the given tolerance, by index into
    /// <see cref="Segments"/>: index i is the junction between segment i and
    /// the one after it. Reported so a caller can describe exactly where a
    /// boundary is discontinuous.
    /// </summary>
    public IReadOnlyList<int> JunctionsExceeding(double tolerance)
    {
        if (tolerance < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(tolerance), tolerance, "Tolerance cannot be negative.");
        }

        var indices = new List<int>();
        for (int i = 0; i < JunctionGaps.Count; i++)
        {
            if (JunctionGaps[i] > tolerance)
            {
                indices.Add(i);
            }
        }

        return indices;
    }

    /// <summary>
    /// Segments Revit attributed to no element. Real boundary that cannot be
    /// reported as a model element, surfaced rather than dropped.
    /// </summary>
    public IReadOnlyList<BoundarySegment> UnattributedSegments =>
        Segments.Where(s => !s.Reference.IsAttributed).ToList();

    private static IReadOnlyList<double> MeasureJunctionGaps(IReadOnlyList<BoundarySegment> segments)
    {
        var gaps = new double[segments.Count];
        for (int i = 0; i < segments.Count; i++)
        {
            Point2D end = segments[i].Curve.End;
            Point2D nextStart = segments[(i + 1) % segments.Count].Curve.Start;
            gaps[i] = end.DistanceTo(nextStart);
        }

        return gaps;
    }
}
