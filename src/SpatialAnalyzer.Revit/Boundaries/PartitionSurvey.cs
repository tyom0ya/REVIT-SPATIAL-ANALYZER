using Autodesk.Revit.DB;
using SpatialAnalyzer.Core.Domain;
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
        IReadOnlyList<string> Failures);

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

        List<Wall> walls = new FilteredElementCollector(context.Document, context.View.Id)
            .OfCategory(BuiltInCategory.OST_Walls)
            .WhereElementIsNotElementType()
            .OfType<Wall>()
            .Where(IsIgnoredForRooms)
            .ToList();

        var partitions = new List<PartitionWall>();

        foreach (Wall wall in walls)
        {
            if (wall.Location is not LocationCurve location)
            {
                continue;
            }

            try
            {
                partitions.Add(new PartitionWall(
                    new RevitElementId(wall.Id.Value),
                    BoundaryExtractor.ConvertCurve(location.Curve)));
            }
            catch (Exception exception)
            {
                failures.Add($"wall {wall.Id.Value}: {exception.GetType().Name}: {exception.Message}");
            }
        }

        return new Result(
            walls.Count,
            PartitionLoops.Find(partitions, toleranceInternalFeet),
            failures);
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
