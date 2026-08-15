using SpatialAnalyzer.Core.Domain;
using SpatialAnalyzer.Core.Spatial;

namespace SpatialAnalyzer.Core.Tests.Spatial;

public class DoorAdjacencyIndexTests
{
    private static BoundaryFeature Door(long id) =>
        new(ElementDescriptor.Create(new RevitElementId(id), "Doors", "Door-Passage-Single-Flush", "36\" x 84\""),
            BoundaryFeatureKind.Door);

    private static BoundaryFeature Window(long id) =>
        new(ElementDescriptor.Create(new RevitElementId(id), "Windows", "Window-Sliding-Double", "50\" x 60\""),
            BoundaryFeatureKind.Window);

    private static BoundaryFeature LiftDoor(long id) =>
        new(ElementDescriptor.Create(new RevitElementId(id), "Specialty Equipment", "Elevator Door-Center", "48\" x 84\""),
            BoundaryFeatureKind.SpecialtyEquipment);

    private static DoorAdjacencyIndex Build(
        params (int Region, BoundaryFeature[] Features)[] regions)
    {
        var map = regions.ToDictionary(
            r => new RegionId(r.Region),
            r => (IReadOnlyList<BoundaryFeature>)r.Features);

        return DoorAdjacencyIndex.Build(map, EntranceRule.Default);
    }

    /// <summary>
    /// The ordinary case, and the one the acceptance level is full of: door
    /// 786853 lies on the boundary of both region 2 and region 6, so it is what
    /// is between them.
    /// </summary>
    [Fact]
    public void ADoorOnTwoBoundariesConnectsThoseTwoRegions()
    {
        DoorAdjacencyIndex index = Build(
            (2, new[] { Door(786853) }),
            (6, new[] { Door(786853) }));

        DoorAdjacency adjacency = Assert.Single(index.Adjacencies);

        Assert.Equal(DoorConnection.BetweenTwoRegions, adjacency.Connection);
        Assert.Equal(new[] { new RegionId(2), new RegionId(6) }, adjacency.Regions);
    }

    [Fact]
    public void ADoorOnOneBoundaryReportsOnlyWhatWasFound()
    {
        DoorAdjacencyIndex index = Build((3, new[] { Door(1919946) }));

        DoorAdjacency adjacency = Assert.Single(index.Adjacencies);

        Assert.Equal(DoorConnection.OneSideResolved, adjacency.Connection);
        Assert.Equal(new[] { new RegionId(3) }, adjacency.Regions);
    }

    /// <summary>
    /// One wall may hold several doors into different spaces. On the acceptance
    /// level wall 1390768 carries two, opening from one large region into two
    /// small ones, and they must not be conflated because they share a host.
    /// </summary>
    [Fact]
    public void TwoDoorsInOneWallAreKeptApart()
    {
        DoorAdjacencyIndex index = Build(
            (0, new[] { Door(1390771), Door(1390772) }),
            (16, new[] { Door(1390772) }),
            (17, new[] { Door(1390771) }));

        Assert.Equal(2, index.Adjacencies.Count);
        Assert.Equal(new[] { new RegionId(0), new RegionId(17) }, index.For(new RevitElementId(1390771))!.Regions);
        Assert.Equal(new[] { new RegionId(0), new RegionId(16) }, index.For(new RevitElementId(1390772))!.Regions);
    }

    /// <summary>
    /// Only things that admit a person join two spaces. A window between two
    /// rooms does not connect them, and a lift door is not a way through to the
    /// shaft in the sense the analysis reports.
    /// </summary>
    [Fact]
    public void ThingsThatAreNotWaysInDoNotConnectAnything()
    {
        DoorAdjacencyIndex index = Build(
            (2, new[] { Window(824851), LiftDoor(724791) }),
            (6, new[] { Window(824851), LiftDoor(724791) }));

        Assert.Empty(index.Adjacencies);
    }

    /// <summary>
    /// A door has two sides. Found on three boundaries, the evidence contradicts
    /// itself, and naming two of the three would be a guess presented as a
    /// result. It is reported and left out of the adjacencies.
    /// </summary>
    [Fact]
    public void AConnectorOnThreeBoundariesIsReportedRatherThanResolved()
    {
        DoorAdjacencyIndex index = Build(
            (1, new[] { Door(500) }),
            (2, new[] { Door(500) }),
            (3, new[] { Door(500) }));

        Assert.Empty(index.Adjacencies);
        Assert.Null(index.For(new RevitElementId(500)));

        AmbiguousConnector ambiguous = Assert.Single(index.Ambiguous);
        Assert.Equal(3, ambiguous.Regions.Count);
    }

    [Fact]
    public void ADoorNeverFoundOnAnyBoundaryIsUnknownRatherThanUnconnected()
    {
        DoorAdjacencyIndex index = Build((1, new[] { Door(100) }));

        Assert.Null(index.For(new RevitElementId(999)));
    }

    /// <summary>
    /// Answers the question the brief asks of a selected room: what leads out of
    /// here, and to where.
    /// </summary>
    [Fact]
    public void ARegionCanBeAskedWhatLeadsOutOfIt()
    {
        DoorAdjacencyIndex index = Build(
            (6, new[] { Door(1), Door(2), Door(3) }),
            (7, new[] { Door(1) }),
            (8, new[] { Door(2) }));

        IReadOnlyList<DoorAdjacency> touching = index.Touching(new RegionId(6));

        Assert.Equal(3, touching.Count);
        Assert.Equal(2, touching.Count(a => a.ConnectsTwoRegions));
    }

    /// <summary>
    /// Reports and exports are compared between runs, so the order must not
    /// depend on which region happened to be examined first.
    /// </summary>
    [Fact]
    public void TheOrderDoesNotDependOnWhichRegionWasExaminedFirst()
    {
        DoorAdjacencyIndex oneWay = Build(
            (2, new[] { Door(300), Door(100) }),
            (6, new[] { Door(100) }));

        DoorAdjacencyIndex theOther = Build(
            (6, new[] { Door(100) }),
            (2, new[] { Door(100), Door(300) }));

        Assert.Equal(
            oneWay.Adjacencies.Select(a => a.Door.Id.Value),
            theOther.Adjacencies.Select(a => a.Door.Id.Value));

        Assert.Equal(new long[] { 100, 300 }, oneWay.Adjacencies.Select(a => a.Door.Id.Value));
    }

    [Fact]
    public void ACurtainWallDoorPanelConnectsJustAsADoorDoes()
    {
        var panel = new BoundaryFeature(
            ElementDescriptor.Create(new RevitElementId(1048452), "Doors", "Door-Curtain-Wall-Single-Storefront", "x"),
            BoundaryFeatureKind.CurtainWallDoorPanel);

        DoorAdjacencyIndex index = Build(
            (10, new[] { panel }),
            (11, new[] { panel }));

        Assert.Equal(DoorConnection.BetweenTwoRegions, Assert.Single(index.Adjacencies).Connection);
    }

    [Fact]
    public void AnEmptyModelYieldsNoAdjacencies()
    {
        DoorAdjacencyIndex index = DoorAdjacencyIndex.Build(
            new Dictionary<RegionId, IReadOnlyList<BoundaryFeature>>(),
            EntranceRule.Default);

        Assert.Empty(index.Adjacencies);
        Assert.Empty(index.Ambiguous);
    }
}
