using SpatialAnalyzer.Core.Domain;
using SpatialAnalyzer.Core.Geometry;

namespace SpatialAnalyzer.Core.Tests.Geometry;

public class PlanAreaTests
{
    private const double DraftingArtefact = 0.001;   // ~0.3 mm
    private const double DoorwayWidth = 3.0;         // ~915 mm
    private const double Exact = 1e-9;

    private static BoundarySegment Wall(Point2D from, Point2D to, long id) =>
        new(BoundaryCurve.Straight(from, to), BoundaryReference.Host(new RevitElementId(id)));

    /// <summary>A 10 x 8 rectangle, drawn counter-clockwise.</summary>
    private static BoundaryLoop Rectangle() => new(new[]
    {
        Wall(new Point2D(0, 0), new Point2D(10, 0), 1),
        Wall(new Point2D(10, 0), new Point2D(10, 8), 2),
        Wall(new Point2D(10, 8), new Point2D(0, 8), 3),
        Wall(new Point2D(0, 8), new Point2D(0, 0), 4),
    });

    /// <summary>The same rectangle, drawn the other way round.</summary>
    private static BoundaryLoop RectangleReversed() => new(new[]
    {
        Wall(new Point2D(0, 0), new Point2D(0, 8), 4),
        Wall(new Point2D(0, 8), new Point2D(10, 8), 3),
        Wall(new Point2D(10, 8), new Point2D(10, 0), 2),
        Wall(new Point2D(10, 0), new Point2D(0, 0), 1),
    });

    /// <summary>
    /// The shape from the brief: four walls with a doorway-sized opening.
    ///
    ///     +----   ----+
    ///     |           |
    ///     +-----------+
    /// </summary>
    private static BoundaryLoop WallsWithARealOpening() => new(new[]
    {
        Wall(new Point2D(0, 0), new Point2D(10, 0), 1),
        Wall(new Point2D(10, 0), new Point2D(10, 8), 2),
        Wall(new Point2D(10, 8), new Point2D(6.5, 8), 3),
        // 3 ft of nothing here
        Wall(new Point2D(3.5, 8), new Point2D(0, 8), 4),
        Wall(new Point2D(0, 8), new Point2D(0, 0), 5),
    });

    [Fact]
    public void AClosedRectangleEnclosesItsArea()
    {
        AreaMeasurement area = PlanArea.OfLoop(Rectangle(), Exact);

        Assert.True(area.IsMeasured);
        Assert.Equal(80.0, area.InternalSquareFeet, precision: 9);
    }

    [Fact]
    public void AreaDoesNotDependOnWhichWayTheBoundaryWasDrawn()
    {
        Assert.Equal(
            PlanArea.OfLoop(Rectangle(), Exact).InternalSquareFeet,
            PlanArea.OfLoop(RectangleReversed(), Exact).InternalSquareFeet,
            precision: 9);
    }

    /// <summary>
    /// Direction is reported even though it does not change the area, because
    /// distinguishing an outer boundary from an interior void depends on it and
    /// that distinction is currently taken on trust from Revit's convention.
    /// </summary>
    [Fact]
    public void TheDirectionTheBoundaryWasDrawnInIsReported()
    {
        Assert.Equal(LoopWinding.CounterClockwise, PlanArea.OfLoop(Rectangle(), Exact).Winding);
        Assert.Equal(LoopWinding.Clockwise, PlanArea.OfLoop(RectangleReversed(), Exact).Winding);
    }

    /// <summary>
    /// The measurement counterpart of the gap rule.
    ///
    /// Computing an area requires treating the boundary as a closed ring, which
    /// for this shape would mean drawing a wall across the doorway. The result
    /// would be an entirely plausible 80 sq ft room that is not in the building.
    /// </summary>
    [Fact]
    public void ADoorwaySizedOpeningYieldsNoAreaAtAnyDefensibleTolerance()
    {
        BoundaryLoop loop = WallsWithARealOpening();

        Assert.False(PlanArea.OfLoop(loop, DraftingArtefact).IsMeasured);
        Assert.False(PlanArea.OfLoop(loop, 0.1).IsMeasured);
        Assert.False(PlanArea.OfLoop(loop, 1.0).IsMeasured);
    }

    [Fact]
    public void AnOpeningIsReportedAtItsRealSizeAsTheReason()
    {
        AreaMeasurement area = PlanArea.OfLoop(WallsWithARealOpening(), DraftingArtefact);

        Assert.Equal(DoorwayWidth, area.LargestGapInternalFeet, precision: 9);
        Assert.Equal(DraftingArtefact, area.ToleranceInternalFeet);
    }

    /// <summary>
    /// An unknown area must never arrive downstream looking like a real one.
    /// A room of zero area and a room whose area cannot be determined are
    /// different facts about the building, and a plain double would flatten them
    /// into the same 0.0.
    /// </summary>
    [Fact]
    public void AnUnmeasuredAreaWillNotHandOutANumber()
    {
        AreaMeasurement area = PlanArea.OfLoop(WallsWithARealOpening(), DraftingArtefact);

        Assert.Throws<InvalidOperationException>(() => area.InternalSquareFeet);
        Assert.False(area.TryGetInternalSquareFeet(out double value));
        Assert.Equal(0, value);
    }

    [Fact]
    public void AZeroAreaIsAMeasurementAndNotAFailure()
    {
        // A boundary that runs out and straight back: closed, encloses nothing.
        var loop = new BoundaryLoop(new[]
        {
            Wall(new Point2D(0, 0), new Point2D(5, 0), 1),
            Wall(new Point2D(5, 0), new Point2D(0, 0), 2),
        });

        AreaMeasurement area = PlanArea.OfLoop(loop, Exact);

        Assert.True(area.IsMeasured);
        Assert.Equal(0, area.InternalSquareFeet, precision: 12);
        Assert.Equal(LoopWinding.Degenerate, area.Winding);
    }

    /// <summary>
    /// The counterpart case, and the one the model actually contains: endpoints
    /// a third of a millimetre apart describe one location recorded twice. The
    /// closing edge that implies is a third of a millimetre long, which is why
    /// the resulting area is trustworthy.
    /// </summary>
    [Fact]
    public void ADraftingArtefactDoesNotPreventMeasurement()
    {
        var loop = new BoundaryLoop(new[]
        {
            Wall(new Point2D(0, 0), new Point2D(10, 0), 1),
            Wall(new Point2D(10, 0), new Point2D(10, 8), 2),
            Wall(new Point2D(10, 8), new Point2D(0, 8), 3),
            Wall(new Point2D(0, 8), new Point2D(0, DraftingArtefact), 4),
        });

        AreaMeasurement area = PlanArea.OfLoop(loop, DraftingArtefact);

        Assert.True(area.IsMeasured);
        Assert.Equal(80.0, area.InternalSquareFeet, precision: 2);
        Assert.False(PlanArea.OfLoop(loop, 0).IsMeasured);
    }

    /// <summary>
    /// Consistent with the rest of the project: a tolerance wide enough to
    /// swallow a doorway is not a tolerance, and this type does not pretend
    /// otherwise. It answers the question it was asked, and the stated tolerance
    /// stays attached to the answer so the choice remains visible.
    /// </summary>
    [Fact]
    public void AnAbsurdToleranceIsStillTheCallersStatedChoice()
    {
        AreaMeasurement area = PlanArea.OfLoop(WallsWithARealOpening(), DoorwayWidth);

        Assert.True(area.IsMeasured);
        Assert.Equal(DoorwayWidth, area.ToleranceInternalFeet);
    }

    /// <summary>
    /// A circle of radius 1, described as one curved segment tessellated into
    /// the given number of chords.
    /// </summary>
    private static BoundaryLoop TessellatedCircle(int chords)
    {
        var points = new List<Point2D>(chords + 1);
        for (int i = 0; i <= chords; i++)
        {
            double angle = 2 * Math.PI * i / chords;
            points.Add(new Point2D(Math.Cos(angle), Math.Sin(angle)));
        }

        var curve = new BoundaryCurve(BoundaryCurveKind.Arc, points[0], points[^1], 2 * Math.PI, points);
        return new BoundaryLoop(new[] { new BoundarySegment(curve, BoundaryReference.Host(new RevitElementId(1))) });
    }

    /// <summary>
    /// Records how the approximation behaves, rather than only that it is close.
    ///
    /// A polygon through points on a circle lies inside it, so a curved boundary
    /// is understated, and refining the tessellation closes the difference. That
    /// is a known, bounded, one-directional fidelity trade-off - unlike closing
    /// a gap, which changes what the building is.
    /// </summary>
    [Fact]
    public void ACurvedBoundaryIsMeasuredFromItsTessellationAndUnderstatedSlightly()
    {
        double coarse = PlanArea.OfLoop(TessellatedCircle(8), Exact).InternalSquareFeet;
        double fine = PlanArea.OfLoop(TessellatedCircle(720), Exact).InternalSquareFeet;

        Assert.True(coarse < fine, $"a coarser tessellation should enclose less, but {coarse} >= {fine}");
        Assert.True(fine < Math.PI, $"an inscribed polygon cannot exceed the circle, but {fine} >= {Math.PI}");
        Assert.Equal(Math.PI, fine, precision: 4);
    }

    [Fact]
    public void ANegativeToleranceIsRejectedRatherThanTreatedAsZero()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PlanArea.OfLoop(Rectangle(), -1));
    }

    [Fact]
    public void ANegativeAreaCannotBeConstructed()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => AreaMeasurement.Measured(-1, LoopWinding.Clockwise, 0.001, 0));
    }

    /// <summary>
    /// The default value of the struct must not read as a real measurement of
    /// nothing, because uninitialised state reaching an export is exactly the
    /// kind of silent wrong answer this project guards against.
    /// </summary>
    [Fact]
    public void ADefaultMeasurementIsNotMistakenForZeroArea()
    {
        AreaMeasurement uninitialised = default;

        Assert.False(uninitialised.IsMeasured);
        Assert.Throws<InvalidOperationException>(() => uninitialised.InternalSquareFeet);
    }
}
