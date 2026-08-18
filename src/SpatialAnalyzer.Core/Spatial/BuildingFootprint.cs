using SpatialAnalyzer.Core.Geometry;

namespace SpatialAnalyzer.Core.Spatial;

/// <summary>
/// The outline a floor's structure occupies, as a polygon.
///
/// Built from a cloud of points sampled off everything that holds the building
/// up or encloses it - walls, columns, curtain panels - rather than from the
/// spaces between them. That distinction is what makes it work where an
/// approach based on enclosed rooms does not: a facade whose walls never quite
/// close a loop with the partitions behind it still contributes its points, and
/// still shapes the outline.
///
/// A convex hull comes first as a rough envelope, then it is dug inwards until
/// it follows the recesses, entrance bays and L-shapes the building actually
/// has. A convex hull alone would bridge every courtyard and re-entrant corner
/// and call the walls behind them exterior.
/// </summary>
public sealed class BuildingFootprint
{
    private BuildingFootprint(IReadOnlyList<Point2D> outline)
    {
        Outline = outline;
    }

    /// <summary>The boundary, anticlockwise. Empty when there was too little to build one from.</summary>
    public IReadOnlyList<Point2D> Outline { get; }

    public bool IsUsable => Outline.Count >= 3;

    /// <param name="reachInFeet">
    /// How deep a recess the outline is allowed to follow. An edge longer than
    /// this is a candidate for being dug into; shorter ones are left alone.
    /// Too small and the outline frays around every doorway; too large and it
    /// bridges the courtyards it exists to follow.
    /// </param>
    public static BuildingFootprint Around(IReadOnlyList<Point2D> cloud, double reachInFeet)
    {
        ArgumentNullException.ThrowIfNull(cloud);

        if (double.IsNaN(reachInFeet) || reachInFeet <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(reachInFeet),
                reachInFeet,
                "The reach must be a positive number.");
        }

        List<Point2D> distinct = cloud.Distinct().ToList();

        if (distinct.Count < 3)
        {
            return new BuildingFootprint(Array.Empty<Point2D>());
        }

        List<Point2D> hull = ConvexHull(distinct);

        return hull.Count < 3
            ? new BuildingFootprint(Array.Empty<Point2D>())
            : new BuildingFootprint(DigInwards(hull, distinct, reachInFeet));
    }

    /// <summary>Andrew's monotone chain, anticlockwise.</summary>
    private static List<Point2D> ConvexHull(List<Point2D> points)
    {
        List<Point2D> sorted = points.OrderBy(p => p.X).ThenBy(p => p.Y).ToList();

        var lower = new List<Point2D>();
        foreach (Point2D p in sorted)
        {
            while (lower.Count >= 2 && Cross(lower[^2], lower[^1], p) <= 0)
            {
                lower.RemoveAt(lower.Count - 1);
            }

            lower.Add(p);
        }

        var upper = new List<Point2D>();
        for (int i = sorted.Count - 1; i >= 0; i--)
        {
            Point2D p = sorted[i];
            while (upper.Count >= 2 && Cross(upper[^2], upper[^1], p) <= 0)
            {
                upper.RemoveAt(upper.Count - 1);
            }

            upper.Add(p);
        }

        lower.RemoveAt(lower.Count - 1);
        upper.RemoveAt(upper.Count - 1);
        lower.AddRange(upper);

        return lower;
    }

    private static double Cross(Point2D o, Point2D a, Point2D b) =>
        ((a.X - o.X) * (b.Y - o.Y)) - ((a.Y - o.Y) * (b.X - o.X));

    /// <summary>
    /// Pulls the hull in towards the points it skipped over.
    ///
    /// Each edge long enough to be spanning a recess looks for the nearest
    /// point not already on the outline. If that point is close enough to the
    /// edge to be part of the same wall run, the edge is replaced by two edges
    /// through it. Repeating this walks the outline into every bay and notch
    /// the structure has, and stops where there is nothing left to reach.
    /// </summary>
    private static List<Point2D> DigInwards(List<Point2D> hull, List<Point2D> cloud, double reach)
    {
        var outline = new List<Point2D>(hull);
        var used = new HashSet<Point2D>(hull);

        // Bounded so a pathological cloud cannot spin here. Each pass can only
        // add points, and there are finitely many.
        int allowed = cloud.Count * 2;

        bool dugSomething = true;
        while (dugSomething && allowed-- > 0)
        {
            dugSomething = false;

            for (int i = 0; i < outline.Count; i++)
            {
                Point2D a = outline[i];
                Point2D b = outline[(i + 1) % outline.Count];

                if (a.DistanceTo(b) <= reach)
                {
                    continue;
                }

                Point2D? dig = NearestTo(a, b, cloud, used, reach, outline);
                if (dig is not Point2D p)
                {
                    continue;
                }

                outline.Insert(i + 1, p);
                used.Add(p);
                dugSomething = true;
                break;
            }
        }

        return outline;
    }

    /// <summary>
    /// The unused point nearest an edge, if it is near enough to belong to it.
    ///
    /// "Near enough" is judged against the edge's own length rather than a
    /// fixed distance: a long edge spanning the front of a building may legally
    /// reach a long way in to find the back of an entrance bay, while a short
    /// one should not wander.
    /// </summary>
    private static Point2D? NearestTo(
        Point2D a,
        Point2D b,
        List<Point2D> cloud,
        HashSet<Point2D> used,
        double reach,
        List<Point2D> outline)
    {
        double span = a.DistanceTo(b);
        double nearest = double.MaxValue;
        Point2D? found = null;

        foreach (Point2D p in cloud)
        {
            if (used.Contains(p))
            {
                continue;
            }

            double away = DistanceToSegment(p, a, b);

            // Must be within reach of this edge, not merely nearer than the
            // edge is long. Without the cap the outline reaches deep into the
            // plan and hooks the end of any partition that happens to stop a
            // few feet short of the facade, which then reads as facade itself.
            // Deeper recesses are still followed, one step at a time: each dig
            // shortens the edges and brings the next points within reach.
            if (away >= reach || away >= span || away >= nearest || away <= 0)
            {
                continue;
            }

            // And must be a real detour rather than a point sitting on the edge
            // already, which would add a vertex and change nothing.
            if (away < reach / 10.0)
            {
                continue;
            }

            // And must belong to this edge rather than to another part of the
            // outline. A point sitting on the left wall is only a short step
            // from the near end of the bottom wall, and without this it gets
            // dug into the bottom wall and folds the outline through the
            // building - which on a simple rectangle was enough to make every
            // wall of it read as interior.
            if (!BelongsToThisEdge(p, a, b, outline, away))
            {
                continue;
            }

            nearest = away;
            found = p;
        }

        return found;
    }

    /// <summary>
    /// Whether this edge is the nearest part of the outline to the point.
    ///
    /// A point already lying against some other edge is that edge's business.
    /// Digging it into this one drags the outline across the building instead
    /// of into a recess.
    /// </summary>
    private static bool BelongsToThisEdge(
        Point2D p,
        Point2D a,
        Point2D b,
        List<Point2D> outline,
        double away)
    {
        for (int i = 0; i < outline.Count; i++)
        {
            Point2D from = outline[i];
            Point2D to = outline[(i + 1) % outline.Count];

            if (from.Equals(a) && to.Equals(b))
            {
                continue;
            }

            if (DistanceToSegment(p, from, to) < away)
            {
                return false;
            }
        }

        return true;
    }

    public static double DistanceToSegment(Point2D p, Point2D a, Point2D b)
    {
        double dx = b.X - a.X;
        double dy = b.Y - a.Y;
        double square = (dx * dx) + (dy * dy);

        if (square <= 0)
        {
            return p.DistanceTo(a);
        }

        double t = Math.Clamp((((p.X - a.X) * dx) + ((p.Y - a.Y) * dy)) / square, 0, 1);

        return p.DistanceTo(new Point2D(a.X + (t * dx), a.Y + (t * dy)));
    }

    /// <summary>How far a point is from the outline itself, inside or out.</summary>
    public double DistanceToBoundary(Point2D point)
    {
        if (!IsUsable)
        {
            return double.MaxValue;
        }

        double nearest = double.MaxValue;

        for (int i = 0; i < Outline.Count; i++)
        {
            nearest = Math.Min(nearest, DistanceToSegment(point, Outline[i], Outline[(i + 1) % Outline.Count]));
        }

        return nearest;
    }

    public bool Contains(Point2D point)
    {
        if (!IsUsable)
        {
            return false;
        }

        bool inside = false;

        for (int i = 0, j = Outline.Count - 1; i < Outline.Count; j = i++)
        {
            Point2D a = Outline[i];
            Point2D b = Outline[j];

            if ((a.Y > point.Y) == (b.Y > point.Y))
            {
                continue;
            }

            double at = ((b.X - a.X) * (point.Y - a.Y) / (b.Y - a.Y)) + a.X;
            if (point.X < at)
            {
                inside = !inside;
            }
        }

        return inside;
    }
}
