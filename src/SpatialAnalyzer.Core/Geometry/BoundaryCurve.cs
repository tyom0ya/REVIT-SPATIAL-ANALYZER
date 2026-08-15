using System.Globalization;

namespace SpatialAnalyzer.Core.Geometry;

/// <summary>
/// What shape a boundary segment actually is.
///
/// Recorded rather than inferred, because the model contains arcs and treating
/// every segment as a line would quietly straighten a curved wall and change
/// the area it encloses.
/// </summary>
public enum BoundaryCurveKind
{
    Line,
    Arc,

    /// <summary>
    /// Anything else Revit produced: splines and the like. The shape is carried
    /// by the tessellation rather than by parameters this project would have to
    /// reimplement.
    /// </summary>
    Other,
}

/// <summary>
/// One curve of a room boundary, copied out of Revit into plain data.
///
/// A note on tessellation, which is a numerical approximation and must not be
/// confused with the rule about physical gaps. Tessellation replaces a curve
/// with a chain of points that follows it as closely as the source chose to;
/// it changes how finely a shape is described. Closing a gap changes whether
/// two spaces are connected. The first is a fidelity trade-off and is fine to
/// make. The second alters what the building is, and this project does not do
/// it. Tessellating more coarsely can never be a reason to treat an opening as
/// closed.
///
/// The tessellation is taken from Revit rather than computed here, so no curve
/// mathematics is reimplemented and the points are the ones Revit itself would
/// draw.
/// </summary>
public sealed record BoundaryCurve
{
    public BoundaryCurve(
        BoundaryCurveKind kind,
        Point2D start,
        Point2D end,
        double lengthInternalFeet,
        IReadOnlyList<Point2D> tessellation)
    {
        if (lengthInternalFeet < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(lengthInternalFeet), lengthInternalFeet, "Length cannot be negative.");
        }

        if (tessellation.Count < 2)
        {
            throw new ArgumentException("A boundary curve needs at least a start and an end point.", nameof(tessellation));
        }

        Kind = kind;
        Start = start;
        End = end;
        LengthInternalFeet = lengthInternalFeet;
        Tessellation = tessellation;
    }

    public BoundaryCurveKind Kind { get; }

    public Point2D Start { get; }

    public Point2D End { get; }

    /// <summary>
    /// Length along the curve, not the straight distance between its ends. For
    /// an arc the two differ, and the difference is the point of recording it.
    /// </summary>
    public double LengthInternalFeet { get; }

    /// <summary>
    /// The curve as a point chain, beginning at <see cref="Start"/> and ending
    /// at <see cref="End"/>. A straight segment needs only those two.
    /// </summary>
    public IReadOnlyList<Point2D> Tessellation { get; }

    /// <summary>
    /// True when the curve deviates from the straight line between its ends,
    /// which is the case worth knowing about when deciding whether a simplified
    /// representation would misrepresent the space.
    /// </summary>
    public bool IsCurved => Kind != BoundaryCurveKind.Line;

    public static BoundaryCurve Straight(Point2D start, Point2D end) =>
        new(BoundaryCurveKind.Line, start, end, start.DistanceTo(end), new[] { start, end });

    public override string ToString() => string.Create(
        CultureInfo.InvariantCulture,
        $"{Kind} {Start} -> {End} ({LengthInternalFeet:0.####} ft)");
}
