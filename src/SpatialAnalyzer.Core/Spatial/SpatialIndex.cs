using System.Globalization;
using SpatialAnalyzer.Core.Domain;
using SpatialAnalyzer.Core.Geometry;

namespace SpatialAnalyzer.Core.Spatial;

/// <summary>
/// The finished analysis of one plan, ready to be asked about repeatedly.
///
/// Working out the rooms is the expensive part: every region on the level has to
/// be read out of Revit, given a temporary room to expose its boundary, and have
/// its boundary examined. Doing that again for each element somebody selects
/// would make the tool unusable, and would also mean the answers could drift
/// between one question and the next if anything in the model moved. Both are
/// solved the same way - build it once, then ask it.
///
/// Each room is remembered with the rectangle around it, so a question about a
/// point discards almost every room by comparing four numbers before any
/// boundary is walked.
///
/// The index records the view, level and phase it was built from. An index is
/// only an answer about the plan it came from, and one asked about a different
/// view would give confident and wrong answers - so it carries what it is about,
/// and callers can check.
/// </summary>
public sealed class SpatialIndex
{
    private readonly IReadOnlyList<(GranularRoom Room, PlanBounds Bounds)> _rooms;
    private readonly RoomMembershipResolver _resolver;

    private SpatialIndex(
        AnalysisContextInfo context,
        IReadOnlyList<(GranularRoom Room, PlanBounds Bounds)> rooms,
        DoorAdjacencyIndex doors,
        double closureToleranceInternalFeet)
    {
        Context = context;
        _rooms = rooms;
        Doors = doors;
        ClosureToleranceInternalFeet = closureToleranceInternalFeet;
        _resolver = new RoomMembershipResolver(rooms.Select(r => r.Room).ToList(), doors);
    }

    public static SpatialIndex Build(
        AnalysisContextInfo context,
        IReadOnlyList<GranularRoom> rooms,
        DoorAdjacencyIndex doors,
        double closureToleranceInternalFeet)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(rooms);
        ArgumentNullException.ThrowIfNull(doors);

        var bounded = rooms
            .Select(room => (Room: room, Bounds: BoundsOf(room)))
            .ToList();

        return new SpatialIndex(context, bounded, doors, closureToleranceInternalFeet);
    }

    /// <summary>What this index is an answer about.</summary>
    public AnalysisContextInfo Context { get; }

    public DoorAdjacencyIndex Doors { get; }

    public double ClosureToleranceInternalFeet { get; }

    public IReadOnlyList<GranularRoom> Rooms => _rooms.Select(r => r.Room).ToList();

    /// <summary>
    /// Which room an element belongs to.
    ///
    /// A way through is answered from what it connects, before any question of
    /// position arises, because a door stands inside a wall. Everything else is
    /// placed by where it stands, and the rectangles rule out the rooms it
    /// cannot be in first.
    /// </summary>
    public RoomMembership Resolve(ElementDescriptor element, Point2D location)
    {
        ArgumentNullException.ThrowIfNull(element);

        // Doors are not placed by position at all, so the rectangles have
        // nothing to offer and the resolver is asked directly.
        if (Doors.For(element.Id) is not null)
        {
            return _resolver.Resolve(element, location, ClosureToleranceInternalFeet);
        }

        List<GranularRoom> candidates = _rooms
            .Where(r => r.Bounds.Contains(location, ClosureToleranceInternalFeet))
            .Select(r => r.Room)
            .ToList();

        if (candidates.Count == 0)
        {
            return new RoomMembership(element, Array.Empty<RegionId>(), MembershipKind.NotInAnyRoom);
        }

        // The same resolver, over fewer rooms. Narrowing the question must not
        // change the answer, so nothing about how it is decided lives here.
        return new RoomMembershipResolver(candidates, Doors)
            .Resolve(element, location, ClosureToleranceInternalFeet);
    }

    /// <summary>The rooms an element encloses, rather than the one it is in.</summary>
    public IReadOnlyList<RegionId> RoomsBoundedBy(RevitElementId element) => _resolver.RoomsBoundedBy(element);

    public GranularRoom? Room(RegionId id) => _rooms.FirstOrDefault(r => r.Room.Id == id).Room;

    /// <summary>
    /// The rectangle around a room's outer boundary.
    ///
    /// A room is always enclosed - an unenclosed region cannot become one - so
    /// the outer loop is always there to measure.
    /// </summary>
    private static PlanBounds BoundsOf(GranularRoom room)
    {
        BoundaryLoop outer = room.Region.OuterLoop
            ?? throw new InvalidOperationException(
                string.Create(CultureInfo.InvariantCulture, $"Room {room.Id} has no outer boundary, which a room cannot."));

        return PlanBounds.Around(
            outer.Segments.SelectMany(segment => segment.Curve.Tessellation));
    }

    public override string ToString() => string.Create(
        CultureInfo.InvariantCulture,
        $"{Rooms.Count} room(s) on {Context.LevelName}, {Context.ViewName}, phase {Context.PhaseName}");
}
