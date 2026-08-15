using System.Globalization;

namespace SpatialAnalyzer.Core.Geometry;

/// <summary>
/// The smallest axis-aligned rectangle containing a set of points.
///
/// Used to rule regions out cheaply. Deciding whether a point lies inside a
/// boundary means walking every edge of it, and a level has thirty regions with
/// hundreds of edges between them; comparing four numbers first discards almost
/// all of them for the cost of nothing.
///
/// It can only ever rule out, never rule in. A point inside the rectangle may
/// still be outside the boundary - an L-shaped room is the obvious case - so
/// this narrows the question and the real test answers it.
/// </summary>
public readonly record struct PlanBounds
{
    public PlanBounds(double minX, double minY, double maxX, double maxY)
    {
        if (maxX < minX || maxY < minY)
        {
            throw new ArgumentException("The maximum corner cannot be below the minimum corner.");
        }

        MinX = minX;
        MinY = minY;
        MaxX = maxX;
        MaxY = maxY;
    }

    public double MinX { get; }

    public double MinY { get; }

    public double MaxX { get; }

    public double MaxY { get; }

    public double Width => MaxX - MinX;

    public double Height => MaxY - MinY;

    public static PlanBounds Around(IEnumerable<Point2D> points)
    {
        ArgumentNullException.ThrowIfNull(points);

        double minX = double.MaxValue;
        double minY = double.MaxValue;
        double maxX = double.MinValue;
        double maxY = double.MinValue;
        bool any = false;

        foreach (Point2D point in points)
        {
            any = true;
            minX = Math.Min(minX, point.X);
            minY = Math.Min(minY, point.Y);
            maxX = Math.Max(maxX, point.X);
            maxY = Math.Max(maxY, point.Y);
        }

        if (!any)
        {
            throw new ArgumentException("Cannot bound an empty set of points.", nameof(points));
        }

        return new PlanBounds(minX, minY, maxX, maxY);
    }

    /// <summary>
    /// Whether a point falls within the rectangle, grown by the tolerance.
    ///
    /// The margin matters: a point just outside a boundary may still be on it
    /// within tolerance, and a rectangle tested exactly would discard the region
    /// before anything had the chance to say so.
    /// </summary>
    public bool Contains(Point2D point, double tolerance)
    {
        if (tolerance < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(tolerance), tolerance, "Tolerance cannot be negative.");
        }

        return point.X >= MinX - tolerance
            && point.X <= MaxX + tolerance
            && point.Y >= MinY - tolerance
            && point.Y <= MaxY + tolerance;
    }

    public override string ToString() => string.Create(
        CultureInfo.InvariantCulture,
        $"({MinX:0.###}, {MinY:0.###}) to ({MaxX:0.###}, {MaxY:0.###})");
}
