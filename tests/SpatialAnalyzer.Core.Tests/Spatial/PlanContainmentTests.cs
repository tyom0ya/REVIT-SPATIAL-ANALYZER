using SpatialAnalyzer.Core.Domain;
using SpatialAnalyzer.Core.Geometry;
using SpatialAnalyzer.Core.Spatial;

namespace SpatialAnalyzer.Core.Tests.Spatial;

public class PlanContainmentTests
{
    private const double Tolerance = 1.0 / 384.0;   // Revit's ShortCurveTolerance
    private const double Exact = 1e-9;

    private static long _nextId = 20000;

    private static BoundaryLoop Loop(params Point2D[] corners)
    {
        var segments = new List<BoundarySegment>(corners.Length);
        for (int i = 0; i < corners.Length; i++)
        {
            segments.Add(new BoundarySegment(
                BoundaryCurve.Straight(corners[i], corners[(i + 1) % corners.Length]),
                BoundaryReference.Host(new RevitElementId(Interlocked.Increment(ref _nextId)))));
        }

        return new BoundaryLoop(segments);
    }

    private static BoundaryLoop Rect(double x0, double y0, double width, double height) =>
        Loop(
            new Point2D(x0, y0),
            new Point2D(x0 + width, y0),
            new Point2D(x0 + width, y0 + height),
            new Point2D(x0, y0 + height));

    private static CandidateRegion Region(params BoundaryLoop[] loops) =>
        new(new RegionId(0), loops, Tolerance);

    [Fact]
    public void APointInTheMiddleIsInside()
    {
        Assert.Equal(
            Containment.Inside,
            PlanContainment.Of(Region(Rect(0, 0, 10, 8)), new Point2D(5, 4), Tolerance));
    }

    [Fact]
    public void APointBeyondTheWallsIsOutside()
    {
        Assert.Equal(
            Containment.Outside,
            PlanContainment.Of(Region(Rect(0, 0, 10, 8)), new Point2D(20, 4), Tolerance));
    }

    [Fact]
    public void APointOnTheBoundaryIsNeither()
    {
        Assert.Equal(
            Containment.OnBoundary,
            PlanContainment.Of(Region(Rect(0, 0, 10, 8)), new Point2D(0, 4), Tolerance));
    }

    [Fact]
    public void APointAtACornerIsOnTheBoundary()
    {
        Assert.Equal(
            Containment.OnBoundary,
            PlanContainment.Of(Region(Rect(0, 0, 10, 8)), new Point2D(10, 8), Tolerance));
    }

    [Fact]
    public void HowCloseCountsAsOnTheBoundaryIsTheCallersToSay()
    {
        var justInside = new Point2D(0.01, 4);

        Assert.Equal(Containment.Inside, PlanContainment.Of(Region(Rect(0, 0, 10, 8)), justInside, Exact));
        Assert.Equal(Containment.OnBoundary, PlanContainment.Of(Region(Rect(0, 0, 10, 8)), justInside, 0.1));
    }

    /// <summary>
    /// The model contains this: one region has two column enclosures inside its
    /// outer boundary. Something standing in a void is not in the room, and a
    /// containment test that ignored the inner loops would put it there.
    /// </summary>
    [Fact]
    public void APointInAnInteriorVoidIsNotInTheRoom()
    {
        CandidateRegion region = Region(Rect(0, 0, 10, 8), Rect(3, 3, 2, 2));

        Assert.Equal(Containment.Outside, PlanContainment.Of(region, new Point2D(4, 4), Tolerance));
        Assert.Equal(Containment.Inside, PlanContainment.Of(region, new Point2D(1, 1), Tolerance));
    }

    /// <summary>
    /// A ray cast from a point level with a corner meets two edges at once.
    /// Counted naively that is either two crossings or none, and the point flips
    /// to the wrong side. Buildings are full of corners at round coordinates, so
    /// this is the ordinary case rather than a curiosity.
    /// </summary>
    [Fact]
    public void APointLevelWithACornerIsStillJudgedCorrectly()
    {
        // A diamond. A ray in the positive X direction from the centre passes
        // exactly through the vertex at (2, 0).
        BoundaryLoop diamond = Loop(
            new Point2D(0, 0),
            new Point2D(1, 1),
            new Point2D(2, 0),
            new Point2D(1, -1));

        Assert.Equal(Containment.Inside, PlanContainment.Of(Region(diamond), new Point2D(1, 0), Tolerance));
        Assert.Equal(Containment.Outside, PlanContainment.Of(Region(diamond), new Point2D(-1, 0), Tolerance));
        Assert.Equal(Containment.Outside, PlanContainment.Of(Region(diamond), new Point2D(3, 0), Tolerance));
    }

    /// <summary>
    /// Rooms are not all convex. The notch of an L is outside the room while
    /// being inside its bounding box, which is what a lazier test would report.
    /// </summary>
    [Fact]
    public void TheNotchOfAnLShapedRoomIsOutsideIt()
    {
        BoundaryLoop lShape = Loop(
            new Point2D(0, 0),
            new Point2D(10, 0),
            new Point2D(10, 4),
            new Point2D(4, 4),
            new Point2D(4, 10),
            new Point2D(0, 10));

        Assert.Equal(Containment.Inside, PlanContainment.Of(Region(lShape), new Point2D(2, 2), Tolerance));
        Assert.Equal(Containment.Inside, PlanContainment.Of(Region(lShape), new Point2D(8, 2), Tolerance));
        Assert.Equal(Containment.Inside, PlanContainment.Of(Region(lShape), new Point2D(2, 8), Tolerance));

        // Inside the bounding box, outside the room.
        Assert.Equal(Containment.Outside, PlanContainment.Of(Region(lShape), new Point2D(8, 8), Tolerance));
    }

    /// <summary>
    /// One region in the model is bounded by three arcs. Following the chords
    /// instead of the tessellation would misplace points near the curve, and by
    /// the width of the bulge rather than by a rounding error.
    /// </summary>
    [Fact]
    public void ACurvedBoundaryIsFollowedRatherThanCutAcross()
    {
        var points = new List<Point2D>();
        for (int i = 0; i <= 64; i++)
        {
            double angle = 2 * Math.PI * i / 64;
            points.Add(new Point2D(10 * Math.Cos(angle), 10 * Math.Sin(angle)));
        }

        var curve = new BoundaryCurve(BoundaryCurveKind.Arc, points[0], points[^1], 2 * Math.PI * 10, points);
        var circle = new BoundaryLoop(new[]
        {
            new BoundarySegment(curve, BoundaryReference.Host(new RevitElementId(1))),
        });

        CandidateRegion region = Region(circle);

        // Near the rim, where a chord between widely spaced points would fall
        // short of the true boundary.
        Assert.Equal(Containment.Inside, PlanContainment.Of(region, new Point2D(9.5, 0), Tolerance));
        Assert.Equal(Containment.Outside, PlanContainment.Of(region, new Point2D(10.5, 0), Tolerance));
        Assert.Equal(Containment.Inside, PlanContainment.Of(region, new Point2D(0, 0), Tolerance));
    }

    /// <summary>
    /// A region open to the space next door has no inside to be in. Answering
    /// "outside" would be a claim; answering "indeterminate" is the truth, and
    /// it is the default value so that an unset result never reads as a verdict.
    /// </summary>
    [Fact]
    public void AnUnenclosedRegionHasNoInside()
    {
        var open = new BoundaryLoop(new[]
        {
            new BoundarySegment(
                BoundaryCurve.Straight(new Point2D(0, 0), new Point2D(10, 0)),
                BoundaryReference.Host(new RevitElementId(1))),
            new BoundarySegment(
                BoundaryCurve.Straight(new Point2D(10, 0), new Point2D(10, 8)),
                BoundaryReference.Host(new RevitElementId(2))),
            new BoundarySegment(
                BoundaryCurve.Straight(new Point2D(10, 8), new Point2D(6.5, 8)),
                BoundaryReference.Host(new RevitElementId(3))),
            // a 3 ft doorway-sized opening
            new BoundarySegment(
                BoundaryCurve.Straight(new Point2D(3.5, 8), new Point2D(0, 8)),
                BoundaryReference.Host(new RevitElementId(4))),
            new BoundarySegment(
                BoundaryCurve.Straight(new Point2D(0, 8), new Point2D(0, 0)),
                BoundaryReference.Host(new RevitElementId(5))),
        });

        Assert.Equal(
            Containment.Indeterminate,
            PlanContainment.Of(new CandidateRegion(new RegionId(1), new[] { open }, Tolerance), new Point2D(5, 4), Tolerance));

        Assert.Equal(Containment.Indeterminate, default(Containment));
    }

    [Fact]
    public void ANegativeToleranceIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => PlanContainment.Of(Region(Rect(0, 0, 10, 8)), new Point2D(5, 4), -1));
    }
}
