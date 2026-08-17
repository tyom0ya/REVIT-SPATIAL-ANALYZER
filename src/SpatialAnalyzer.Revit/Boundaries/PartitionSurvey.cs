using Autodesk.Revit.DB;
using SpatialAnalyzer.Core.Domain;
using SpatialAnalyzer.Core.Geometry;
using SpatialAnalyzer.Core.Spatial;
using SpatialAnalyzer.Revit.Context;

namespace SpatialAnalyzer.Revit.Boundaries;

/// <summary>
/// Finds the spaces enclosed by walls the model walks past when it works out
/// rooms.
///
/// A wall whose Room Bounding flag is off is invisible to Revit's plan
/// topology, so an apartment divided by such partitions comes back as one
/// region and the rooms inside it cannot be seen. On the acceptance model's
/// second floor forty-four of the walls are in that state, on the third floor
/// sixty-six, and nearly all of them sit inside model groups that Revit will
/// not let a tool modify from outside group edit mode.
///
/// An earlier attempt laid a room separation line along each such wall inside a
/// transaction that was rolled back, on the theory that Revit would then divide
/// the region for us. It does not. Measured three ways on the running model:
/// every line was created without complaint, in the right category, on the
/// right level, and the circuit count did not move - forty-four lines, thirty
/// regions before, thirty after. That approach is abandoned rather than tuned.
///
/// So the enclosures are worked out here instead, from the lines the walls
/// themselves draw. Nothing is written to the model: this reads wall centre
/// lines and hands them to the Core algorithm, which decides what encloses
/// something and what merely nearly does.
///
/// The distinction it will not blur is the one the whole project turns on. A
/// ring of walls that meets is an enclosure. A ring with a gap in it is a run
/// of walls with a gap in it, and the gap is reported at its measured size, at
/// any size, for a person to judge. Sixty millimetres is a doorway as often as
/// it is a drafting slip, and no tool can tell which from the number alone.
/// </summary>
public static class PartitionSurvey
{
    public sealed record Result(
        int WallsConsidered,
        PartitionArrangement Arrangement,
        PlanSubdivision Subdivision,
        IReadOnlyList<Point2D> WhereRoomsAlreadyAre,
        IReadOnlyList<string> Failures);

    /// <summary>
    /// The spaces the walls enclose that have no room in them.
    ///
    /// This was once "every face with an ignored wall on its boundary", which
    /// sounded right and was not. It missed spaces that Revit fails to report
    /// for reasons of its own, and it kept apartments that already had rooms
    /// merely because one of their walls happened to be ignored. Both were
    /// visible on the acceptance model the moment every face was drawn rather
    /// than only the flagged ones.
    ///
    /// Whether a wall is room bounding was never the question. The question is
    /// whether the building has a space here that the model does not account
    /// for, and that is answered by asking where the rooms already are.
    /// </summary>
    public static IReadOnlyList<PlanFace> HiddenBy(Result survey)
    {
        ArgumentNullException.ThrowIfNull(survey);

        return survey.Subdivision.Faces
            .Where(face => !survey.WhereRoomsAlreadyAre.Any(at => PlanFaces.Contains(face, at)))
            .ToList();
    }

    /// <summary>
    /// Reads every wall in this view that Revit is told not to treat as room
    /// bounding, and reports what they enclose.
    ///
    /// Read-only. No transaction is needed and none is opened.
    /// </summary>
    public static Result Of(AnalysisContext context, double toleranceInternalFeet)
    {
        ArgumentNullException.ThrowIfNull(context);

        var failures = new List<string>();

        List<Wall> everyWall = new FilteredElementCollector(context.Document, context.View.Id)
            .OfCategory(BuiltInCategory.OST_Walls)
            .WhereElementIsNotElementType()
            .OfType<Wall>()
            .ToList();

        var partitions = new List<PartitionWall>();
        var plan = new List<PlanWall>();
        int ignoredCount = 0;

        foreach (Wall wall in everyWall)
        {
            if (wall.Location is not LocationCurve location)
            {
                continue;
            }

            try
            {
                var id = new RevitElementId(wall.Id.Value);
                BoundaryCurve centre = BoundaryExtractor.ConvertCurve(location.Curve);
                bool ignored = IsIgnoredForRooms(wall);

                // Every wall goes to the subdivision, because a space is closed
                // by whatever happens to close it and the model's opinion of
                // which walls bound rooms is exactly what is in question.
                plan.Add(new PlanWall(id, centre, ignored));

                if (ignored)
                {
                    ignoredCount++;
                    partitions.Add(new PartitionWall(id, centre));
                }
            }
            catch (Exception exception)
            {
                failures.Add($"wall {wall.Id.Value}: {exception.GetType().Name}: {exception.Message}");
            }
        }

        return new Result(
            ignoredCount,
            PartitionLoops.Find(partitions, toleranceInternalFeet),
            PlanFaces.Find(plan, toleranceInternalFeet),
            WhereRoomsAre(context),
            failures);
    }

    /// <summary>
    /// Where the rooms on this level already stand.
    ///
    /// A room's own location point rather than anything computed, so a space
    /// counts as accounted for exactly when Revit itself has put a room in it.
    /// Rooms with no area are unplaced and stand nowhere.
    /// </summary>
    private static IReadOnlyList<Point2D> WhereRoomsAre(AnalysisContext context)
    {
        var at = new List<Point2D>();

        foreach (Element element in new FilteredElementCollector(context.Document)
                     .OfCategory(BuiltInCategory.OST_Rooms)
                     .WhereElementIsNotElementType())
        {
            if (element is not Autodesk.Revit.DB.Architecture.Room room ||
                room.LevelId != context.Level.Id ||
                room.Area <= 0 ||
                room.Location is not LocationPoint point)
            {
                continue;
            }

            at.Add(new Point2D(point.Point.X, point.Point.Y));
        }

        return at;
    }

    /// <summary>
    /// Whether Revit has been told to walk past this wall when working out
    /// rooms.
    /// </summary>
    private static bool IsIgnoredForRooms(Wall wall)
    {
        Parameter? flag = wall.get_Parameter(BuiltInParameter.WALL_ATTR_ROOM_BOUNDING);
        return flag is not null && flag.AsInteger() == 0;
    }
}
