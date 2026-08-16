using SpatialAnalyzer.Core.Domain;
using SpatialAnalyzer.Core.Geometry;
using SpatialAnalyzer.Core.Spatial;
using Xunit;

namespace SpatialAnalyzer.Core.Tests.Spatial;

public class PartitionLoopsTests
{
    private const double Tolerance = 0.0025602645572916664;

    private static long _nextId = 90000;

    private static PartitionWall Wall(double x0, double y0, double x1, double y1) =>
        new(
            new RevitElementId(_nextId++),
            BoundaryCurve.Straight(new Point2D(x0, y0), new Point2D(x1, y1)));

    /// <summary>Four walls meeting at four corners, enclosing ten by six.</summary>
    private static List<PartitionWall> Rectangle(double width = 10, double height = 6) => new()
    {
        Wall(0, 0, width, 0),
        Wall(width, 0, width, height),
        Wall(width, height, 0, height),
        Wall(0, height, 0, 0),
    };

    [Fact]
    public void AClosedRingBecomesALoopWithItsArea()
    {
        PartitionArrangement found = PartitionLoops.Find(Rectangle(), Tolerance);

        PartitionLoop loop = Assert.Single(found.ClosedLoops);
        Assert.Equal(4, loop.Walls.Count);
        Assert.Equal(60.0, loop.Area.InternalSquareFeet, 9);
        Assert.Empty(found.OpenChains);
        Assert.Empty(found.Tangled);
    }

    /// <summary>
    /// The rule this whole type exists to respect. A ring with a doorway in it
    /// is not a room, however small the doorway, and nothing here may decide
    /// otherwise.
    /// </summary>
    [Fact]
    public void ARingWithADoorwayInItIsAChainAndKeepsItsGap()
    {
        var walls = Rectangle();
        walls.RemoveAt(3);
        walls.Add(Wall(0, 6, 0, 3));

        PartitionArrangement found = PartitionLoops.Find(walls, Tolerance);

        Assert.Empty(found.ClosedLoops);
        PartitionChain chain = Assert.Single(found.OpenChains);
        Assert.Equal(3.0, chain.GapBetweenNearestFreeEndsInternalFeet, 9);
    }

    /// <summary>
    /// A gap of a tenth of an inch is far smaller than any doorway and is
    /// exactly the size a person would be tempted to close. It stays open, and
    /// it stays reported.
    /// </summary>
    [Fact]
    public void ATinyGapIsStillAGap()
    {
        var walls = Rectangle();
        walls.RemoveAt(3);
        walls.Add(Wall(0, 6, 0, 0.008333));

        PartitionArrangement found = PartitionLoops.Find(walls, Tolerance);

        Assert.Empty(found.ClosedLoops);
        Assert.Equal(0.008333, Assert.Single(found.OpenChains).GapBetweenNearestFreeEndsInternalFeet, 9);
    }

    /// <summary>
    /// Two corners stored a ten-thousandth of a foot apart are one corner
    /// drawn twice, not a hole. Reconciling them is what tolerance is for.
    /// </summary>
    [Fact]
    public void CornersStoredFractionallyApartAreTheSameCorner()
    {
        var walls = Rectangle();
        walls.RemoveAt(3);
        walls.Add(Wall(0, 6, 0.0001, 0.0001));

        PartitionArrangement found = PartitionLoops.Find(walls, Tolerance);

        Assert.Single(found.ClosedLoops);
        Assert.Empty(found.OpenChains);
    }

    /// <summary>
    /// Tolerance joins ends pairwise, so a run of them could in principle walk
    /// a corner further than the tolerance allows. The ring is therefore
    /// measured against the real coordinates afterwards, and one that does not
    /// truly meet is reported as open however it was assembled.
    /// </summary>
    [Fact]
    public void ChainedTolerancesCannotAddUpToAClosedRoom()
    {
        double step = Tolerance * 0.9;

        var walls = new List<PartitionWall>
        {
            Wall(0, 0, 10, 0),
            Wall(10, 0, 10, 6),
            Wall(10, 6, 0, 6),
            Wall(0, 6, 0, step * 4),
            Wall(0, step * 3, 0, step * 2),
            Wall(0, step, 0, 0),
        };

        PartitionArrangement found = PartitionLoops.Find(walls, Tolerance);

        Assert.All(
            found.ClosedLoops,
            loop => Assert.True(
                loop.Area.LargestGapInternalFeet <= Tolerance,
                "A loop reported as closed must actually close within the stated tolerance."));
    }

    [Fact]
    public void AWallHangingByOneEndEnclosesNothing()
    {
        var walls = Rectangle();
        walls.Add(Wall(5, 3, 5, 5));

        PartitionArrangement found = PartitionLoops.Find(walls, Tolerance);

        Assert.Single(found.ClosedLoops);
        Assert.Equal(4, found.ClosedLoops[0].Walls.Count);
        Assert.Single(found.OpenChains);
    }

    [Fact]
    public void TwoSeparateRoomsAreFoundSeparately()
    {
        var walls = Rectangle();
        walls.AddRange(new[]
        {
            Wall(20, 0, 24, 0),
            Wall(24, 0, 24, 4),
            Wall(24, 4, 20, 4),
            Wall(20, 4, 20, 0),
        });

        PartitionArrangement found = PartitionLoops.Find(walls, Tolerance);

        Assert.Equal(2, found.ClosedLoops.Count);
        Assert.Equal(60.0, found.ClosedLoops[0].Area.InternalSquareFeet, 9);
        Assert.Equal(16.0, found.ClosedLoops[1].Area.InternalSquareFeet, 9);
    }

    /// <summary>
    /// A wall dividing a ring in two makes three rings of the same lines, and
    /// which of them is a room cannot be read off the lines. Guessing would
    /// invent a room as readily as find one, so the walls are handed back
    /// unresolved.
    /// </summary>
    [Fact]
    public void AJunctionWhereThreeWallsMeetIsReportedRatherThanGuessedAt()
    {
        var walls = Rectangle();
        walls.Add(Wall(0, 0, 10, 6));

        PartitionArrangement found = PartitionLoops.Find(walls, Tolerance);

        Assert.Empty(found.ClosedLoops);
        Assert.Equal(5, found.Tangled.Count);
    }

    [Fact]
    public void NoWallsIsNotAnError()
    {
        PartitionArrangement found = PartitionLoops.Find(Array.Empty<PartitionWall>(), Tolerance);

        Assert.Empty(found.ClosedLoops);
        Assert.Empty(found.OpenChains);
        Assert.Empty(found.Tangled);
    }

    [Fact]
    public void ANegativeToleranceIsRefused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PartitionLoops.Find(Rectangle(), -1));
    }

    [Fact]
    public void ANaNToleranceIsRefused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PartitionLoops.Find(Rectangle(), double.NaN));
    }
}
