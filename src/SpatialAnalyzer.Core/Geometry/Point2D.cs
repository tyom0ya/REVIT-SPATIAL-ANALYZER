using System.Globalization;

namespace SpatialAnalyzer.Core.Geometry;

/// <summary>
/// A point on the plan, in Revit's internal length unit.
///
/// That unit is decimal feet whatever the project displays, and values are kept
/// in it deliberately: converting for presentation is cheap and reversible,
/// whereas converting on the way in loses precision that later comparisons
/// depend on.
///
/// Plan analysis is two dimensional. Elevation is a property of the level being
/// analysed rather than of each boundary point, so carrying a Z here would
/// invite comparisons between points at different heights that mean nothing.
/// </summary>
public readonly record struct Point2D(double X, double Y)
{
    public double DistanceTo(Point2D other)
    {
        double dx = X - other.X;
        double dy = Y - other.Y;
        return Math.Sqrt((dx * dx) + (dy * dy));
    }

    /// <summary>
    /// Whether two points are near enough to be treated as the same location,
    /// within a tolerance the caller must supply.
    ///
    /// There is no default tolerance, on purpose. A tolerance is a claim about
    /// what counts as the same physical place, and that claim belongs to
    /// whoever is making it, in view of what they are comparing. Any tolerance
    /// used here reconciles the representation of one location; it is never a
    /// licence to treat two genuinely separate places as one, which is what
    /// would happen if an opening in a building were closed by widening a
    /// number.
    /// </summary>
    public bool IsWithin(Point2D other, double tolerance)
    {
        if (tolerance < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(tolerance), tolerance, "Tolerance cannot be negative.");
        }

        return DistanceTo(other) <= tolerance;
    }

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"({X:0.######}, {Y:0.######})");
}
