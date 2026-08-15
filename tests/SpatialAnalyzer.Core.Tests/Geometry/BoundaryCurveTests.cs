using System.Globalization;
using SpatialAnalyzer.Core.Geometry;

namespace SpatialAnalyzer.Core.Tests.Geometry;

public class BoundaryCurveTests
{
    [Fact]
    public void Straight_TakesItsLengthFromItsEndpoints()
    {
        BoundaryCurve curve = BoundaryCurve.Straight(new Point2D(0, 0), new Point2D(3, 4));

        Assert.Equal(BoundaryCurveKind.Line, curve.Kind);
        Assert.Equal(5.0, curve.LengthInternalFeet, precision: 10);
        Assert.False(curve.IsCurved);
    }

    [Fact]
    public void Straight_NeedsOnlyItsEndpointsToDescribeItself()
    {
        BoundaryCurve curve = BoundaryCurve.Straight(new Point2D(0, 0), new Point2D(1, 0));

        Assert.Equal(2, curve.Tessellation.Count);
        Assert.Equal(curve.Start, curve.Tessellation[0]);
        Assert.Equal(curve.End, curve.Tessellation[^1]);
    }

    /// <summary>
    /// An arc must survive extraction as an arc. Recording it as a line between
    /// the same endpoints would straighten a curved wall and change the area
    /// the boundary encloses, which is a silent misstatement about the
    /// building rather than a rounding difference.
    /// </summary>
    [Fact]
    public void AnArcIsNotRecordedAsALine()
    {
        var start = new Point2D(1, 0);
        var end = new Point2D(0, 1);
        double quarterCircle = Math.PI / 2;

        var curve = new BoundaryCurve(
            BoundaryCurveKind.Arc,
            start,
            end,
            quarterCircle,
            new[] { start, new Point2D(0.7071, 0.7071), end });

        Assert.Equal(BoundaryCurveKind.Arc, curve.Kind);
        Assert.True(curve.IsCurved);

        // The arc is longer than the straight line between its ends, and that
        // difference is exactly what would be lost by flattening it.
        Assert.True(curve.LengthInternalFeet > start.DistanceTo(end));
    }

    [Fact]
    public void OtherCurveKindsAreCarriedByTheirTessellation()
    {
        var points = new[]
        {
            new Point2D(0, 0),
            new Point2D(1, 0.4),
            new Point2D(2, 0.1),
            new Point2D(3, 0.9),
        };

        var curve = new BoundaryCurve(BoundaryCurveKind.Other, points[0], points[^1], 3.4, points);

        Assert.True(curve.IsCurved);
        Assert.Equal(4, curve.Tessellation.Count);
    }

    [Fact]
    public void ACurveNeedsAtLeastTwoPoints()
    {
        Assert.Throws<ArgumentException>(() => new BoundaryCurve(
            BoundaryCurveKind.Line,
            new Point2D(0, 0),
            new Point2D(1, 0),
            1,
            new[] { new Point2D(0, 0) }));
    }

    [Fact]
    public void ANegativeLengthIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new BoundaryCurve(
            BoundaryCurveKind.Line,
            new Point2D(0, 0),
            new Point2D(1, 0),
            -1,
            new[] { new Point2D(0, 0), new Point2D(1, 0) }));
    }

    /// <summary>
    /// A zero length segment is unusual but not invalid: Revit can produce one
    /// where two boundaries meet exactly. Rejecting it would discard evidence
    /// about the model, so it is allowed through and left for the caller to
    /// notice.
    /// </summary>
    /// <summary>
    /// Curves are described in diagnostic reports that get compared between
    /// runs and between machines, so a length must not render as 1,5 on one and
    /// 1.5 on another. Swedish is included because it writes a negative with
    /// U+2212 rather than a hyphen, which is the case that slips past a decimal
    /// separator check.
    /// </summary>
    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    [InlineData("sv-SE")]
    public void ToString_IsCultureInvariant(string culture)
    {
        CultureInfo original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo(culture);

            string text = BoundaryCurve.Straight(new Point2D(-1.5, 0), new Point2D(0, 0)).ToString();

            Assert.Equal("Line (-1.5, 0) -> (0, 0) (1.5 ft)", text);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void AZeroLengthCurveIsAllowedThrough()
    {
        var point = new Point2D(2, 2);

        BoundaryCurve curve = BoundaryCurve.Straight(point, point);

        Assert.Equal(0, curve.LengthInternalFeet);
    }
}
