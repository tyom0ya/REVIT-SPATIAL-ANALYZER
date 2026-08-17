using SpatialAnalyzer.Core.Domain;
using SpatialAnalyzer.Core.Geometry;
using SpatialAnalyzer.Core.Spatial;
using Xunit;

namespace SpatialAnalyzer.Core.Tests.Spatial;

public class PlanFacesTests
{
    private const double Tolerance = 0.0025602645572916664;

    private static long _nextId = 70000;

    private static PlanWall Wall(double x0, double y0, double x1, double y1, bool ignored = false) =>
        new(
            new RevitElementId(_nextId++),
            BoundaryCurve.Straight(new Point2D(x0, y0), new Point2D(x1, y1)),
            ignored);

    private static List<PlanWall> Room(double width = 10, double height = 6) => new()
    {
        Wall(0, 0, width, 0),
        Wall(width, 0, width, height),
        Wall(width, height, 0, height),
        Wall(0, height, 0, 0),
    };

    [Fact]
    public void FourWallsMakeOneRoom()
    {
        PlanSubdivision found = PlanFaces.Find(Room(), Tolerance);

        PlanFace face = Assert.Single(found.Faces);
        Assert.Equal(60.0, face.Area.InternalSquareFeet, 9);
        Assert.False(face.TouchesAWallIgnoredForRooms);
    }

    /// <summary>
    /// The whole point of the exercise. A partition the model ignores, running
    /// between two walls it does not, divides one space into two - and neither
    /// half can be found by looking at the partition alone.
    /// </summary>
    [Fact]
    public void APartitionTheModelIgnoresStillDividesTheRoom()
    {
        var walls = Room();
        walls.Add(Wall(4, 0, 4, 6, ignored: true));

        PlanSubdivision found = PlanFaces.Find(walls, Tolerance);

        Assert.Equal(2, found.Faces.Count);
        Assert.Equal(36.0, found.Faces[0].Area.InternalSquareFeet, 9);
        Assert.Equal(24.0, found.Faces[1].Area.InternalSquareFeet, 9);
        Assert.All(found.Faces, f => Assert.True(f.TouchesAWallIgnoredForRooms));
    }

    /// <summary>
    /// The same partition found by the older search, which is given only the
    /// walls the model ignores. It closes nothing, because the walls that close
    /// it were filtered out before it looked - which is exactly what the
    /// acceptance model showed and why this type exists.
    /// </summary>
    [Fact]
    public void TheSamePartitionAloneEnclosesNothing()
    {
        var alone = new List<PartitionWall>
        {
            new(new RevitElementId(1), BoundaryCurve.Straight(new Point2D(4, 0), new Point2D(4, 6))),
        };

        Assert.Empty(PartitionLoops.Find(alone, Tolerance).ClosedLoops);
    }

    /// <summary>
    /// A partition crossing the middle of a room is not attached at its ends in
    /// the input, but the split at the crossing attaches it. Without that the
    /// room stays whole.
    /// </summary>
    [Fact]
    public void AWallIsCutWhereAnotherCrossesIt()
    {
        var walls = Room();
        walls.Add(Wall(4, -2, 4, 8, ignored: true));

        PlanSubdivision found = PlanFaces.Find(walls, Tolerance);

        Assert.Equal(2, found.Faces.Count);
        Assert.Equal(60.0, found.Faces.Sum(f => f.Area.InternalSquareFeet), 9);
    }

    [Fact]
    public void TwoPartitionsMakeThreeRooms()
    {
        var walls = Room(12, 6);
        walls.Add(Wall(4, 0, 4, 6, ignored: true));
        walls.Add(Wall(8, 0, 8, 6, ignored: true));

        PlanSubdivision found = PlanFaces.Find(walls, Tolerance);

        Assert.Equal(3, found.Faces.Count);
        Assert.Equal(72.0, found.Faces.Sum(f => f.Area.InternalSquareFeet), 9);
    }

    /// <summary>
    /// A partition with a doorway in it divides nothing, and no tolerance may
    /// decide otherwise. The room stays whole and stays one face.
    /// </summary>
    [Fact]
    public void APartitionWithADoorwayDoesNotDivideAnything()
    {
        var walls = Room();
        walls.Add(Wall(4, 0, 4, 2, ignored: true));
        walls.Add(Wall(4, 5, 4, 6, ignored: true));

        PlanSubdivision found = PlanFaces.Find(walls, Tolerance);

        PlanFace face = Assert.Single(found.Faces);
        Assert.Equal(60.0, face.Area.InternalSquareFeet, 9);
    }

    /// <summary>
    /// Even a doorway of a tenth of an inch. This is the same rule the rest of
    /// the project keeps, stated again where a new algorithm could quietly
    /// break it.
    /// </summary>
    [Fact]
    public void ATinyDoorwayStillDoesNotDivideAnything()
    {
        var walls = Room();
        walls.Add(Wall(4, 0, 4, 2.995, ignored: true));
        walls.Add(Wall(4, 3.005, 4, 6, ignored: true));

        PlanSubdivision found = PlanFaces.Find(walls, Tolerance);

        Assert.Single(found.Faces);
    }

    /// <summary>
    /// The space outside the building is bounded like any other face, and is
    /// traced the other way round. It is not a room and must not be reported as
    /// the largest one.
    /// </summary>
    [Fact]
    public void TheSpaceOutsideIsNotARoom()
    {
        PlanSubdivision found = PlanFaces.Find(Room(), Tolerance);

        Assert.Single(found.Faces);
        Assert.All(found.Faces, f => Assert.True(f.Area.InternalSquareFeet > 0));
    }

    [Fact]
    public void AWallHangingByOneEndDividesNothing()
    {
        var walls = Room();
        walls.Add(Wall(4, 0, 4, 3, ignored: true));

        PlanSubdivision found = PlanFaces.Find(walls, Tolerance);

        PlanFace face = Assert.Single(found.Faces);
        Assert.Equal(60.0, face.Area.InternalSquareFeet, 9);
    }

    [Fact]
    public void ARoomBoundedOnlyByOrdinaryWallsIsNotFlagged()
    {
        var walls = Room(12, 6);
        walls.Add(Wall(4, 0, 4, 6));
        walls.Add(Wall(8, 0, 8, 6, ignored: true));

        PlanSubdivision found = PlanFaces.Find(walls, Tolerance);

        Assert.Equal(3, found.Faces.Count);
        Assert.Single(found.Faces.Where(f => !f.TouchesAWallIgnoredForRooms));
    }

    [Fact]
    public void TwoSeparateBuildingsAreFoundSeparately()
    {
        var walls = Room();
        walls.AddRange(new[]
        {
            Wall(20, 0, 24, 0),
            Wall(24, 0, 24, 4),
            Wall(24, 4, 20, 4),
            Wall(20, 4, 20, 0),
        });

        PlanSubdivision found = PlanFaces.Find(walls, Tolerance);

        Assert.Equal(2, found.Faces.Count);
        Assert.Equal(60.0, found.Faces[0].Area.InternalSquareFeet, 9);
        Assert.Equal(16.0, found.Faces[1].Area.InternalSquareFeet, 9);
    }

    /// <summary>
    /// A partition butting into the middle of another wall meets it at the
    /// partition's own endpoint, so the crossing parameter along the partition
    /// is nought or one exactly - and floating point puts such a value outside
    /// the unit range as readily as inside it. Rejecting those left the wall
    /// unsplit and whole apartments with no rooms found in them, which is what
    /// unit 304B of the acceptance model showed.
    /// </summary>
    [Fact]
    public void APartitionButtingIntoAWallStillDividesTheRoom()
    {
        var walls = Room();

        // Ends land exactly on the top and bottom walls rather than at their
        // corners, which is how a real partition meets a real wall.
        walls.Add(Wall(4, 6, 4, 0, ignored: true));

        PlanSubdivision found = PlanFaces.Find(walls, Tolerance);

        Assert.Equal(2, found.Faces.Count);
        Assert.Equal(60.0, found.Faces.Sum(f => f.Area.InternalSquareFeet), 9);
    }

    /// <summary>
    /// Two walls a hand's breadth apart and closed at both ends enclose a real
    /// face, and it is not a room. Reporting it would bury the rooms that
    /// matter among construction gaps, which is what unit 307A showed.
    /// </summary>
    [Fact]
    public void AGapBetweenTwoWallsIsNotARoom()
    {
        var walls = new List<PlanWall>
        {
            Wall(0, 0, 10, 0),
            Wall(10, 0, 10, 0.3, ignored: true),
            Wall(10, 0.3, 0, 0.3, ignored: true),
            Wall(0, 0.3, 0, 0),
        };

        PlanSubdivision found = PlanFaces.Find(walls, Tolerance);

        Assert.Empty(found.Faces);
        Assert.Equal(1, found.FacesTooNarrowToStandIn);
    }

    /// <summary>
    /// A cupboard is small but not thin, and must survive the filter that
    /// removes slivers. Area alone could not tell these apart.
    /// </summary>
    [Fact]
    public void ASmallCupboardIsStillARoom()
    {
        var walls = new List<PlanWall>
        {
            Wall(0, 0, 2, 0),
            Wall(2, 0, 2, 3, ignored: true),
            Wall(2, 3, 0, 3, ignored: true),
            Wall(0, 3, 0, 0),
        };

        PlanSubdivision found = PlanFaces.Find(walls, Tolerance);

        PlanFace face = Assert.Single(found.Faces);
        Assert.Equal(6.0, face.Area.InternalSquareFeet, 9);
        Assert.Equal(0, found.FacesTooNarrowToStandIn);
    }

    [Fact]
    public void APointIsFoundInsideASimpleRoom()
    {
        PlanFace face = Assert.Single(PlanFaces.Find(Room(), Tolerance).Faces);

        Assert.True(PlanFaces.TryFindPointInside(face, out Point2D inside));
        Assert.InRange(inside.X, 0, 10);
        Assert.InRange(inside.Y, 0, 6);
    }

    /// <summary>
    /// An L-shaped flat has its centroid out in the corridor. A room placed
    /// there would sit in the wrong space and look exactly like the analysis
    /// having failed, so the point has to be tested rather than computed and
    /// trusted.
    /// </summary>
    [Fact]
    public void APointIsFoundInsideAnLShapedRoom()
    {
        var walls = new List<PlanWall>
        {
            Wall(0, 0, 10, 0),
            Wall(10, 0, 10, 2),
            Wall(10, 2, 2, 2),
            Wall(2, 2, 2, 10),
            Wall(2, 10, 0, 10),
            Wall(0, 10, 0, 0),
        };

        PlanFace face = Assert.Single(PlanFaces.Find(walls, Tolerance).Faces);

        Assert.True(PlanFaces.TryFindPointInside(face, out Point2D inside));

        // The notch is everything above y=2 and right of x=2. A point there is
        // outside the flat however plausible its arithmetic looked.
        Assert.False(inside.X > 2 && inside.Y > 2, "The point must not land in the notch.");
    }

    [Fact]
    public void NoWallsIsNotAnError()
    {
        PlanSubdivision found = PlanFaces.Find(Array.Empty<PlanWall>(), Tolerance);

        Assert.Empty(found.Faces);
        Assert.Equal(0, found.SegmentsAfterSplitting);
    }

    [Fact]
    public void ANegativeToleranceIsRefused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PlanFaces.Find(Room(), -1));
    }
}
