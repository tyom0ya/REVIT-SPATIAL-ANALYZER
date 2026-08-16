using SpatialAnalyzer.Core.Domain;
using SpatialAnalyzer.Core.Geometry;

namespace SpatialAnalyzer.Core.Spatial;

/// <summary>
/// One wall as the plan sees it, and whether the model bounds rooms with it.
/// </summary>
public sealed record PlanWall(RevitElementId Id, BoundaryCurve CentreLine, bool IgnoredForRooms);

/// <summary>
/// A space enclosed by walls, however the model feels about them.
/// </summary>
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
    bool TouchesAWallIgnoredForRooms);

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

    public static PlanSubdivision Find(IReadOnlyList<PlanWall> walls, double toleranceInternalFeet)
    {
        ArgumentNullException.ThrowIfNull(walls);

        if (double.IsNaN(toleranceInternalFeet) || toleranceInternalFeet < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(toleranceInternalFeet),
                toleranceInternalFeet,
                "Tolerance must be a number and cannot be negative.");
        }

        List<Piece> pieces = SplitWhereTheyCross(Flatten(walls), toleranceInternalFeet);

        if (pieces.Count == 0)
        {
            return new PlanSubdivision(
                Array.Empty<PlanFace>(),
                walls.Count,
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
            walls.Count,
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
    private sealed record Piece(Point2D A, Point2D B, RevitElementId Wall, bool Ignored);

    /// <summary>
    /// Reduces every wall to straight pieces.
    ///
    /// An arc arrives as the tessellation Revit itself drew, so a curved wall
    /// is followed rather than straightened, and no curve mathematics is
    /// reimplemented here.
    /// </summary>
    private static List<Piece> Flatten(IReadOnlyList<PlanWall> walls)
    {
        var pieces = new List<Piece>();

        foreach (PlanWall wall in walls)
        {
            IReadOnlyList<Point2D> points = wall.CentreLine.Tessellation;

            for (int i = 0; i < points.Count - 1; i++)
            {
                if (points[i] != points[i + 1])
                {
                    pieces.Add(new Piece(points[i], points[i + 1], wall.Id, wall.IgnoredForRooms));
                }
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

        for (int i = 0; i < pieces.Count; i++)
        {
            Piece piece = pieces[i];

            var cuts = new List<double> { 0.0, 1.0 };

            for (int j = 0; j < pieces.Count; j++)
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
        private readonly bool[] _dead;

        internal Graph(List<Piece> pieces, double tolerance)
        {
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

        private sealed record Edge(int From, int To, double Angle, RevitElementId Wall, bool Ignored);

        private void Add(int from, int to, Piece piece)
        {
            _edges.Add(new Edge(
                from,
                to,
                Math.Atan2(_vertices[to].Y - _vertices[from].Y, _vertices[to].X - _vertices[from].X),
                piece.Wall,
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
            for (int i = 0; i < _vertices.Count; i++)
            {
                if (_vertices[i].DistanceTo(point) <= tolerance)
                {
                    return i;
                }
            }

            _vertices.Add(point);
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
            bool removedSomething = true;

            while (removedSomething)
            {
                removedSomething = false;

                foreach ((int vertex, List<int> fan) in _outgoing)
                {
                    List<int> alive = fan.Where(e => !_dead[e]).ToList();
                    if (alive.Count != 1)
                    {
                        continue;
                    }

                    _dead[alive[0]] = true;
                    _dead[Twin(alive[0])] = true;
                    removedSomething = true;
                    _ = vertex;
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
            bool touchesIgnored = false;

            foreach (int index in walk)
            {
                Edge edge = _edges[index];
                Point2D from = _vertices[edge.From];
                Point2D to = _vertices[edge.To];

                outline.Add(from);
                segments.Add(new BoundarySegment(
                    BoundaryCurve.Straight(from, to),
                    BoundaryReference.Host(edge.Wall)));

                if (!walls.Contains(edge.Wall))
                {
                    walls.Add(edge.Wall);
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

            return new PlanFace(outline, area, walls, touchesIgnored);
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
