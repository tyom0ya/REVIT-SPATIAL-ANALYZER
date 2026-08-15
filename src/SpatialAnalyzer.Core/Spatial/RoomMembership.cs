using System.Globalization;
using SpatialAnalyzer.Core.Domain;
using SpatialAnalyzer.Core.Geometry;

namespace SpatialAnalyzer.Core.Spatial;

/// <summary>
/// How an element relates to the rooms found.
/// </summary>
public enum MembershipKind
{
    /// <summary>
    /// No room was found for it. It may stand outside every room, or in a region
    /// that did not qualify as one, and this does not claim to know which.
    /// </summary>
    NotInAnyRoom,

    InOneRoom,

    /// <summary>
    /// Standing on the boundary of one or more rooms rather than within any of
    /// them. Where an element is set into a wall, or pushed up against one, this
    /// is the honest answer.
    /// </summary>
    OnABoundary,

    /// <summary>
    /// A way through, belonging to the rooms on either side of it. The brief
    /// asks that selecting a door report both rooms, and this is that case.
    /// </summary>
    ConnectsRooms,

    /// <summary>
    /// Found inside more than one room at once, which cannot be: rooms do not
    /// overlap. Reported rather than resolved by picking one.
    /// </summary>
    InMoreThanOneRoom,
}

/// <summary>
/// What an element belongs to.
/// </summary>
public sealed record RoomMembership(
    ElementDescriptor Element,
    IReadOnlyList<RegionId> Rooms,
    MembershipKind Kind)
{
    public override string ToString() => Kind switch
    {
        MembershipKind.NotInAnyRoom => string.Create(CultureInfo.InvariantCulture, $"{Element}: in no room"),
        MembershipKind.ConnectsRooms when Rooms.Count == 2 => string.Create(
            CultureInfo.InvariantCulture,
            $"{Element}: connects {Rooms[0]} and {Rooms[1]}"),
        _ => string.Create(CultureInfo.InvariantCulture, $"{Element}: {Kind}, {string.Join(", ", Rooms)}"),
    };
}

/// <summary>
/// Answers the question the brief is built around: given an element, which
/// granular room is it in.
///
/// Two different questions hide inside that one, and they are answered
/// differently on purpose.
///
/// A door is not in a room. It is between two, and reporting only one of them
/// would be answering a question nobody asked. Anything the analysis already
/// established to be a way through is answered from the adjacency it belongs to,
/// not by testing where its centre point falls - which, for something sitting in
/// a wall, would land on a boundary and resolve to nothing.
///
/// Everything else is placed by where it stands. Furniture, equipment, columns:
/// a point, tested against each room's boundary.
///
/// Neither answer is forced. An element in no room says so rather than being
/// attached to the nearest one, because "nearest" is not a relationship a
/// building has.
/// </summary>
public sealed class RoomMembershipResolver
{
    private readonly IReadOnlyList<GranularRoom> _rooms;
    private readonly DoorAdjacencyIndex _adjacency;

    public RoomMembershipResolver(IReadOnlyList<GranularRoom> rooms, DoorAdjacencyIndex adjacency)
    {
        ArgumentNullException.ThrowIfNull(rooms);
        ArgumentNullException.ThrowIfNull(adjacency);

        _rooms = rooms;
        _adjacency = adjacency;
    }

    /// <param name="location">
    /// Where the element stands, in plan. Ignored for a door, whose answer comes
    /// from what it connects.
    /// </param>
    /// <param name="toleranceInternalFeet">
    /// How close to a wall counts as being on it rather than inside the room.
    /// </param>
    public RoomMembership Resolve(ElementDescriptor element, Point2D location, double toleranceInternalFeet)
    {
        ArgumentNullException.ThrowIfNull(element);

        // A way through is answered by what it joins. Its own position sits in
        // the wall, where a containment test has nothing useful to say.
        DoorAdjacency? adjacency = _adjacency.For(element.Id);
        if (adjacency is not null)
        {
            IReadOnlyList<RegionId> rooms = adjacency.Regions.Where(IsARoom).ToList();

            return new RoomMembership(
                element,
                rooms,
                rooms.Count switch
                {
                    0 => MembershipKind.NotInAnyRoom,

                    // A door with one room beside it belongs to that room. The
                    // other side may be outdoors, another storey, or a region
                    // that did not qualify; calling that "connects rooms" would
                    // name a relationship with something this analysis never
                    // found.
                    1 => MembershipKind.InOneRoom,

                    _ => MembershipKind.ConnectsRooms,
                });
        }

        var inside = new List<RegionId>();
        var onBoundary = new List<RegionId>();

        foreach (GranularRoom room in _rooms)
        {
            switch (PlanContainment.Of(room.Region, location, toleranceInternalFeet))
            {
                case Containment.Inside:
                    inside.Add(room.Id);
                    break;

                case Containment.OnBoundary:
                    onBoundary.Add(room.Id);
                    break;
            }
        }

        if (inside.Count == 1)
        {
            return new RoomMembership(element, inside, MembershipKind.InOneRoom);
        }

        if (inside.Count > 1)
        {
            // Rooms are disjoint, so this is a contradiction rather than a
            // finding. Naming one of them would hide it.
            return new RoomMembership(element, inside.OrderBy(r => r).ToList(), MembershipKind.InMoreThanOneRoom);
        }

        if (onBoundary.Count > 0)
        {
            return new RoomMembership(element, onBoundary.OrderBy(r => r).ToList(), MembershipKind.OnABoundary);
        }

        return new RoomMembership(element, Array.Empty<RegionId>(), MembershipKind.NotInAnyRoom);
    }

    /// <summary>
    /// An adjacency may name a region that did not qualify as a room - a door
    /// into a lift shaft, say. Only rooms are reported, because a region this
    /// analysis declined to call a room is not one to hand back as an answer.
    /// </summary>
    private bool IsARoom(RegionId region) => _rooms.Any(r => r.Id == region);
}
