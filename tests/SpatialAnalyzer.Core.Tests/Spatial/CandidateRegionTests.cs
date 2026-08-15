using SpatialAnalyzer.Core.Domain;
using SpatialAnalyzer.Core.Geometry;
using SpatialAnalyzer.Core.Spatial;

namespace SpatialAnalyzer.Core.Tests.Spatial;

public class CandidateRegionTests
{
    private const double Tolerance = 0.001;   // ~0.3 mm
    private const double DoorwayWidth = 3.0;  // ~915 mm

    private static long _nextId = 1;

    /// <summary>An axis-aligned rectangle, drawn counter-clockwise.</summary>
    private static BoundaryLoop Rect(double x0, double y0, double width, double height)
    {
        var corners = new[]
        {
            new Point2D(x0, y0),
            new Point2D(x0 + width, y0),
            new Point2D(x0 + width, y0 + height),
            new Point2D(x0, y0 + height),
        };

        var segments = new List<BoundarySegment>(4);
        for (int i = 0; i < corners.Length; i++)
        {
            segments.Add(new BoundarySegment(
                BoundaryCurve.Straight(corners[i], corners[(i + 1) % corners.Length]),
                BoundaryReference.Host(new RevitElementId(Interlocked.Increment(ref _nextId)))));
        }

        return new BoundaryLoop(segments);
    }

    private static CandidateRegion Region(params BoundaryLoop[] loops) =>
        new(new RegionId(0), loops, Tolerance);

    [Fact]
    public void ASingleLoopRegionOccupiesTheAreaItEncloses()
    {
        CandidateRegion region = Region(Rect(0, 0, 10, 8));

        Assert.True(region.IsEnclosed);
        Assert.Equal(80.0, region.NetArea.InternalSquareFeet, precision: 9);
        Assert.Empty(region.InnerLoops);
    }

    /// <summary>
    /// Four circuits in the acceptance model report more than one loop, so this
    /// is not a hypothetical case. Counting a void as occupied space would
    /// overstate the room by exactly the void.
    /// </summary>
    [Fact]
    public void AnInteriorVoidIsSubtractedRatherThanCounted()
    {
        CandidateRegion region = Region(Rect(0, 0, 10, 8), Rect(3, 3, 2, 2));

        Assert.Equal(76.0, region.NetArea.InternalSquareFeet, precision: 9);
        Assert.Single(region.InnerLoops);
    }

    /// <summary>
    /// The classification must not rest on Revit returning the outer loop first.
    /// Extraction order is a convention; a void being smaller than what contains
    /// it is a geometric fact, and that is what is used.
    /// </summary>
    [Fact]
    public void TheOuterLoopIsIdentifiedBySizeNotByExtractionOrder()
    {
        BoundaryLoop void_ = Rect(3, 3, 2, 2);
        BoundaryLoop outer = Rect(0, 0, 10, 8);

        // Deliberately the wrong way round.
        CandidateRegion region = Region(void_, outer);

        Assert.Same(outer, region.OuterLoop);
        Assert.Equal(new[] { void_ }, region.InnerLoops);
        Assert.Equal(76.0, region.NetArea.InternalSquareFeet, precision: 9);
    }

    [Fact]
    public void EveryLoopIsKeptInTheOrderItWasExtracted()
    {
        BoundaryLoop first = Rect(3, 3, 2, 2);
        BoundaryLoop second = Rect(0, 0, 10, 8);

        CandidateRegion region = Region(first, second);

        Assert.Equal(new[] { first, second }, region.Loops);
    }

    /// <summary>
    /// A region reachable through a doorway-sized opening is not enclosed, and
    /// nothing about it may be reported as though it were. It is not thereby
    /// disqualified - that is a rule's decision, made later and in the open -
    /// but it has no area to give.
    /// </summary>
    [Fact]
    public void ARegionOpenToTheNextSpaceHasNoAreaAndNoOuterLoop()
    {
        CandidateRegion region = Region(WallsWithARealOpening());

        Assert.False(region.IsEnclosed);
        Assert.Null(region.OuterLoop);
        Assert.Empty(region.InnerLoops);
        Assert.False(region.NetArea.IsMeasured);
        Assert.Throws<InvalidOperationException>(() => region.NetArea.InternalSquareFeet);
    }

    [Fact]
    public void AnOpenRegionPointsAtTheLoopThatIsOpenAndItsRealSize()
    {
        BoundaryLoop open = WallsWithARealOpening();
        BoundaryLoop closed = Rect(0, 0, 10, 8);

        CandidateRegion region = Region(closed, open);

        Assert.Equal(new[] { open }, region.OpenLoops);
        Assert.Equal(DoorwayWidth, region.LargestGapInternalFeet, precision: 9);
    }

    /// <summary>
    /// One closed loop alongside an open one does not make the region enclosed.
    /// Reporting the closed loop's area as the region's would describe a space
    /// bounded on all sides that is not.
    /// </summary>
    [Fact]
    public void OneOpenLoopIsEnoughToLeaveTheWholeRegionUnenclosed()
    {
        CandidateRegion region = Region(Rect(0, 0, 10, 8), WallsWithARealOpening());

        Assert.False(region.IsEnclosed);
        Assert.False(region.NetArea.IsMeasured);
    }

    [Fact]
    public void TheToleranceTheRegionWasBuiltWithTravelsWithItsArea()
    {
        CandidateRegion region = Region(Rect(0, 0, 10, 8));

        Assert.Equal(Tolerance, region.ClosureToleranceInternalFeet);
        Assert.Equal(Tolerance, region.NetArea.ToleranceInternalFeet);
    }

    [Fact]
    public void UnattributedBoundaryIsSurfacedAcrossEveryLoop()
    {
        var loop = new BoundaryLoop(new[]
        {
            new BoundarySegment(
                BoundaryCurve.Straight(new Point2D(0, 0), new Point2D(4, 0)),
                BoundaryReference.Host(new RevitElementId(10))),
            new BoundarySegment(
                BoundaryCurve.Straight(new Point2D(4, 0), new Point2D(4, 4)),
                BoundaryReference.Unattributed()),
            new BoundarySegment(
                BoundaryCurve.Straight(new Point2D(4, 4), new Point2D(0, 4)),
                BoundaryReference.Host(new RevitElementId(11))),
            new BoundarySegment(
                BoundaryCurve.Straight(new Point2D(0, 4), new Point2D(0, 0)),
                BoundaryReference.Unattributed()),
        });

        CandidateRegion region = Region(loop);

        Assert.Equal(2, region.UnattributedSegments.Count);
    }

    /// <summary>
    /// A wall usually contributes several segments to a boundary, and the rules
    /// that follow ask which elements enclose a region, not how many times each
    /// was met.
    /// </summary>
    [Fact]
    public void BoundingElementsAreListedOnceEachInTheOrderTheyFirstAppear()
    {
        BoundaryReference wallA = BoundaryReference.Host(new RevitElementId(100));
        BoundaryReference wallB = BoundaryReference.Host(new RevitElementId(200));

        var loop = new BoundaryLoop(new[]
        {
            new BoundarySegment(BoundaryCurve.Straight(new Point2D(0, 0), new Point2D(4, 0)), wallA),
            new BoundarySegment(BoundaryCurve.Straight(new Point2D(4, 0), new Point2D(4, 4)), wallB),
            new BoundarySegment(BoundaryCurve.Straight(new Point2D(4, 4), new Point2D(0, 4)), wallA),
            new BoundarySegment(BoundaryCurve.Straight(new Point2D(0, 4), new Point2D(0, 0)), BoundaryReference.Unattributed()),
        });

        CandidateRegion region = Region(loop);

        Assert.Equal(new[] { wallA, wallB }, region.BoundingReferences);
    }

    /// <summary>
    /// Guards the one assumption the loop classification rests on. Voids sit
    /// inside what encloses them, so they cannot together be larger than it.
    /// If that ever fails, the areas derived from the classification would be
    /// wrong, and saying so is better than reporting them.
    /// </summary>
    [Fact]
    public void LoopsThatContradictTheSizeClassificationAreReportedNotAveragedOver()
    {
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => Region(Rect(0, 0, 10, 1), Rect(0, 0, 6, 1), Rect(0, 0, 6, 1)));

        Assert.Contains("classified by size", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ARegionNeedsAtLeastOneLoop()
    {
        Assert.Throws<ArgumentException>(() => new CandidateRegion(new RegionId(0), Array.Empty<BoundaryLoop>(), Tolerance));
    }

    [Fact]
    public void ANegativeToleranceIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new CandidateRegion(new RegionId(0), new[] { Rect(0, 0, 4, 4) }, -1));
    }

    /// <summary>
    /// The shape from the brief: four walls with a doorway-sized opening.
    ///
    ///     +----   ----+
    ///     |           |
    ///     +-----------+
    /// </summary>
    private static BoundaryLoop WallsWithARealOpening()
    {
        BoundarySegment Wall(Point2D from, Point2D to) => new(
            BoundaryCurve.Straight(from, to),
            BoundaryReference.Host(new RevitElementId(Interlocked.Increment(ref _nextId))));

        return new BoundaryLoop(new[]
        {
            Wall(new Point2D(0, 0), new Point2D(10, 0)),
            Wall(new Point2D(10, 0), new Point2D(10, 8)),
            Wall(new Point2D(10, 8), new Point2D(6.5, 8)),
            // 3 ft of nothing here
            Wall(new Point2D(3.5, 8), new Point2D(0, 8)),
            Wall(new Point2D(0, 8), new Point2D(0, 0)),
        });
    }
}
