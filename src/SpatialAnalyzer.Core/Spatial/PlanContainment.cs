using SpatialAnalyzer.Core.Geometry;

namespace SpatialAnalyzer.Core.Spatial;

/// <summary>
/// Where a point stands in relation to a region.
/// </summary>
public enum Containment
{
    /// <summary>
    /// The question could not be answered. A region whose boundary does not
    /// close has no inside: the space carries on past the opening, so asking
    /// whether a point is within it presumes something untrue. The default
    /// value on purpose, so an uninitialised answer never reads as "outside".
    /// </summary>
    Indeterminate,

    Outside,

    /// <summary>
    /// On the boundary itself, within the stated tolerance.
    ///
    /// Kept separate rather than forced into one side or the other. A radiator
    /// against a wall, a door in it, a skirting board - these sit exactly where
    /// the answer is genuinely ambiguous, and it is a decision about the
    /// building which room they belong to, not a question of arithmetic.
    /// Deciding it here by whichever way the rounding fell would bury that.
    /// </summary>
    OnBoundary,

    Inside,
}

/// <summary>
/// Answers whether a point lies within a region.
///
/// The method is even-odd ray casting: count how many times a ray from the point
/// crosses the boundary, and an odd count means inside. It runs over the
/// tessellation rather than the segment ends, so a curved boundary is followed
/// as closely as Revit drew it - the model has a room bounded by three arcs, and
/// treating those as chords would put points near the curve on the wrong side.
///
/// Interior voids are subtracted, not ignored. A point in the void inside a room
/// is not in the room, and the model has several: one region carries two column
/// enclosures within its outer boundary.
///
/// A point on the boundary is reported as such instead of being pushed to one
/// side. Ray casting is at its least reliable exactly there, and the elements
/// that sit there - things against or inside walls - are the ones where the
/// answer matters and should be made deliberately.
/// </summary>
public static class PlanContainment
{
    /// <param name="toleranceInternalFeet">
    /// How close to the boundary counts as on it. No default: how near a wall
    /// something has to be before "which side" stops being a meaningful question
    /// depends on what is being asked, and the caller knows that.
    /// </param>
    public static Containment Of(CandidateRegion region, Point2D point, double toleranceInternalFeet)
    {
        ArgumentNullException.ThrowIfNull(region);

        if (toleranceInternalFeet < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(toleranceInternalFeet),
                toleranceInternalFeet,
                "Tolerance cannot be negative.");
        }

        if (region.OuterLoop is null)
        {
            return Containment.Indeterminate;
        }

        // Tested before anything else. A point on the boundary would otherwise
        // get whichever answer the arithmetic happened to produce, and the
        // whole reason for reporting it separately is that it has no answer.
        foreach (BoundaryLoop loop in region.Loops)
        {
            if (IsOn(loop, point, toleranceInternalFeet))
            {
                return Containment.OnBoundary;
            }
        }

        if (!IsWithin(region.OuterLoop, point))
        {
            return Containment.Outside;
        }

        foreach (BoundaryLoop inner in region.InnerLoops)
        {
            if (IsWithin(inner, point))
            {
                return Containment.Outside;
            }
        }

        return Containment.Inside;
    }

    /// <summary>
    /// Even-odd ray casting along the positive X direction.
    ///
    /// The comparison is deliberately asymmetric - one end of each edge counts
    /// as above the ray and the other does not - so that a ray passing exactly
    /// through a vertex crosses the two edges meeting there once in total rather
    /// than twice or not at all. Without that, points level with a corner get
    /// the wrong answer, and buildings are full of corners at round coordinates.
    /// </summary>
    private static bool IsWithin(BoundaryLoop loop, Point2D point)
    {
        IReadOnlyList<Point2D> ring = Ring(loop);

        bool inside = false;
        for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
        {
            Point2D a = ring[i];
            Point2D b = ring[j];

            if ((a.Y > point.Y) == (b.Y > point.Y))
            {
                continue;
            }

            double crossingX = ((b.X - a.X) * (point.Y - a.Y) / (b.Y - a.Y)) + a.X;
            if (point.X < crossingX)
            {
                inside = !inside;
            }
        }

        return inside;
    }

    private static bool IsOn(BoundaryLoop loop, Point2D point, double tolerance)
    {
        IReadOnlyList<Point2D> ring = Ring(loop);

        for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
        {
            if (DistanceToSegment(point, ring[j], ring[i]) <= tolerance)
            {
                return true;
            }
        }

        return false;
    }

    private static double DistanceToSegment(Point2D point, Point2D from, Point2D to)
    {
        double dx = to.X - from.X;
        double dy = to.Y - from.Y;
        double lengthSquared = (dx * dx) + (dy * dy);

        if (lengthSquared == 0)
        {
            // A segment of no length is a point, which happens where one
            // boundary curve meets the next.
            return point.DistanceTo(from);
        }

        // How far along the segment the nearest point lies, clamped so that a
        // point beyond either end measures to that end rather than to the
        // infinite line through them.
        double along = (((point.X - from.X) * dx) + ((point.Y - from.Y) * dy)) / lengthSquared;
        along = Math.Clamp(along, 0, 1);

        return point.DistanceTo(new Point2D(from.X + (along * dx), from.Y + (along * dy)));
    }

    /// <summary>
    /// Every tessellation point of the loop, in order. Points repeated where one
    /// segment meets the next are harmless: a zero-length edge crosses no ray
    /// and measures as a point.
    /// </summary>
    private static IReadOnlyList<Point2D> Ring(BoundaryLoop loop)
    {
        var ring = new List<Point2D>();
        foreach (BoundarySegment segment in loop.Segments)
        {
            foreach (Point2D point in segment.Curve.Tessellation)
            {
                ring.Add(point);
            }
        }

        return ring;
    }
}
