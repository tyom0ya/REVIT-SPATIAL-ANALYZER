using SpatialAnalyzer.Core.Domain;
using SpatialAnalyzer.Core.Geometry;

namespace SpatialAnalyzer.Core.Spatial;

/// <summary>
/// A wall the model declines to treat as room bounding, reduced to the plan
/// line it draws and the element it came from.
/// </summary>
public sealed record PartitionWall(RevitElementId Id, BoundaryCurve CentreLine);

/// <summary>
/// A ring of partition walls that meets itself, with the area it encloses.
///
/// Only rings whose ends genuinely meet reach this type. A ring that is closed
/// in the graph but open on the ground is not one of these - it is a chain, and
/// it is reported as one.
/// </summary>
public sealed record PartitionLoop(IReadOnlyList<PartitionWall> Walls, AreaMeasurement Area);

/// <summary>
/// A run of partition walls that does not close, and the distance between the
/// two free ends that come nearest to each other.
///
/// The gap is measured and reported. It is never removed. What separates a
/// doorway from a drafting slip is not the size of the gap, and this project
/// does not pretend otherwise - a person looks at the number and decides. The
/// number is here so that deciding is possible; that is the whole of its job.
/// </summary>
/// <param name="GapBetweenNearestFreeEndsInternalFeet">
/// How far apart the run's two nearest loose ends are. Meaningful for a run
/// that almost closes on itself, and merely a fact about the run's shape for
/// one that does not - a long L of partitions has ends at opposite corners of
/// a flat, and that distance is not a gap in anything.
/// </param>
/// <param name="FreeEnds">
/// Every end of the run that meets no other wall in the set. These are what
/// actually matter: an end is either in open air, or it stops against a wall
/// that was not in the set being searched. Which of those it is cannot be
/// decided here, because the walls that would settle it were filtered out
/// before this saw them. Reporting the ends lets a caller with the whole model
/// ask the question this type cannot.
/// </param>
public sealed record PartitionChain(
    IReadOnlyList<PartitionWall> Walls,
    double GapBetweenNearestFreeEndsInternalFeet,
    IReadOnlyList<Point2D> FreeEnds);

/// <summary>
/// What the partition walls, taken together, turn out to describe.
/// </summary>
/// <param name="Tangled">
/// Walls that are part of some enclosure but meet at junctions where more than
/// two walls come together. Which of the several possible rings is a room is
/// not decidable from the lines alone, so they are reported rather than guessed
/// at.
/// </param>
public sealed record PartitionArrangement(
    IReadOnlyList<PartitionLoop> ClosedLoops,
    IReadOnlyList<PartitionChain> OpenChains,
    IReadOnlyList<PartitionWall> Tangled,
    double ToleranceInternalFeet);

/// <summary>
/// Finds the rings among walls the model walks past when it works out rooms.
///
/// Revit's plan topology will not divide a region for these walls - measured,
/// three separate ways, on the acceptance model. So the rings are found here
/// instead, from the lines the walls themselves draw.
///
/// Two ends are treated as meeting when they are within a tolerance the caller
/// states. That is the permitted use of tolerance and not a licence to close
/// gaps: it reconciles two coordinates that describe the same physical corner,
/// where two walls were drawn to the same point and stored a fraction of a
/// millimetre apart. It is never used to join ends that are actually apart.
///
/// The guard against that is not the tolerance alone but what happens next. Two
/// ends within tolerance are treated as the same junction, but the ring they
/// produce is then measured against the real coordinates, and a ring whose
/// corners do not actually meet is reported as an open chain with its gap
/// rather than as a room. Joining tolerantly and measuring strictly means a
/// chain of small tolerances cannot add up to a bridged doorway without the
/// output saying so.
/// </summary>
public static class PartitionLoops
{
    public static PartitionArrangement Find(
        IReadOnlyList<PartitionWall> walls,
        double toleranceInternalFeet)
    {
        ArgumentNullException.ThrowIfNull(walls);

        if (double.IsNaN(toleranceInternalFeet) || toleranceInternalFeet < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(toleranceInternalFeet),
                toleranceInternalFeet,
                "Tolerance must be a number and cannot be negative.");
        }

        if (walls.Count == 0)
        {
            return new PartitionArrangement(
                Array.Empty<PartitionLoop>(),
                Array.Empty<PartitionChain>(),
                Array.Empty<PartitionWall>(),
                toleranceInternalFeet);
        }

        int[] junction = JoinEndsThatMeet(walls, toleranceInternalFeet);

        // Walls hanging by one end enclose nothing, however long they are.
        // Stripping them repeatedly leaves exactly the walls that take part in
        // a ring, which is the only part of the arrangement that can be a room.
        bool[] hangsFree = StripFreeEnds(walls.Count, junction);

        var loops = new List<PartitionLoop>();
        var chains = new List<PartitionChain>();
        var tangled = new List<PartitionWall>();

        foreach (List<int> component in Components(walls.Count, junction, w => !hangsFree[w]))
        {
            if (!EveryJunctionJoinsExactlyTwo(component, junction))
            {
                tangled.AddRange(component.Select(w => walls[w]));
                continue;
            }

            List<PartitionWall> ordered = WalkTheRing(component, walls, junction, out BoundaryLoop ring);
            AreaMeasurement area = PlanArea.OfLoop(ring, toleranceInternalFeet);

            if (area.IsMeasured)
            {
                loops.Add(new PartitionLoop(ordered, area));
            }
            else
            {
                // Closed as a graph, open on the ground. Reported as what it is.
                chains.Add(new PartitionChain(ordered, ring.LargestGapInternalFeet, Array.Empty<Point2D>()));
            }
        }

        foreach (List<int> component in Components(walls.Count, junction, w => hangsFree[w]))
        {
            IReadOnlyList<Point2D> ends = FreeEndsOf(component, walls, junction);

            chains.Add(new PartitionChain(
                component.Select(w => walls[w]).ToList(),
                NearestApproachOf(ends),
                ends));
        }

        return new PartitionArrangement(
            loops.OrderByDescending(l => l.Area.InternalSquareFeet).ToList(),
            chains.OrderBy(c => c.GapBetweenNearestFreeEndsInternalFeet).ToList(),
            tangled,
            toleranceInternalFeet);
    }

    /// <summary>
    /// Gives every wall end a junction number, shared with the ends that are
    /// within tolerance of it.
    /// </summary>
    private static int[] JoinEndsThatMeet(IReadOnlyList<PartitionWall> walls, double tolerance)
    {
        int ends = walls.Count * 2;
        var parent = new int[ends];
        for (int i = 0; i < ends; i++)
        {
            parent[i] = i;
        }

        int Find(int i)
        {
            while (parent[i] != i)
            {
                parent[i] = parent[parent[i]];
                i = parent[i];
            }

            return i;
        }

        Point2D At(int end) => (end % 2 == 0) ? walls[end / 2].CentreLine.Start : walls[end / 2].CentreLine.End;

        for (int a = 0; a < ends; a++)
        {
            for (int b = a + 1; b < ends; b++)
            {
                if (At(a).DistanceTo(At(b)) <= tolerance)
                {
                    parent[Find(a)] = Find(b);
                }
            }
        }

        var junction = new int[ends];
        for (int i = 0; i < ends; i++)
        {
            junction[i] = Find(i);
        }

        return junction;
    }

    /// <summary>
    /// Marks every wall that can be removed by repeatedly taking away whatever
    /// is attached at only one end. What survives takes part in a ring.
    /// </summary>
    private static bool[] StripFreeEnds(int wallCount, int[] junction)
    {
        var stripped = new bool[wallCount];
        var degree = new Dictionary<int, int>();

        foreach (int j in junction)
        {
            degree[j] = degree.GetValueOrDefault(j) + 1;
        }

        bool removedSomething = true;
        while (removedSomething)
        {
            removedSomething = false;

            for (int w = 0; w < wallCount; w++)
            {
                if (stripped[w])
                {
                    continue;
                }

                int start = junction[w * 2];
                int end = junction[(w * 2) + 1];

                if (degree[start] > 1 && degree[end] > 1)
                {
                    continue;
                }

                stripped[w] = true;
                degree[start]--;
                degree[end]--;
                removedSomething = true;
            }
        }

        return stripped;
    }

    private static IEnumerable<List<int>> Components(int wallCount, int[] junction, Func<int, bool> include)
    {
        var byJunction = new Dictionary<int, List<int>>();
        for (int w = 0; w < wallCount; w++)
        {
            if (!include(w))
            {
                continue;
            }

            byJunction.TryAdd(junction[w * 2], new List<int>());
            byJunction[junction[w * 2]].Add(w);
            byJunction.TryAdd(junction[(w * 2) + 1], new List<int>());
            byJunction[junction[(w * 2) + 1]].Add(w);
        }

        var seen = new bool[wallCount];
        for (int w = 0; w < wallCount; w++)
        {
            if (!include(w) || seen[w])
            {
                continue;
            }

            var component = new List<int>();
            var queue = new Queue<int>();
            queue.Enqueue(w);
            seen[w] = true;

            while (queue.Count > 0)
            {
                int current = queue.Dequeue();
                component.Add(current);

                foreach (int end in new[] { junction[current * 2], junction[(current * 2) + 1] })
                {
                    foreach (int neighbour in byJunction[end])
                    {
                        if (!seen[neighbour])
                        {
                            seen[neighbour] = true;
                            queue.Enqueue(neighbour);
                        }
                    }
                }
            }

            yield return component;
        }
    }

    private static bool EveryJunctionJoinsExactlyTwo(List<int> component, int[] junction)
    {
        var degree = new Dictionary<int, int>();
        foreach (int w in component)
        {
            degree[junction[w * 2]] = degree.GetValueOrDefault(junction[w * 2]) + 1;
            degree[junction[(w * 2) + 1]] = degree.GetValueOrDefault(junction[(w * 2) + 1]) + 1;
        }

        return degree.Values.All(d => d == 2);
    }

    /// <summary>
    /// Puts a ring's walls in order around it, head to tail, and builds the
    /// loop whose junction gaps can then be measured against the real
    /// coordinates.
    /// </summary>
    private static List<PartitionWall> WalkTheRing(
        List<int> component,
        IReadOnlyList<PartitionWall> walls,
        int[] junction,
        out BoundaryLoop ring)
    {
        var ordered = new List<PartitionWall>();
        var segments = new List<BoundarySegment>();
        var used = new HashSet<int>();

        int current = component[0];
        int arriveAt = junction[(current * 2) + 1];
        bool forwards = true;

        while (true)
        {
            used.Add(current);
            PartitionWall wall = walls[current];
            ordered.Add(wall);

            segments.Add(new BoundarySegment(
                forwards ? wall.CentreLine : Reversed(wall.CentreLine),
                BoundaryReference.Host(wall.Id)));

            int next = component.FirstOrDefault(
                w => !used.Contains(w) && (junction[w * 2] == arriveAt || junction[(w * 2) + 1] == arriveAt),
                -1);

            if (next < 0)
            {
                break;
            }

            forwards = junction[next * 2] == arriveAt;
            arriveAt = forwards ? junction[(next * 2) + 1] : junction[next * 2];
            current = next;
        }

        ring = new BoundaryLoop(segments);
        return ordered;
    }

    private static BoundaryCurve Reversed(BoundaryCurve curve) => new(
        curve.Kind,
        curve.End,
        curve.Start,
        curve.LengthInternalFeet,
        curve.Tessellation.Reverse().ToList());

    /// <summary>
    /// How far apart the nearest two loose ends of a chain are - the distance
    /// that would have to be crossed for it to enclose anything.
    ///
    /// Reported so the size of the thing is known. A chain with a three foot
    /// opening is a room with a doorway; a chain with a hair's breadth opening
    /// is probably a drafting slip. Both come back here as a number, and which
    /// is which stays a person's call.
    /// </summary>
    private static double NearestApproachOf(IReadOnlyList<Point2D> ends)
    {
        double nearest = double.PositiveInfinity;

        for (int a = 0; a < ends.Count; a++)
        {
            for (int b = a + 1; b < ends.Count; b++)
            {
                nearest = Math.Min(nearest, ends[a].DistanceTo(ends[b]));
            }
        }

        return nearest;
    }

    private static IReadOnlyList<Point2D> FreeEndsOf(
        List<int> component,
        IReadOnlyList<PartitionWall> walls,
        int[] junction)
    {
        var degree = new Dictionary<int, int>();
        foreach (int w in component)
        {
            degree[junction[w * 2]] = degree.GetValueOrDefault(junction[w * 2]) + 1;
            degree[junction[(w * 2) + 1]] = degree.GetValueOrDefault(junction[(w * 2) + 1]) + 1;
        }

        var free = new List<Point2D>();
        foreach (int w in component)
        {
            if (degree[junction[w * 2]] == 1)
            {
                free.Add(walls[w].CentreLine.Start);
            }

            if (degree[junction[(w * 2) + 1]] == 1)
            {
                free.Add(walls[w].CentreLine.End);
            }
        }

        return free;
    }
}
