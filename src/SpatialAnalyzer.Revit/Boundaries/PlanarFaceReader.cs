using Autodesk.Revit.DB;
using SpatialAnalyzer.Core.Domain;
using SpatialAnalyzer.Core.Geometry;
using SpatialAnalyzer.Core.Spatial;

namespace SpatialAnalyzer.Revit.Boundaries;

/// <param name="Traces">
/// The lines the upright faces draw in plan. Each names the face it came from,
/// as an index into Faces.
/// </param>
/// <param name="FloorTopsInternalFeet">
/// The elevations of every upward facing floor face in the document, which is
/// where the storeys get cut.
/// </param>
/// <param name="FloorSoffitsInternalFeet">
/// The undersides of the same floors. A storey runs from the top of one slab
/// to the top of the next, but a space in it stops at the underside of the
/// slab above, and the difference is the thickness of a slab - which is
/// exactly the amount by which the masses looked too tall.
/// </param>
/// <param name="CurvedFacesPassedOver">
/// Faces that are not planar and so drew nothing. A cylindrical wall has one
/// face whose direction differs at every point of it, and there is no single
/// line it draws in plan. Counted rather than ignored, because a model full of
/// them would otherwise come back with rooms missing and no reason given.
/// </param>
/// <summary>
/// A room Revit already has, and where it stands.
/// </summary>
public sealed record PlacedRoom(Point2D At, double ZInternalFeet, string Name);

public sealed record FaceReading(
    IReadOnlyList<PlanTrace> Traces,
    IReadOnlyList<LoopFace> Faces,
    IReadOnlyList<double> FloorTopsInternalFeet,
    IReadOnlyList<double> FloorSoffitsInternalFeet,
    IReadOnlyList<PlacedRoom> RoomsPlaced,
    double LowestInternalFeet,
    double HighestInternalFeet,
    int ElementsRead,
    int CurvedFacesPassedOver,
    IReadOnlyList<string> Failures);

/// <summary>
/// Reads the flat faces of walls, curtain walls and curtain panels, and works
/// out the line each of them draws on the plan.
///
/// The difference between this and everything before it is which line. A wall
/// centre line is a drafting convenience: it runs down the middle of the wall,
/// so a room bounded by centre lines is half a wall too big on every side, and
/// a wall between two rooms has one centre line to offer both of them. A face
/// is the surface somebody paints. Bounding a room by faces gives the room the
/// area it actually has.
///
/// Planar faces rather than the triangles the exterior wall test uses, and the
/// reason is worth stating because the two look interchangeable and are not. A
/// triangulation of a rectangular wall face gives two triangles with a diagonal
/// between them, and that diagonal projects onto the plan as a line running
/// across the wall at an angle. It bounds nothing. Fed into an arrangement it
/// cuts the room it crosses into pieces. Triangles are right for asking which
/// way a face looks and wrong for asking where it is.
///
/// The cost is that curved faces draw nothing here, a cylinder having no one
/// direction. They are counted and reported.
/// </summary>
public static class PlanarFaceReader
{
    private static readonly Options HowToRead = new()
    {
        ComputeReferences = false,
        IncludeNonVisibleObjects = false,
        DetailLevel = ViewDetailLevel.Medium,
    };

    /// <summary>
    /// How nearly vertical a normal must point for its face to be a floor
    /// rather than a wall.
    /// </summary>
    private const double FlatEnough = 0.9;

    /// <summary>
    /// Reads the walls a view shows, and the floors of the whole document.
    ///
    /// Any view will do, and which one is chosen decides how much of the
    /// building is answered for. A plan shows one storey and gets one storey. A
    /// three dimensional view shows the whole model and gets the whole model,
    /// every floor of it at once, which is the point of cutting the height into
    /// storeys rather than trusting a level to say which walls are whose. A
    /// section box on that view narrows it again, so the operator can ask about
    /// a wing or a few floors without asking about all of them.
    ///
    /// The floors are read from the document either way, and not from the view.
    /// They are not in question: they are being asked how tall each storey is,
    /// and the floor that answers is the one above, which the plan of a level
    /// does not show. Reading floors from the view would put every ceiling back
    /// at the level datum, which is the height that was wrong.
    /// </summary>
    public static FaceReading Of(Document document, View view, double toleranceInternalFeet)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(view);

        var failures = new List<string>();
        var traces = new List<PlanTrace>();
        var faces = new List<LoopFace>();
        int curved = 0;

        double lowest = double.MaxValue;
        double highest = double.MinValue;

        List<Element> upright = InView(document, view, BuiltInCategory.OST_Walls)
            .Concat(InView(document, view, BuiltInCategory.OST_CurtainWallPanels))
            .ToList();

        foreach (Element element in upright)
        {
            try
            {
                Read(element, IsIgnoredForRooms(element), toleranceInternalFeet, traces, faces, ref curved);
            }
            catch (Exception exception)
            {
                failures.Add($"element {element.Id.Value}: {exception.GetType().Name}: {exception.Message}");
            }
        }

        foreach (LoopFace face in faces)
        {
            lowest = Math.Min(lowest, face.ZMinInternalFeet);
            highest = Math.Max(highest, face.ZMaxInternalFeet);
        }

        FloorFaces(document, failures, out List<double> tops, out List<double> soffits);

        return new FaceReading(
            traces,
            faces,
            tops,
            soffits,
            RoomsPlaced(document),
            lowest == double.MaxValue ? 0 : lowest,
            highest == double.MinValue ? 0 : highest,
            upright.Count,
            curved,
            failures);
    }

    /// <summary>
    /// Every room Revit has placed, anywhere in the document.
    ///
    /// Not used to find anything - the spaces are worked out from geometry and
    /// would be the same if the model had no rooms in it at all. These are the
    /// answer key. Where Revit has put a room, there is a space; if the spaces
    /// found here do not contain it, this is wrong about that part of the
    /// building, and saying so is worth more than any number of masses that
    /// look plausible in a view.
    /// </summary>
    private static IReadOnlyList<PlacedRoom> RoomsPlaced(Document document)
    {
        var placed = new List<PlacedRoom>();

        foreach (Element element in new FilteredElementCollector(document)
                     .OfCategory(BuiltInCategory.OST_Rooms)
                     .WhereElementIsNotElementType())
        {
            // A room with no area is unplaced and stands nowhere.
            if (element is not Autodesk.Revit.DB.Architecture.Room room ||
                room.Area <= 0 ||
                room.Location is not LocationPoint point)
            {
                continue;
            }

            placed.Add(new PlacedRoom(
                new Point2D(point.Point.X, point.Point.Y),
                point.Point.Z,
                room.Name ?? string.Empty));
        }

        return placed;
    }

    private static bool IsIgnoredForRooms(Element element) =>
        element is Wall wall &&
        wall.get_Parameter(BuiltInParameter.WALL_ATTR_ROOM_BOUNDING) is Parameter bounding &&
        bounding.AsInteger() == 0;

    private static IEnumerable<Element> InView(
        Document document,
        View view,
        BuiltInCategory category) =>
        new FilteredElementCollector(document, view.Id)
            .OfCategory(category)
            .WhereElementIsNotElementType();

    /// <summary>
    /// The elevation of every upward facing floor face in the document.
    ///
    /// The top of a slab rather than its level, because a level is a datum
    /// somebody named and can sit anywhere, while the top of a slab is the
    /// thing you stand on. A slab modelled with a step in it reports both
    /// elevations, which is correct: it has two.
    ///
    /// The undersides come back with them, because a storey is measured
    /// between slab tops and a space inside it stops at the slab above.
    /// </summary>
    private static void FloorFaces(
        Document document,
        List<string> failures,
        out List<double> tops,
        out List<double> soffits)
    {
        tops = new List<double>();
        soffits = new List<double>();

        foreach (Element floor in new FilteredElementCollector(document)
                     .OfCategory(BuiltInCategory.OST_Floors)
                     .WhereElementIsNotElementType())
        {
            try
            {
                foreach (Face found in FacesOf(floor))
                {
                    if (found is not PlanarFace planar)
                    {
                        continue;
                    }

                    if (planar.FaceNormal.Z > FlatEnough)
                    {
                        tops.Add(planar.Origin.Z);
                    }
                    else if (planar.FaceNormal.Z < -FlatEnough)
                    {
                        soffits.Add(planar.Origin.Z);
                    }
                }
            }
            catch (Exception exception)
            {
                failures.Add($"floor {floor.Id.Value}: {exception.GetType().Name}: {exception.Message}");
            }
        }
    }

    private static void Read(
        Element element,
        bool ignoredForRooms,
        double tolerance,
        List<PlanTrace> traces,
        List<LoopFace> faces,
        ref int curved)
    {
        var id = new RevitElementId(element.Id.Value);

        foreach (Face found in FacesOf(element))
        {
            if (found is not PlanarFace planar)
            {
                curved++;
                continue;
            }

            XYZ normal = planar.FaceNormal;
            if (!ExteriorFaces.IsUpright(normal.Z))
            {
                continue;
            }

            double flat = Math.Sqrt((normal.X * normal.X) + (normal.Y * normal.Y));
            if (flat <= 0)
            {
                continue;
            }

            List<(Point2D A, Point2D B)> drawn = Draw(planar, tolerance, out double zMin, out double zMax);
            if (drawn.Count == 0)
            {
                continue;
            }

            int index = faces.Count;

            faces.Add(new LoopFace(
                id,
                Middle(drawn),
                new Point2D(normal.X / flat, normal.Y / flat),
                zMin,
                zMax));

            foreach ((Point2D a, Point2D b) in drawn)
            {
                traces.Add(new PlanTrace(id, index, a, b, ignoredForRooms));
            }
        }
    }

    /// <summary>
    /// The lines one upright face draws in plan.
    ///
    /// An upright face is a piece of a vertical plane, so the whole of it
    /// projects onto a single line and every edge it has lands on that line.
    /// The top and bottom of a plain rectangular wall face therefore draw the
    /// same segment twice and the two vertical sides draw nothing at all, so
    /// what comes back is one segment. A face with a door cut out of it draws
    /// the full run along its top and the shorter runs either side of the
    /// opening; all are kept, because the arrangement splits them at their
    /// crossings anyway and the full run is the one that closes the room.
    /// </summary>
    private static List<(Point2D A, Point2D B)> Draw(
        PlanarFace face,
        double tolerance,
        out double zMin,
        out double zMax)
    {
        var drawn = new List<(Point2D, Point2D)>();
        var already = new HashSet<(long, long, long, long)>();

        zMin = double.MaxValue;
        zMax = double.MinValue;

        foreach (CurveLoop loop in face.GetEdgesAsCurveLoops())
        {
            foreach (Curve curve in loop)
            {
                IList<XYZ> along = curve.Tessellate();

                foreach (XYZ point in along)
                {
                    zMin = Math.Min(zMin, point.Z);
                    zMax = Math.Max(zMax, point.Z);
                }

                for (int i = 0; i < along.Count - 1; i++)
                {
                    var a = new Point2D(along[i].X, along[i].Y);
                    var b = new Point2D(along[i + 1].X, along[i + 1].Y);

                    // A vertical edge projects to a point and draws nothing.
                    if (a.DistanceTo(b) <= tolerance)
                    {
                        continue;
                    }

                    if (already.Add(Key(a, b, tolerance)))
                    {
                        drawn.Add((a, b));
                    }
                }
            }
        }

        if (zMin == double.MaxValue)
        {
            zMin = 0;
            zMax = 0;
        }

        return drawn;
    }

    /// <summary>
    /// Where a face is in plan: the middle of the line it draws, each piece of
    /// that line weighed by its length.
    ///
    /// Emphatically not the face's own Origin, which is where Revit chose to
    /// anchor the infinite plane the face lies in. That point need not be on
    /// the face, or anywhere near it. Asking whether one face lies the way
    /// another is looking is a question about two positions, and answering it
    /// with plane anchors answers about somewhere else entirely - which is why
    /// only twenty six of two hundred and sixty five rooms could be shown to
    /// have walls facing each other across them.
    /// </summary>
    private static Point2D Middle(List<(Point2D A, Point2D B)> drawn)
    {
        double x = 0;
        double y = 0;
        double measured = 0;

        foreach ((Point2D a, Point2D b) in drawn)
        {
            double length = a.DistanceTo(b);
            if (length <= 0)
            {
                continue;
            }

            x += (a.X + b.X) / 2.0 * length;
            y += (a.Y + b.Y) / 2.0 * length;
            measured += length;
        }

        return measured > 0 ? new Point2D(x / measured, y / measured) : drawn[0].A;
    }

    /// <summary>
    /// A segment named by its ends rounded to the tolerance, either way round,
    /// so the top and bottom edges of one wall face are recognised as the same
    /// line rather than laid one on top of the other.
    ///
    /// Rounding here is not closing a gap. Two edges of one planar face that
    /// round to the same pair of endpoints are the same line of the same face,
    /// which is as proven as a shared location gets; nothing is joined to
    /// anything it was not already part of.
    /// </summary>
    private static (long, long, long, long) Key(Point2D a, Point2D b, double tolerance)
    {
        double grid = tolerance > 0 ? tolerance : 1e-9;

        long ax = (long)Math.Round(a.X / grid);
        long ay = (long)Math.Round(a.Y / grid);
        long bx = (long)Math.Round(b.X / grid);
        long by = (long)Math.Round(b.Y / grid);

        return (ax < bx) || (ax == bx && ay <= by)
            ? (ax, ay, bx, by)
            : (bx, by, ax, ay);
    }

    private static IEnumerable<Face> FacesOf(Element element)
    {
        GeometryElement? geometry = element.get_Geometry(HowToRead);
        if (geometry is null)
        {
            yield break;
        }

        foreach (GeometryObject found in Walk(geometry))
        {
            if (found is Solid solid)
            {
                foreach (Face face in solid.Faces)
                {
                    yield return face;
                }
            }
        }
    }

    /// <summary>
    /// Whatever Revit hands back, flattened. A wall is usually a solid, but a
    /// stacked wall or a curtain wall arrives as an instance holding others,
    /// and missing those would drop entire facades.
    /// </summary>
    private static IEnumerable<GeometryObject> Walk(IEnumerable<GeometryObject> geometry)
    {
        foreach (GeometryObject found in geometry)
        {
            if (found is GeometryInstance instance)
            {
                foreach (GeometryObject inside in Walk(instance.GetInstanceGeometry()))
                {
                    yield return inside;
                }
            }
            else
            {
                yield return found;
            }
        }
    }
}
