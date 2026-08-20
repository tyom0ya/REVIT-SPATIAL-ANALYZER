using SpatialAnalyzer.Core.Domain;
using SpatialAnalyzer.Core.Geometry;

namespace SpatialAnalyzer.Core.Spatial;

/// <summary>
/// One wall as the plan sees it, and whether the model bounds rooms with it.
/// </summary>
public sealed record PlanWall(RevitElementId Id, BoundaryCurve CentreLine, bool IgnoredForRooms);

/// <summary>
/// One straight run of boundary, and where it came from.
///
/// A wall centre line says where a wall is on average. It is not where the wall
/// stands: a room bounded by centre lines is half a wall too big in every
/// direction, and a wall whose two sides bound different rooms has only one
/// centre line to offer both. The face of the wall is the surface somebody
/// paints, and it is what the room is actually bounded by.
///
/// So the arrangement is fed traces rather than walls. The traversal does not
/// care where a segment came from - it needs two endpoints and something to
/// blame the resulting edge on - which is why this could be introduced without
/// disturbing what already works.
/// </summary>
/// <param name="Face">
/// Which face of the element drew this, as an index into whatever list the
/// caller read them from, or <see cref="PlanFaces.NoFace"/> when the trace came
/// from a centre line and there is no face to name.
/// </param>
public sealed record PlanTrace(
    RevitElementId Element,
    int Face,
    Point2D A,
    Point2D B,
    bool IgnoredForRooms);

/// <summary>
/// A space enclosed by walls, however the model feels about them.
/// </summary>
/// <param name="EdgeFaces">
/// Which face drew each edge, in step with <c>Outline</c>: entry i belongs to
/// the edge running from outline point i to the one after it. <c>Faces</c> says
/// which faces took part; this says where each of them is, which is what lets a
/// caller ask whether a face looks into this loop or out of it.
/// </param>
/// <param name="TouchesAWallIgnoredForRooms">
/// True when at least one wall on this face's boundary is one the model walks
/// past when working out rooms. Those are the faces worth looking at: a face
/// bounded entirely by walls Revit already respects is a room Revit already
/// reports.
/// </param>
public sealed record PlanFace(
    IReadOnlyList<Point2D> Outline,
    AreaMeasurement Area,
    IReadOnlyList<RevitElementId> Walls,
    bool TouchesAWallIgnoredForRooms,
    IReadOnlyList<int> Faces,
    IReadOnlyList<int> EdgeFaces);

/// <param name="FacesTooNarrowToStandIn">
/// Faces set aside as construction gaps rather than spaces. Counted rather
/// than silently dropped, so a floor where the count is large is a question
/// somebody can ask.
/// </param>
public sealed record PlanSubdivision(
    IReadOnlyList<PlanFace> Faces,
    int WallsRead,
    int SegmentsAfterSplitting,
    int FacesTooNarrowToStandIn,
    double ToleranceInternalFeet);

/// <summary>
/// Divides a floor plan into the spaces its walls actually enclose.
///
/// This exists because the obvious approach does not work and the reason took
/// measuring. Walls the model is told not to treat as room bounding are
/// invisible to Revit's own topology, and every attempt to make Revit account
/// for them failed: the flag cannot be changed on grouped walls from outside
/// group edit mode, and room separation lines laid along them were accepted in
/// the right category on the right level and moved the region count not at all.
///
/// Working only from those ignored walls does not work either, and that failure
/// was more interesting. On the acceptance model they enclose nothing whatever -
/// but every single one of their loose ends, forty-three of forty-three, stops
/// dead against another wall at zero distance. They do enclose spaces. They just
/// do it together with the ordinary room bounding walls, and a search given only
/// half the walls can never close a loop.
///
/// So this takes all of them. The lines are split wherever they cross, the
/// pieces are assembled into a graph, and its faces are walked. A face is a
/// space; a face with an ignored wall on its boundary is a space the model is
/// not reporting.
///
/// The gap rule is untouched throughout. Ends are treated as meeting only when
/// they are within the stated tolerance, crossings are computed rather than
/// assumed, and every face is measured against real coordinates before it counts
/// as enclosed. Nothing here can bridge a doorway, because nothing here joins
/// anything that was not already touching.
/// </summary>
public static class PlanFaces
{
    /// <summary>
    /// The narrowest a face may average and still be a space, in feet - about
    /// a hundred and fifty millimetres.
    ///
    /// Two walls running near each other and closed at both ends enclose a
    /// face, and it is a real face: the arrangement genuinely has one there.
    /// It is not a room. Nobody stands in the hundred millimetre gap between a
    /// kitchen wall and a bathroom wall, and reporting it as a space the model
    /// failed to notice is worse than saying nothing, because it buries the
    /// rooms that matter among slivers.
    ///
    /// Judged on mean width - twice the area over the perimeter - rather than
    /// on area alone. Area alone cannot tell a small cupboard from a long thin
    /// crack, and a cupboard is a room while a crack is not.
    /// </summary>
    public const double NarrowestUsableWidthFeet = 0.5;

    /// <summary>
    /// A point that lies inside a face, for putting a room at.
    ///
    /// The centroid is tried first and is usually right, but an L-shaped flat
    /// has its centroid out in the corridor - placing a room there would put it
    /// in the wrong space entirely and look, from the outside, exactly like the
    /// analysis being wrong. So the answer is tested rather than assumed, and
    /// the search falls back to points just inside each edge and then to a
    /// sweep of the interior.
    /// </summary>
    public static bool TryFindPointInside(PlanFace face, out Point2D inside)
    {
        ArgumentNullException.ThrowIfNull(face);

        IReadOnlyList<Point2D> outline = face.Outline;
        inside = default;

        if (outline.Count < 3)
        {
            return false;
        }

        if (Encloses(outline, Centroid(outline)))
        {
            inside = Centroid(outline);
            return true;
        }

        // Just inside the middle of each edge, stepping along the inward
        // normal. For any simple polygon at least one such point lands in it.
        for (int i = 0; i < outline.Count; i++)
        {
            Point2D a = outline[i];
            Point2D b = outline[(i + 1) % outline.Count];

            double dx = b.X - a.X;
            double dy = b.Y - a.Y;
            double length = Math.Sqrt((dx * dx) + (dy * dy));

            if (length <= 0)
            {
                continue;
            }

            // The outline is traced anticlockwise, so the interior lies to the
            // left of each edge.
            double step = Math.Min(length, 0.1) / 2.0;
            var candidate = new Point2D(
                ((a.X + b.X) / 2.0) - (dy / length * step),
                ((a.Y + b.Y) / 2.0) + (dx / length * step));

            if (Encloses(outline, candidate))
            {
                inside = candidate;
                return true;
            }
        }

        double minX = outline.Min(p => p.X);
        double maxX = outline.Max(p => p.X);
        double minY = outline.Min(p => p.Y);
        double maxY = outline.Max(p => p.Y);

        for (int x = 1; x < 8; x++)
        {
            for (int y = 1; y < 8; y++)
            {
                var candidate = new Point2D(
                    minX + ((maxX - minX) * x / 8.0),
                    minY + ((maxY - minY) * y / 8.0));

                if (Encloses(outline, candidate))
                {
                    inside = candidate;
                    return true;
                }
            }
        }

        return false;
    }

    private static Point2D Centroid(IReadOnlyList<Point2D> outline)
    {
        double twiceArea = 0;
        double x = 0;
        double y = 0;

        for (int i = 0; i < outline.Count; i++)
        {
            Point2D a = outline[i];
            Point2D b = outline[(i + 1) % outline.Count];

            double cross = (a.X * b.Y) - (b.X * a.Y);
            twiceArea += cross;
            x += (a.X + b.X) * cross;
            y += (a.Y + b.Y) * cross;
        }

        return Math.Abs(twiceArea) < 1e-12
            ? new Point2D(outline.Average(p => p.X), outline.Average(p => p.Y))
            : new Point2D(x / (3 * twiceArea), y / (3 * twiceArea));
    }

    /// <summary>
    /// Ray casting, with the half-open rule at each edge so a ray passing
    /// exactly through a corner is counted once rather than twice or not at
    /// all.
    /// </summary>
    /// <summary>
    /// Whether a face contains a point - used to ask whether a space already
    /// has a room in it.
    /// </summary>
    public static bool Contains(PlanFace face, Point2D point)
    {
        ArgumentNullException.ThrowIfNull(face);
        return Encloses(face.Outline, point);
    }

    private static bool Encloses(IReadOnlyList<Point2D> outline, Point2D point)
    {
        bool inside = false;

        for (int i = 0, j = outline.Count - 1; i < outline.Count; j = i++)
        {
            Point2D a = outline[i];
            Point2D b = outline[j];

            if ((a.Y > point.Y) == (b.Y > point.Y))
            {
                continue;
            }

            double at = ((b.X - a.X) * (point.Y - a.Y) / (b.Y - a.Y)) + a.X;
            if (point.X < at)
            {
                inside = !inside;
            }
        }

        return inside;
    }

    public static PlanSubdivision Find(IReadOnlyList<PlanWall> walls, double toleranceInternalFeet)
    {
        ArgumentNullException.ThrowIfNull(walls);
        return FindAmong(Flatten(walls), toleranceInternalFeet);
    }

    /// <summary>
    /// The same subdivision, from traces the caller has already worked out.
    ///
    /// This is the whole of the algorithm; the wall overload above is a way of
    /// producing traces from centre lines and nothing more. The traversal never
    /// knew what a wall was - it needs two endpoints and something to blame the
    /// edge on - so feeding it the faces of the wall instead of its centre line
    /// changes what the answer means without changing a line of how it is
    /// reached.
    /// </summary>
    public static PlanSubdivision FindAmong(
        IReadOnlyList<PlanTrace> traces,
        double toleranceInternalFeet)
    {
        ArgumentNullException.ThrowIfNull(traces);

        if (double.IsNaN(toleranceInternalFeet) || toleranceInternalFeet < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(toleranceInternalFeet),
                toleranceInternalFeet,
                "Tolerance must be a number and cannot be negative.");
        }

        int elements = traces.Select(t => t.Element).Distinct().Count();

        List<Piece> pieces = SplitWhereTheyCross(Cut(traces), toleranceInternalFeet);

        if (pieces.Count == 0)
        {
            return new PlanSubdivision(
                Array.Empty<PlanFace>(),
                elements,
                0,
                0,
                toleranceInternalFeet);
        }

        var graph = new Graph(pieces, toleranceInternalFeet);
        graph.PruneDeadEnds();

        var faces = new List<PlanFace>();
        int tooNarrow = 0;

        foreach (List<int> walk in graph.WalkFaces())
        {
            PlanFace? face = graph.Describe(walk, toleranceInternalFeet);
            if (face is null)
            {
                continue;
            }

            if (MeanWidthOf(face) < NarrowestUsableWidthFeet)
            {
                tooNarrow++;
                continue;
            }

            faces.Add(face);
        }

        return new PlanSubdivision(
            faces.OrderByDescending(f => f.Area.InternalSquareFeet).ToList(),
            elements,
            pieces.Count,
            tooNarrow,
            toleranceInternalFeet);
    }

    private static double MeanWidthOf(PlanFace face)
    {
        double perimeter = 0;

        for (int i = 0; i < face.Outline.Count; i++)
        {
            perimeter += face.Outline[i].DistanceTo(face.Outline[(i + 1) % face.Outline.Count]);
        }

        return perimeter > 0 ? 2 * face.Area.InternalSquareFeet / perimeter : 0;
    }

    /// <summary>One straight piece of a wall, before any splitting.</summary>
    private sealed record Piece(Point2D A, Point2D B, RevitElementId Wall, int Face, bool Ignored);

    /// <summary>
    /// The trace a wall centre line draws, one straight run at a time.
    ///
    /// An arc arrives as the tessellation Revit itself drew, so a curved wall
    /// is followed rather than straightened, and no curve mathematics is
    /// reimplemented here.
    /// </summary>
    private static List<PlanTrace> Flatten(IReadOnlyList<PlanWall> walls)
    {
        var traces = new List<PlanTrace>();

        foreach (PlanWall wall in walls)
        {
            IReadOnlyList<Point2D> points = wall.CentreLine.Tessellation;

            for (int i = 0; i < points.Count - 1; i++)
            {
                if (points[i] != points[i + 1])
                {
                    traces.Add(new PlanTrace(
                        wall.Id,
                        NoFace,
                        points[i],
                        points[i + 1],
                        wall.IgnoredForRooms));
                }
            }
        }

        return traces;
    }

    /// <summary>
    /// What a trace whose segment came from a centre line names as its face,
    /// there being none.
    /// </summary>
    public const int NoFace = -1;

    private static List<Piece> Cut(IReadOnlyList<PlanTrace> traces)
    {
        var pieces = new List<Piece>(traces.Count);

        foreach (PlanTrace trace in traces)
        {
            if (trace.A != trace.B)
            {
                pieces.Add(new Piece(
                    trace.A,
                    trace.B,
                    trace.Element,
                    trace.Face,
                    trace.IgnoredForRooms));
            }
        }

        return pieces;
    }

    /// <summary>
    /// Cuts every piece at every point another piece crosses it.
    ///
    /// Without this a wall running past a junction stays one edge, and the face
    /// on either side of it cannot be told apart. Crossings are solved for, not
    /// guessed: two pieces that nearly meet do not intersect and are not made
    /// to.
    /// </summary>
    private static List<Piece> SplitWhereTheyCross(List<Piece> pieces, double tolerance)
    {
        var split = new List<Piece>();
        var near = Bucket(pieces, tolerance);
        var candidates = new HashSet<int>();

        for (int i = 0; i < pieces.Count; i++)
        {
            Piece piece = pieces[i];

            var cuts = new List<double> { 0.0, 1.0 };

            candidates.Clear();
            foreach ((long, long) cell in CellsOver(piece, tolerance))
            {
                if (near.TryGetValue(cell, out List<int>? sharing))
                {
                    candidates.UnionWith(sharing);
                }
            }

            foreach (int j in candidates)
            {
                if (i != j && TryCross(piece, pieces[j], tolerance, out double at))
                {
                    cuts.Add(at);
                }
            }

            cuts.Sort();

            for (int c = 0; c < cuts.Count - 1; c++)
            {
                Point2D from = Along(piece, cuts[c]);
                Point2D to = Along(piece, cuts[c + 1]);

                if (from.DistanceTo(to) > tolerance)
                {
                    split.Add(piece with { A = from, B = to });
                }
            }
        }

        return split;
    }

    /// <summary>
    /// How wide a bucket is, in feet.
    ///
    /// Only a search structure: which pieces are tested against each other, not
    /// which of them cross. Every pair that shares a bucket is still tested
    /// exactly as before and every pair that does not share one cannot touch,
    /// because each piece is filed under every bucket its bounding box reaches
    /// and that box is grown by the tolerance first. The answer is the same as
    /// testing all of them; the time is not.
    ///
    /// Ten feet is a compromise. Smaller buckets mean fewer pairs tested and
    /// more buckets for a long wall to be filed under; a storey of a building
    /// at this size is a few hundred buckets across.
    /// </summary>
    private const double BucketFeet = 10.0;

    private static Dictionary<(long, long), List<int>> Bucket(List<Piece> pieces, double tolerance)
    {
        var near = new Dictionary<(long, long), List<int>>();

        for (int i = 0; i < pieces.Count; i++)
        {
            foreach ((long, long) cell in CellsOver(pieces[i], tolerance))
            {
                if (!near.TryGetValue(cell, out List<int>? sharing))
                {
                    sharing = new List<int>();
                    near[cell] = sharing;
                }

                sharing.Add(i);
            }
        }

        return near;
    }

    /// <summary>
    /// Every bucket a piece reaches, its bounding box first grown by the
    /// tolerance so that two pieces which only just touch still meet in one.
    /// </summary>
    private static IEnumerable<(long, long)> CellsOver(Piece piece, double tolerance)
    {
        double grow = Math.Max(tolerance, 0);

        long fromX = (long)Math.Floor((Math.Min(piece.A.X, piece.B.X) - grow) / BucketFeet);
        long toX = (long)Math.Floor((Math.Max(piece.A.X, piece.B.X) + grow) / BucketFeet);
        long fromY = (long)Math.Floor((Math.Min(piece.A.Y, piece.B.Y) - grow) / BucketFeet);
        long toY = (long)Math.Floor((Math.Max(piece.A.Y, piece.B.Y) + grow) / BucketFeet);

        for (long x = fromX; x <= toX; x++)
        {
            for (long y = fromY; y <= toY; y++)
            {
                yield return (x, y);
            }
        }
    }

    private static Point2D Along(Piece piece, double t) =>
        new(piece.A.X + ((piece.B.X - piece.A.X) * t), piece.A.Y + ((piece.B.Y - piece.A.Y) * t));

    /// <summary>
    /// Where one piece crosses another, as a fraction along the first.
    ///
    /// Parallel pieces never report a crossing. Two walls lying along each other
    /// share no single point to cut at, and inventing one would put a vertex
    /// where the building has none.
    ///
    /// The second piece is allowed to reach its crossing a hair beyond either
    /// end, and that slack is the difference between finding a room and not.
    /// A partition butting into the middle of another wall meets it exactly at
    /// the partition's own endpoint, so the crossing parameter along the
    /// partition is nought or one - and arithmetic on doubles lands such a
    /// value a whisper outside the range as often as inside it. Rejecting those
    /// leaves the wall unsplit and the space around it forever open. The slack
    /// is the caller's tolerance expressed as a fraction of that piece's
    /// length, so it admits exactly the ends that already touch and no others.
    /// </summary>
    private static bool TryCross(Piece piece, Piece other, double tolerance, out double at)
    {
        at = 0;

        double px = piece.B.X - piece.A.X;
        double py = piece.B.Y - piece.A.Y;
        double qx = other.B.X - other.A.X;
        double qy = other.B.Y - other.A.Y;

        double denominator = (px * qy) - (py * qx);
        if (Math.Abs(denominator) < 1e-12)
        {
            return false;
        }

        double dx = other.A.X - piece.A.X;
        double dy = other.A.Y - piece.A.Y;

        double t = ((dx * qy) - (dy * qx)) / denominator;
        double u = ((dx * py) - (dy * px)) / denominator;

        double reach = other.A.DistanceTo(other.B);
        double slack = reach > 0 ? tolerance / reach : 0;

        if (t <= 0 || t >= 1 || u < -slack || u > 1 + slack)
        {
            return false;
        }

        at = t;
        return true;
    }

    /// <summary>
    /// The pieces as a planar graph: shared corners become shared vertices, and
    /// each piece becomes a pair of opposite directed edges.
    /// </summary>
    private sealed class Graph
    {
        private readonly List<Point2D> _vertices = new();
        private readonly List<Edge> _edges = new();
        private readonly Dictionary<int, List<int>> _outgoing = new();
        private readonly Dictionary<(long, long), List<int>> _near = new();
        private readonly double _cell;
        private readonly bool[] _dead;

        internal Graph(List<Piece> pieces, double tolerance)
        {
            // A bucket exactly the tolerance wide, so anything close enough to
            // be the same vertex is at most one bucket away on each axis and
            // the nine around a point are the whole search.
            _cell = Math.Max(tolerance, 1e-9);

            var seen = new HashSet<(int, int)>();

            foreach (Piece piece in pieces)
            {
                int a = VertexAt(piece.A, tolerance);
                int b = VertexAt(piece.B, tolerance);

                // A piece whose ends land on one vertex has been swallowed by
                // the tolerance and encloses nothing. A pair already present is
                // a wall drawn twice along the same line; one edge is enough.
                if (a == b || !seen.Add((Math.Min(a, b), Math.Max(a, b))))
                {
                    continue;
                }

                Add(a, b, piece);
                Add(b, a, piece);
            }

            _dead = new bool[_edges.Count];

            foreach (List<int> fan in _outgoing.Values)
            {
                fan.Sort((x, y) => _edges[x].Angle.CompareTo(_edges[y].Angle));
            }
        }

        private sealed record Edge(int From, int To, double Angle, RevitElementId Wall, int Face, bool Ignored);

        private void Add(int from, int to, Piece piece)
        {
            _edges.Add(new Edge(
                from,
                to,
                Math.Atan2(_vertices[to].Y - _vertices[from].Y, _vertices[to].X - _vertices[from].X),
                piece.Wall,
                piece.Face,
                piece.Ignored));

            if (!_outgoing.TryGetValue(from, out List<int>? fan))
            {
                fan = new List<int>();
                _outgoing[from] = fan;
            }

            fan.Add(_edges.Count - 1);
        }

        private int VertexAt(Point2D point, double tolerance)
        {
            long cx = (long)Math.Floor(point.X / _cell);
            long cy = (long)Math.Floor(point.Y / _cell);

            for (long dx = -1; dx <= 1; dx++)
            {
                for (long dy = -1; dy <= 1; dy++)
                {
                    if (!_near.TryGetValue((cx + dx, cy + dy), out List<int>? sharing))
                    {
                        continue;
                    }

                    foreach (int i in sharing)
                    {
                        if (_vertices[i].DistanceTo(point) <= tolerance)
                        {
                            return i;
                        }
                    }
                }
            }

            _vertices.Add(point);

            if (!_near.TryGetValue((cx, cy), out List<int>? own))
            {
                own = new List<int>();
                _near[(cx, cy)] = own;
            }

            own.Add(_vertices.Count - 1);
            return _vertices.Count - 1;
        }

        /// <summary>
        /// Removes whatever hangs by one end, repeatedly.
        ///
        /// A wall attached at only one end encloses nothing, and leaving it in
        /// makes every face walk run out along it and back, which contributes
        /// no area and a great deal of confusion.
        /// </summary>
        internal void PruneDeadEnds()
        {
            // Worked from a queue rather than by sweeping the whole graph over
            // and over. Killing one hanging edge exposes the next one back
            // along the same chain, and the only vertex whose count changed is
            // the one at the far end - so that is the only one worth looking at
            // again. A plan traced from wall faces has a hanging end at every
            // free wall end and every reveal, and re-reading every vertex once
            // per link of every chain is the difference between a moment and a
            // minute.
            var alive = new int[_vertices.Count];

            foreach ((int vertex, List<int> fan) in _outgoing)
            {
                alive[vertex] = fan.Count;
            }

            var hanging = new Queue<int>();

            for (int vertex = 0; vertex < alive.Length; vertex++)
            {
                if (alive[vertex] == 1)
                {
                    hanging.Enqueue(vertex);
                }
            }

            while (hanging.Count > 0)
            {
                int vertex = hanging.Dequeue();

                if (alive[vertex] != 1)
                {
                    continue;
                }

                int edge = _outgoing[vertex].FirstOrDefault(e => !_dead[e], -1);
                if (edge < 0)
                {
                    alive[vertex] = 0;
                    continue;
                }

                _dead[edge] = true;
                _dead[Twin(edge)] = true;

                alive[vertex]--;
                int beyond = _edges[edge].To;
                alive[beyond]--;

                if (alive[beyond] == 1)
                {
                    hanging.Enqueue(beyond);
                }
            }
        }

        /// <summary>
        /// The twin of an edge is the one running the other way, added directly
        /// after it.
        /// </summary>
        private int Twin(int edge) => (edge % 2 == 0) ? edge + 1 : edge - 1;

        /// <summary>
        /// Walks every face of the subdivision.
        ///
        /// At each vertex the walk takes the sharpest available turn, which is
        /// what keeps it hugging one face instead of wandering across the plan.
        /// Every directed edge belongs to exactly one face, so following them
        /// until none is left visits every space exactly once.
        /// </summary>
        internal IEnumerable<List<int>> WalkFaces()
        {
            var used = new bool[_edges.Count];

            for (int start = 0; start < _edges.Count; start++)
            {
                if (used[start] || _dead[start])
                {
                    continue;
                }

                var walk = new List<int>();
                int current = start;

                while (!used[current])
                {
                    used[current] = true;
                    walk.Add(current);
                    current = Next(current);

                    if (current < 0)
                    {
                        break;
                    }
                }

                if (walk.Count >= 3)
                {
                    yield return walk;
                }
            }
        }

        private int Next(int edge)
        {
            int twin = Twin(edge);
            List<int> fan = _outgoing[_edges[edge].To].Where(e => !_dead[e]).ToList();

            int index = fan.IndexOf(twin);
            if (index < 0 || fan.Count == 0)
            {
                return -1;
            }

            // One step clockwise from the way we came. Taking the sharpest turn
            // available is what makes the walk trace a single face rather than
            // cutting across the middle of one.
            return fan[(index - 1 + fan.Count) % fan.Count];
        }

        /// <summary>
        /// Turns a walk into a face, or refuses it.
        ///
        /// The outer face of any subdivision is traced clockwise and comes out
        /// negative; it is the space around the building rather than a room, and
        /// is dropped. Everything else is measured against real coordinates, so
        /// a walk that does not truly close is refused rather than reported with
        /// an area it does not have.
        /// </summary>
        internal PlanFace? Describe(List<int> walk, double tolerance)
        {
            var outline = new List<Point2D>(walk.Count);
            var segments = new List<BoundarySegment>(walk.Count);
            var walls = new List<RevitElementId>();
            var faces = new List<int>();
            var edgeFaces = new List<int>(walk.Count);
            bool touchesIgnored = false;

            foreach (int index in walk)
            {
                Edge edge = _edges[index];
                Point2D from = _vertices[edge.From];
                Point2D to = _vertices[edge.To];

                outline.Add(from);
                edgeFaces.Add(edge.Face);
                segments.Add(new BoundarySegment(
                    BoundaryCurve.Straight(from, to),
                    BoundaryReference.Host(edge.Wall)));

                if (!walls.Contains(edge.Wall))
                {
                    walls.Add(edge.Wall);
                }

                // Which faces drew this loop, so the caller can ask them
                // questions the loop itself cannot answer - how high they
                // reach, which way they look, whether any two of them face
                // each other across the space.
                if (edge.Face != NoFace && !faces.Contains(edge.Face))
                {
                    faces.Add(edge.Face);
                }

                touchesIgnored |= edge.Ignored;
            }

            if (Shoelace(outline) <= 0)
            {
                return null;
            }

            AreaMeasurement area = PlanArea.OfLoop(new BoundaryLoop(segments), tolerance);
            if (!area.IsMeasured)
            {
                return null;
            }

            return new PlanFace(outline, area, walls, touchesIgnored, faces, edgeFaces);
        }

        private static double Shoelace(List<Point2D> outline)
        {
            double twice = 0;

            for (int i = 0; i < outline.Count; i++)
            {
                Point2D a = outline[i];
                Point2D b = outline[(i + 1) % outline.Count];
                twice += (a.X * b.Y) - (b.X * a.Y);
            }

            return twice / 2.0;
        }
    }
}
