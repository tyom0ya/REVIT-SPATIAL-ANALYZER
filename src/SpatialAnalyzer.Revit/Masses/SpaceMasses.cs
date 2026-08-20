using System.Globalization;
using Autodesk.Revit.DB;
using SpatialAnalyzer.Core.Geometry;
using SpatialAnalyzer.Core.Spatial;
using SpatialAnalyzer.Revit.Context;

namespace SpatialAnalyzer.Revit.Masses;

/// <summary>
/// Builds a solid for every space the plan encloses.
///
/// A room is a room on Revit's terms: it must be bounded by walls Revit
/// respects, it must have somewhere to stand, and it belongs to one level. A
/// good deal of what a building actually contains fails one of those and is
/// therefore invisible - the shaft with no door, the space divided by walls
/// inside a group, the riser running the height of the block.
///
/// A mass is bound by none of it. It is a solid where a space is, carrying what
/// is known about that space in its comments, and it can be made for anything
/// the geometry encloses whether or not Revit would call it a room.
///
/// Made as a DirectShape rather than a mass family, which keeps the geometry
/// arbitrary - the outlines here are frequently concave - and ships no family
/// file for someone to lose.
/// </summary>
public static class SpaceMasses
{
    /// <summary>
    /// Marks the masses this project made, so running twice does not make
    /// everything twice.
    ///
    /// Written into the element's name along with a key for the space it
    /// stands in. Running again reads the names back, matches them against the
    /// spaces found now, and leaves alone anything already standing. Without
    /// this a shaft would gain one more mass on every run, stacked inside the
    /// last.
    /// </summary>
    public const string Marker = "Spatial Analyzer Space";

    /// <summary>
    /// How coarsely a space's position is rounded to make its key, in feet.
    ///
    /// Coarse enough that the same space found twice keys the same way despite
    /// arithmetic that need not repeat exactly, fine enough that two different
    /// spaces cannot collide - a tenth of a foot is a little over an inch, and
    /// no two rooms share a centre that closely.
    /// </summary>
    private const double KeyPrecisionFeet = 0.1;

    public sealed record Result(
        int SpacesFound,
        int MassesMade,
        int AlreadyStanding,
        int Refused,
        IReadOnlyList<string> Failures);

    /// <summary>
    /// Must be called inside an open transaction that the caller commits.
    /// </summary>
    public static Result Build(AnalysisContext context, IReadOnlyList<PlanFace> spaces)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(spaces);

        Document document = context.Document;
        var failures = new List<string>();

        HashSet<string> standing = AlreadyMade(document);

        double floor = context.Level.Elevation;
        double levelAbove = NextLevelAbove(document, context.Level);

        // Everything horizontal that could stop a space going up. Gathered once
        // rather than per space, because a floor plan has a few hundred of them
        // and a few dozen spaces.
        List<BoundingBoxXYZ> lids = Lids(document);

        int made = 0;
        int skipped = 0;
        int refused = 0;

        foreach (PlanFace space in spaces)
        {
            string key = KeyOf(space);

            if (standing.Contains(key))
            {
                skipped++;
                continue;
            }

            try
            {
                if (Raise(context, space, floor, TopOf(space, floor, levelAbove, lids), key) is null)
                {
                    refused++;
                }
                else
                {
                    made++;
                }
            }
            catch (Exception exception)
            {
                refused++;
                failures.Add($"space {key}: {exception.GetType().Name}: {exception.Message}");
            }
        }

        return new Result(spaces.Count, made, skipped, refused, failures);
    }

    private static DirectShape? Raise(
        AnalysisContext context,
        PlanFace space,
        double floor,
        double ceiling,
        string key)
    {
        double height = ceiling - floor;
        if (height <= 0)
        {
            return null;
        }

        double shortest = context.Document.Application.ShortCurveTolerance;

        // Duplicate points are dropped before any curve is made, not curves
        // dropped after. A CurveLoop demands that each curve begin where the
        // last one ended, so skipping a segment because it was too short
        // leaves a hole and Revit rejects the entire loop - which is why every
        // space failed rather than only the ones with a repeated vertex, and a
        // tessellation repeats a point wherever one segment meets the next.
        var points = new List<XYZ>(space.Outline.Count);

        foreach (Point2D at in space.Outline)
        {
            var next = new XYZ(at.X, at.Y, floor);

            if (points.Count == 0 || points[^1].DistanceTo(next) > shortest)
            {
                points.Add(next);
            }
        }

        // The last point may also have closed back onto the first.
        while (points.Count > 1 && points[^1].DistanceTo(points[0]) <= shortest)
        {
            points.RemoveAt(points.Count - 1);
        }

        if (points.Count < 3)
        {
            return null;
        }

        var loop = new CurveLoop();

        for (int i = 0; i < points.Count; i++)
        {
            loop.Append(Line.CreateBound(points[i], points[(i + 1) % points.Count]));
        }

        Solid solid = GeometryCreationUtilities.CreateExtrusionGeometry(
            new List<CurveLoop> { loop },
            XYZ.BasisZ,
            height);

        DirectShape shape = DirectShape.CreateElement(
            context.Document,
            new ElementId(BuiltInCategory.OST_GenericModel));

        shape.SetShape(new List<GeometryObject> { solid });

        // Written to Comments rather than to the element's name. A DirectShape
        // does not keep a name set through the API - the masses from an earlier
        // run were standing in the model and read back as nameless, so every one
        // of them was built again. Comments is an ordinary instance parameter
        // and survives, and it is where the space's description is going to live
        // anyway.
        Mark(shape, key);

        return shape;
    }

    /// <summary>Writes this project's mark and the space's key into Comments.</summary>
    private static void Mark(Element shape, string key)
    {
        Parameter? comments = shape.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);
        comments?.Set($"{Marker} {key}");
    }

    /// <summary>
    /// Everything horizontal that could form the top of a space: slabs,
    /// ceilings and roofs.
    /// </summary>
    private static List<BoundingBoxXYZ> Lids(Document document)
    {
        var lids = new List<BoundingBoxXYZ>();

        foreach (BuiltInCategory category in new[]
                 {
                     BuiltInCategory.OST_Ceilings,
                     BuiltInCategory.OST_Floors,
                     BuiltInCategory.OST_Roofs,
                 })
        {
            foreach (Element element in new FilteredElementCollector(document)
                         .OfCategory(category)
                         .WhereElementIsNotElementType())
            {
                BoundingBoxXYZ? box = element.get_BoundingBox(null);
                if (box is not null)
                {
                    lids.Add(box);
                }
            }
        }

        return lids;
    }

    /// <summary>
    /// How high a space reaches: the underside of the lowest thing above it.
    ///
    /// A ceiling stops a room and a slab stops a shaft. Using the level above
    /// for both gives a room the height of the structure rather than the height
    /// of the room, which was the complaint and rightly.
    ///
    /// Bounding boxes rather than a cast ray. A ray needs a three dimensional
    /// view to fire in and this runs from a plan; the box is coarser but it is
    /// right about which slab is lowest, and that is the only thing being asked.
    /// </summary>
    private static double TopOf(
        PlanFace space,
        double floor,
        double levelAbove,
        List<BoundingBoxXYZ> lids)
    {
        if (!PlanFaces.TryFindPointInside(space, out Point2D inside))
        {
            return levelAbove;
        }

        // Clear of the floor itself, so the slab this space stands on is not
        // mistaken for the one it stops at.
        double above = floor + AboveTheFloorFeet;
        double lowest = levelAbove;

        foreach (BoundingBoxXYZ box in lids)
        {
            if (box.Min.Z < above || box.Min.Z >= lowest)
            {
                continue;
            }

            if (inside.X < box.Min.X || inside.X > box.Max.X ||
                inside.Y < box.Min.Y || inside.Y > box.Max.Y)
            {
                continue;
            }

            lowest = box.Min.Z;
        }

        return lowest;
    }

    /// <summary>
    /// How far above the floor a lid must be to count as one, so the slab
    /// underfoot and its finishes are not read as the ceiling.
    /// </summary>
    private const double AboveTheFloorFeet = 1.0;

    /// <summary>
    /// The keys of the masses already standing in this document.
    /// </summary>
    private static HashSet<string> AlreadyMade(Document document)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);

        foreach (Element element in new FilteredElementCollector(document)
                     .OfClass(typeof(DirectShape)))
        {
            string mark = element
                .get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?
                .AsString() ?? string.Empty;

            if (mark.StartsWith(Marker, StringComparison.Ordinal))
            {
                keys.Add(mark[Marker.Length..].Trim());
            }
        }

        return keys;
    }

    /// <summary>
    /// A stable name for a space, from where it is rather than what order it
    /// was found in.
    ///
    /// Ordinals will not do: a space's index shifts the moment a wall is added
    /// somewhere earlier on the plan, and every mass would then be a stranger
    /// to the space it stands in.
    /// </summary>
    private static string KeyOf(PlanFace space)
    {
        double x = 0;
        double y = 0;

        foreach (Point2D at in space.Outline)
        {
            x += at.X;
            y += at.Y;
        }

        x /= space.Outline.Count;
        y /= space.Outline.Count;

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{Math.Round(x / KeyPrecisionFeet):0}:{Math.Round(y / KeyPrecisionFeet):0}");
    }

    /// <summary>
    /// The level above this one, or a storey's worth above it when there is
    /// none.
    ///
    /// The fallback is a guess and is treated as one by the caller, which says
    /// so rather than presenting an invented height as measured.
    /// </summary>
    private static double NextLevelAbove(Document document, Level level)
    {
        double nearest = double.MaxValue;

        foreach (Element element in new FilteredElementCollector(document)
                     .OfCategory(BuiltInCategory.OST_Levels)
                     .WhereElementIsNotElementType())
        {
            if (element is Level other &&
                other.Elevation > level.Elevation &&
                other.Elevation < nearest)
            {
                nearest = other.Elevation;
            }
        }

        return nearest < double.MaxValue ? nearest : level.Elevation + StoreyGuessFeet;
    }

    /// <summary>
    /// Used only when the level being analysed is the topmost one, so nothing
    /// in the model says how tall the space is.
    /// </summary>
    private const double StoreyGuessFeet = 10.0;

    /// <summary>Whether the top of these masses was read from the model or guessed.</summary>
    public static bool TopWasMeasured(Document document, Level level) =>
        NextLevelAbove(document, level) < level.Elevation + StoreyGuessFeet ||
        new FilteredElementCollector(document)
            .OfCategory(BuiltInCategory.OST_Levels)
            .WhereElementIsNotElementType()
            .OfType<Level>()
            .Any(l => l.Elevation > level.Elevation);
}
