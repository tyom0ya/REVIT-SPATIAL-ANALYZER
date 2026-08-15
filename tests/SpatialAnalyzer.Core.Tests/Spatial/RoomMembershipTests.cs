using SpatialAnalyzer.Core.Domain;
using SpatialAnalyzer.Core.Geometry;
using SpatialAnalyzer.Core.Spatial;

namespace SpatialAnalyzer.Core.Tests.Spatial;

public class RoomMembershipTests
{
    private const double Tolerance = 1.0 / 384.0;

    private static long _nextId = 30000;

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

    private static ElementDescriptor Element(long id, string category = "Furniture") =>
        ElementDescriptor.Create(new RevitElementId(id), category, "Family", "Type");

    private static BoundaryFeature DoorFeature(long id) =>
        new(Element(id, "Doors"), BoundaryFeatureKind.Door);

    private static GranularRoom Room(int ordinal, BoundaryLoop outer, params BoundaryLoop[] voids)
    {
        var loops = new List<BoundaryLoop> { outer };
        loops.AddRange(voids);

        var region = new CandidateRegion(new RegionId(ordinal), loops, Tolerance);

        return new GranularRoom(region, new[]
        {
            new RoomEntrance(Element(1, "Doors"), BoundaryFeatureKind.Door, EntranceAuthority.Rule),
        });
    }

    private static DoorAdjacencyIndex NoDoors() =>
        DoorAdjacencyIndex.Build(
            new Dictionary<RegionId, IReadOnlyList<BoundaryFeature>>(),
            EntranceRule.Default);

    [Fact]
    public void SomethingStandingInARoomIsInThatRoom()
    {
        var resolver = new RoomMembershipResolver(
            new[] { Room(0, Rect(0, 0, 10, 8)), Room(1, Rect(20, 0, 10, 8)) },
            NoDoors());

        RoomMembership membership = resolver.Resolve(Element(500), new Point2D(5, 4), Tolerance);

        Assert.Equal(MembershipKind.InOneRoom, membership.Kind);
        Assert.Equal(new[] { new RegionId(0) }, membership.Rooms);
    }

    [Fact]
    public void SomethingStandingNowhereNearARoomBelongsToNone()
    {
        var resolver = new RoomMembershipResolver(new[] { Room(0, Rect(0, 0, 10, 8)) }, NoDoors());

        RoomMembership membership = resolver.Resolve(Element(500), new Point2D(100, 100), Tolerance);

        Assert.Equal(MembershipKind.NotInAnyRoom, membership.Kind);
        Assert.Empty(membership.Rooms);
    }

    /// <summary>
    /// The brief's requirement: selecting a door reports both rooms it joins,
    /// not one of them.
    /// </summary>
    [Fact]
    public void ADoorBelongsToBothRoomsItJoins()
    {
        BoundaryFeature door = DoorFeature(786853);

        var adjacency = DoorAdjacencyIndex.Build(
            new Dictionary<RegionId, IReadOnlyList<BoundaryFeature>>
            {
                [new RegionId(0)] = new[] { door },
                [new RegionId(1)] = new[] { door },
            },
            EntranceRule.Default);

        var resolver = new RoomMembershipResolver(
            new[] { Room(0, Rect(0, 0, 10, 8)), Room(1, Rect(10, 0, 10, 8)) },
            adjacency);

        // Its own position sits in the wall between them, where a containment
        // test has nothing useful to say. The answer comes from what it joins.
        RoomMembership membership = resolver.Resolve(door.Element, new Point2D(10, 4), Tolerance);

        Assert.Equal(MembershipKind.ConnectsRooms, membership.Kind);
        Assert.Equal(new[] { new RegionId(0), new RegionId(1) }, membership.Rooms);
    }

    /// <summary>
    /// A door whose far side is outdoors, another storey, or a region that did
    /// not qualify belongs to the one room beside it. Calling that "connects
    /// rooms" would name a relationship with something never found.
    /// </summary>
    [Fact]
    public void ADoorWithOnlyOneRoomBesideItBelongsToThatRoom()
    {
        BoundaryFeature door = DoorFeature(1919944);

        var adjacency = DoorAdjacencyIndex.Build(
            new Dictionary<RegionId, IReadOnlyList<BoundaryFeature>> { [new RegionId(0)] = new[] { door } },
            EntranceRule.Default);

        var resolver = new RoomMembershipResolver(new[] { Room(0, Rect(0, 0, 10, 8)) }, adjacency);

        RoomMembership membership = resolver.Resolve(door.Element, new Point2D(10, 4), Tolerance);

        Assert.Equal(MembershipKind.InOneRoom, membership.Kind);
        Assert.Equal(new[] { new RegionId(0) }, membership.Rooms);
    }

    /// <summary>
    /// A door into a lift shaft is beside a region the analysis declined to call
    /// a room. That region is not handed back as an answer.
    /// </summary>
    [Fact]
    public void ADoorIntoSomethingThatIsNotARoomReportsNoRoom()
    {
        BoundaryFeature door = DoorFeature(724791);

        var adjacency = DoorAdjacencyIndex.Build(
            new Dictionary<RegionId, IReadOnlyList<BoundaryFeature>> { [new RegionId(14)] = new[] { door } },
            EntranceRule.Default);

        // Region 14 exists in the adjacency but never became a room.
        var resolver = new RoomMembershipResolver(new[] { Room(0, Rect(0, 0, 10, 8)) }, adjacency);

        RoomMembership membership = resolver.Resolve(door.Element, new Point2D(5, 4), Tolerance);

        Assert.Equal(MembershipKind.NotInAnyRoom, membership.Kind);
        Assert.Empty(membership.Rooms);
    }

    /// <summary>
    /// Something pushed against a wall sits where the answer is genuinely
    /// ambiguous. Which room it belongs to is a decision about the building, so
    /// it is reported as being on the boundary rather than settled by rounding.
    /// </summary>
    [Fact]
    public void SomethingAgainstAWallIsReportedAsBeingOnIt()
    {
        var resolver = new RoomMembershipResolver(new[] { Room(0, Rect(0, 0, 10, 8)) }, NoDoors());

        RoomMembership membership = resolver.Resolve(Element(500), new Point2D(0, 4), Tolerance);

        Assert.Equal(MembershipKind.OnABoundary, membership.Kind);
        Assert.Equal(new[] { new RegionId(0) }, membership.Rooms);
    }

    [Fact]
    public void SomethingInAnInteriorVoidIsNotInTheRoomAroundIt()
    {
        var resolver = new RoomMembershipResolver(
            new[] { Room(0, Rect(0, 0, 10, 8), Rect(3, 3, 2, 2)) },
            NoDoors());

        RoomMembership membership = resolver.Resolve(Element(500), new Point2D(4, 4), Tolerance);

        Assert.Equal(MembershipKind.NotInAnyRoom, membership.Kind);
    }

    /// <summary>
    /// Rooms do not overlap, so being inside two of them is a contradiction
    /// rather than a finding. Naming one would hide it.
    /// </summary>
    [Fact]
    public void BeingInsideTwoRoomsAtOnceIsReportedNotResolved()
    {
        var resolver = new RoomMembershipResolver(
            new[] { Room(0, Rect(0, 0, 10, 8)), Room(1, Rect(0, 0, 10, 8)) },
            NoDoors());

        RoomMembership membership = resolver.Resolve(Element(500), new Point2D(5, 4), Tolerance);

        Assert.Equal(MembershipKind.InMoreThanOneRoom, membership.Kind);
        Assert.Equal(2, membership.Rooms.Count);
    }

    [Fact]
    public void TheAnswerNamesTheElementItIsAbout()
    {
        var resolver = new RoomMembershipResolver(new[] { Room(0, Rect(0, 0, 10, 8)) }, NoDoors());
        ElementDescriptor chair = Element(777);

        RoomMembership membership = resolver.Resolve(chair, new Point2D(5, 4), Tolerance);

        Assert.Same(chair, membership.Element);
    }

    [Fact]
    public void RoomsAreListedInAStableOrder()
    {
        var resolver = new RoomMembershipResolver(
            new[] { Room(7, Rect(0, 0, 10, 8)), Room(2, Rect(0, 0, 10, 8)) },
            NoDoors());

        RoomMembership membership = resolver.Resolve(Element(500), new Point2D(5, 4), Tolerance);

        Assert.Equal(new[] { new RegionId(2), new RegionId(7) }, membership.Rooms);
    }
}
