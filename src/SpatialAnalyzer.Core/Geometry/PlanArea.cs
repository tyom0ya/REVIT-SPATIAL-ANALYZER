namespace SpatialAnalyzer.Core.Geometry;

/// <summary>
/// Measures the plan area a boundary encloses.
///
/// Closure is a precondition, not a detail. The area of a ring of points is
/// computed by treating the last point as joined to the first, so asking for the
/// area of a boundary interrupted by a doorway would silently draw an edge
/// across that doorway and return a confident number for a room that does not
/// exist. This class refuses instead, and reports the gap at its real size.
///
/// That refusal is the gap rule applied to measurement. Nothing here closes,
/// snaps or bridges anything; when the caller states a tolerance and the
/// boundary meets it, the implicit closing edge is by definition no longer than
/// that tolerance, which is what makes the result honest.
///
/// For curved boundaries the measurement is taken from the tessellation Revit
/// itself produced, so no curve mathematics is reimplemented here. A polygon
/// through points on a convex arc lies inside it, so such areas are understated
/// very slightly - a fidelity trade-off of the same kind as tessellation itself,
/// and unrelated to the question of whether a space is enclosed.
/// </summary>
public static class PlanArea
{
    /// <summary>
    /// Measures one boundary loop.
    ///
    /// The tolerance has no default, for the same reason it has none anywhere
    /// else in this project: it is a claim about what counts as the same
    /// physical location, and the caller has to make it in the open.
    /// </summary>
    public static AreaMeasurement OfLoop(BoundaryLoop loop, double toleranceInternalFeet)
    {
        ArgumentNullException.ThrowIfNull(loop);

        if (toleranceInternalFeet < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(toleranceInternalFeet),
                toleranceInternalFeet,
                "Tolerance cannot be negative.");
        }

        double largestGap = loop.LargestGapInternalFeet;

        if (!loop.IsClosedWithin(toleranceInternalFeet))
        {
            return AreaMeasurement.NotEnclosed(toleranceInternalFeet, largestGap);
        }

        double signed = SignedArea(loop);

        LoopWinding winding = signed switch
        {
            > 0 => LoopWinding.CounterClockwise,
            < 0 => LoopWinding.Clockwise,
            _ => LoopWinding.Degenerate,
        };

        return AreaMeasurement.Measured(Math.Abs(signed), winding, toleranceInternalFeet, largestGap);
    }

    /// <summary>
    /// The shoelace formula over every tessellation point in the loop, in the
    /// order they were extracted.
    ///
    /// Points repeated where one segment meets the next contribute nothing, so
    /// the junctions need no special handling. The sign carries the direction
    /// the boundary was drawn in.
    /// </summary>
    private static double SignedArea(BoundaryLoop loop)
    {
        var ring = new List<Point2D>();
        foreach (BoundarySegment segment in loop.Segments)
        {
            foreach (Point2D point in segment.Curve.Tessellation)
            {
                ring.Add(point);
            }
        }

        double sum = 0;
        for (int i = 0; i < ring.Count; i++)
        {
            Point2D current = ring[i];
            Point2D next = ring[(i + 1) % ring.Count];
            sum += (current.X * next.Y) - (next.X * current.Y);
        }

        return sum / 2.0;
    }
}
