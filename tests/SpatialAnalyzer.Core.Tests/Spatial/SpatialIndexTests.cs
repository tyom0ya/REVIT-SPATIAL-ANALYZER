using SpatialAnalyzer.Core.Domain;
using SpatialAnalyzer.Core.Geometry;
using SpatialAnalyzer.Core.Spatial;

namespace SpatialAnalyzer.Core.Tests.Spatial;

public class SpatialIndexTests
{
    private const double Tolerance = 1.0 / 384.0;

    private static long _nextId = 40000;

    private static readonly AnalysisContextInfo L2 = new(
        new RevitElementId(1350631), "L2", "FloorPlan",
        new RevitElementId(593177), "L2", 8.0833,
        new RevitElementId(118390), "New Construction");

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

    private static GranularRoom Room(int ordinal, params BoundaryLoop[] loops) =>
        new(
            new CandidateRegion(new RegionId(ordinal), loops, Tolerance),
            new[] { new RoomEntrance(Element(1, "Doors"), BoundaryFeatureKind.Door, EntranceAuthority.Rule) });

    private static DoorAdjacencyIndex NoDoors() =>
        DoorAdjacencyIndex.Build(
            new Dictionary<RegionId, IReadOnlyList<BoundaryFeature>>(),
            EntranceRule.Default);

    private static SpatialIndex Index(params GranularRoom[] rooms) =>
        SpatialIndex.Build(L2, rooms, NoDoors(), Tolerance);

    [Fact]
    public void APointFindsTheRoomItIsIn()
    {
        SpatialIndex index = Index(Room(0, Rect(0, 0, 10, 8)), Room(1, Rect(20, 0, 10, 8)));

        RoomMembership membership = index.Resolve(Element(500), new Point2D(25, 4));

        Assert.Equal(MembershipKind.InOneRoom, membership.Kind);
        Assert.Equal(new[] { new RegionId(1) }, membership.Rooms);
    }

    /// <summary>
    /// Narrowing the question by rectangle must not change the answer. An
    /// L-shaped room's notch is inside its rectangle and outside the room, which
    /// is exactly where a prefilter that was trusted to decide would be wrong.
    /// </summary>
    [Fact]
    public void TheRectangleNarrowsTheQuestionWithoutAnsweringIt()
    {
        var lShape = new List<Point2D>
        {
            new(0, 0), new(10, 0), new(10, 4), new(4, 4), new(4, 10), new(0, 10),
        };

        var segments = new List<BoundarySegment>();
        for (int i = 0; i < lShape.Count; i++)
        {
            segments.Add(new BoundarySegment(
                BoundaryCurve.Straight(lShape[i], lShape[(i + 1) % lShape.Count]),
                BoundaryReference.Host(new RevitElementId(Interlocked.Increment(ref _nextId)))));
        }

        SpatialIndex index = Index(Room(0, new BoundaryLoop(segments)));

        // Inside the rectangle around the room, outside the room itself.
        Assert.Equal(MembershipKind.NotInAnyRoom, index.Resolve(Element(500), new Point2D(8, 8)).Kind);
        Assert.Equal(MembershipKind.InOneRoom, index.Resolve(Element(500), new Point2D(2, 2)).Kind);
    }

    [Fact]
    public void APointNowhereNearAnyRoomIsInNone()
    {
        SpatialIndex index = Index(Room(0, Rect(0, 0, 10, 8)));

        Assert.Equal(MembershipKind.NotInAnyRoom, index.Resolve(Element(500), new Point2D(1000, 1000)).Kind);
    }

    /// <summary>
    /// A point a hair outside a room is on its boundary, and the rectangle is
    /// grown by the tolerance so the room is not discarded before anything can
    /// say so.
    /// </summary>
    [Fact]
    public void APointJustOutsideARoomIsStillConsideredAgainstIt()
    {
        SpatialIndex index = Index(Room(0, Rect(0, 0, 10, 8)));

        RoomMembership membership = index.Resolve(Element(500), new Point2D(-0.001, 4));

        Assert.Equal(MembershipKind.OnABoundary, membership.Kind);
    }

    /// <summary>
    /// A door stands inside a wall, where its position places it in no room at
    /// all. It has to be answered from what it connects even though the
    /// rectangles would have discarded every room.
    /// </summary>
    [Fact]
    public void ADoorIsAnsweredFromWhatItConnectsRatherThanWhereItStands()
    {
        BoundaryFeature door = new(Element(786853, "Doors"), BoundaryFeatureKind.Door);

        DoorAdjacencyIndex doors = DoorAdjacencyIndex.Build(
            new Dictionary<RegionId, IReadOnlyList<BoundaryFeature>>
            {
                [new RegionId(0)] = new[] { door },
                [new RegionId(1)] = new[] { door },
            },
            EntranceRule.Default);

        SpatialIndex index = SpatialIndex.Build(
            L2,
            new[] { Room(0, Rect(0, 0, 10, 8)), Room(1, Rect(10, 0, 10, 8)) },
            doors,
            Tolerance);

        // Far from either room, to prove position played no part.
        RoomMembership membership = index.Resolve(door.Element, new Point2D(5000, 5000));

        Assert.Equal(MembershipKind.ConnectsRooms, membership.Kind);
        Assert.Equal(new[] { new RegionId(0), new RegionId(1) }, membership.Rooms);
    }

    /// <summary>
    /// An index is an answer about one plan. Asked about another view it would
    /// be confidently wrong, so it carries what it was built from.
    /// </summary>
    [Fact]
    public void TheIndexRecordsWhatItIsAnAnswerAbout()
    {
        SpatialIndex index = Index(Room(0, Rect(0, 0, 10, 8)));

        Assert.Equal("L2", index.Context.LevelName);
        Assert.Equal("New Construction", index.Context.PhaseName);
        Assert.Equal(new RevitElementId(1350631), index.Context.ViewId);
    }

    [Fact]
    public void RoomsCanBeLookedUpByNumber()
    {
        SpatialIndex index = Index(Room(0, Rect(0, 0, 10, 8)), Room(3, Rect(20, 0, 4, 4)));

        Assert.Equal(new RegionId(3), index.Room(new RegionId(3))!.Id);
        Assert.Null(index.Room(new RegionId(99)));
    }

    [Fact]
    public void AnInteriorVoidIsStillSubtractedThroughTheIndex()
    {
        SpatialIndex index = Index(Room(0, Rect(0, 0, 10, 8), Rect(3, 3, 2, 2)));

        Assert.Equal(MembershipKind.NotInAnyRoom, index.Resolve(Element(500), new Point2D(4, 4)).Kind);
        Assert.Equal(MembershipKind.InOneRoom, index.Resolve(Element(500), new Point2D(1, 1)).Kind);
    }

    [Fact]
    public void AnEmptyPlanAnswersWithoutFalling()
    {
        SpatialIndex index = Index();

        Assert.Empty(index.Rooms);
        Assert.Equal(MembershipKind.NotInAnyRoom, index.Resolve(Element(500), new Point2D(0, 0)).Kind);
    }
}
