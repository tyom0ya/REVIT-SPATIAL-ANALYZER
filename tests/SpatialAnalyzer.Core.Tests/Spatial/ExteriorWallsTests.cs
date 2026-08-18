using SpatialAnalyzer.Core.Domain;
using SpatialAnalyzer.Core.Geometry;
using SpatialAnalyzer.Core.Spatial;
using Xunit;

namespace SpatialAnalyzer.Core.Tests.Spatial;

public class ExteriorWallsTests
{
    private const double Reach = 4.0;
    private const double Near = 1.0;
    private const double StepAside = 1.5;
    private const double Every = 1.0;

    private static long _nextId = 80000;

    private static PlanWall Wall(double x0, double y0, double x1, double y1) =>
        new(
            new RevitElementId(_nextId++),
            BoundaryCurve.Straight(new Point2D(x0, y0), new Point2D(x1, y1)),
            false);

    private static IReadOnlyList<WallExposureFinding> Classify(IReadOnlyList<PlanWall> walls)
    {
        var cloud = walls
            .SelectMany(w => ExteriorWalls.SampleForCloud(w.CentreLine, Every))
            .ToList();

        return ExteriorWalls.Classify(walls, BuildingFootprint.Around(cloud, Reach), Near, StepAside);
    }

    private static List<PlanWall> Box(double width, double height) => new()
    {
        Wall(0, 0, width, 0),
        Wall(width, 0, width, height),
        Wall(width, height, 0, height),
        Wall(0, height, 0, 0),
    };

    [Fact]
    public void EveryWallOfASingleBoxFacesOutside()
    {
        Assert.All(Classify(Box(40, 30)), f => Assert.Equal(WallExposure.Exterior, f.Exposure));
    }

    /// <summary>
    /// The case the previous approach got wrong on a real model, and the reason
    /// this one exists. A partition down the middle is nowhere near the outline
    /// and must not be called a facade.
    /// </summary>
    [Fact]
    public void APartitionDownTheMiddleIsInterior()
    {
        var walls = Box(40, 30);
        PlanWall partition = Wall(20, 4, 20, 26);
        walls.Add(partition);

        WallExposureFinding found = Assert.Single(Classify(walls).Where(f => f.Wall == partition.Id));

        Assert.Equal(WallExposure.Interior, found.Exposure);
    }

    /// <summary>
    /// A party wall runs the full depth of the block and touches the facade at
    /// both ends. Touching is not running along, and the score is built so that
    /// a corner meeting the outline cannot on its own make a wall exterior.
    /// </summary>
    [Fact]
    public void APartyWallTouchingTheFacadeAtBothEndsIsStillInterior()
    {
        var walls = Box(40, 30);
        PlanWall party = Wall(20, 0, 20, 30);
        walls.Add(party);

        WallExposureFinding found = Assert.Single(Classify(walls).Where(f => f.Wall == party.Id));

        Assert.Equal(WallExposure.Interior, found.Exposure);
        Assert.True(found.Score < ExteriorWalls.Needed);
    }

    /// <summary>
    /// A facade whose walls never close a room behind them is still a facade.
    /// This is what failed before: with no enclosed space either side, the
    /// earlier test called these walls open ground.
    /// </summary>
    [Fact]
    public void AFacadeWithNothingClosedBehindItIsStillExterior()
    {
        var walls = Box(40, 30);

        // Stubs poking inward, closing nothing at all.
        walls.Add(Wall(10, 0, 10, 6));
        walls.Add(Wall(30, 30, 30, 24));

        var found = Classify(walls);
        var boxIds = walls.Take(4).Select(w => w.Id).ToHashSet();

        Assert.All(
            found.Where(f => boxIds.Contains(f.Wall)),
            f => Assert.Equal(WallExposure.Exterior, f.Exposure));
    }

    /// <summary>
    /// The outline must follow a recess rather than bridging it, or the walls
    /// down the sides of an entrance bay are reported as interior.
    /// </summary>
    [Fact]
    public void TheOutlineFollowsARecessRatherThanBridgingIt()
    {
        var walls = new List<PlanWall>
        {
            Wall(0, 0, 40, 0),
            Wall(40, 0, 40, 30),
            Wall(40, 30, 25, 30),
            Wall(25, 30, 25, 20),
            Wall(25, 20, 15, 20),
            Wall(15, 20, 15, 30),
            Wall(15, 30, 0, 30),
            Wall(0, 30, 0, 0),
        };

        var found = Classify(walls);

        // The three walls of the notch face outside; a convex hull would have
        // spanned straight across the top and buried them.
        Assert.All(
            found.Where(f => walls.Skip(3).Take(3).Select(w => w.Id).Contains(f.Wall)),
            f => Assert.Equal(WallExposure.Exterior, f.Exposure));
    }

    [Fact]
    public void AStepAsideOfNothingIsRefused()
    {
        var walls = Box(10, 10);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => ExteriorWalls.Classify(walls, BuildingFootprint.Around(new List<Point2D>(), Reach), Near, 0));
    }

    [Fact]
    public void NothingToGoOnIsSaidRatherThanGuessed()
    {
        var walls = Box(10, 10);

        var found = ExteriorWalls.Classify(
            walls,
            BuildingFootprint.Around(new List<Point2D>(), Reach),
            Near,
            StepAside);

        Assert.All(found, f => Assert.Equal(WallExposure.Unknown, f.Exposure));
    }

    [Fact]
    public void SamplingWalksALongWallRatherThanOnlyItsEnds()
    {
        var points = ExteriorWalls.SampleForCloud(
            BoundaryCurve.Straight(new Point2D(0, 0), new Point2D(10, 0)),
            1.0).ToList();

        Assert.True(points.Count >= 10, $"A ten foot wall sampled every foot gave {points.Count} points.");
    }
}
