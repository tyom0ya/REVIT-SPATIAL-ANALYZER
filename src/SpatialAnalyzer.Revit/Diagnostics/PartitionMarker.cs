using System.Globalization;
using Autodesk.Revit.DB;
using SpatialAnalyzer.Core.Geometry;
using SpatialAnalyzer.Core.Spatial;
using SpatialAnalyzer.Revit.Boundaries;
using SpatialAnalyzer.Revit.Context;

namespace SpatialAnalyzer.Revit.Diagnostics;

/// <summary>
/// Puts the walls the model ignores for rooms onto the plan, and draws each
/// place where a run of them stops short.
///
/// The analysis reports sixty-six such walls on the acceptance model's third
/// floor and thirty-three runs that enclose nothing, the smallest missing by
/// three hundred and eighty-four millimetres. Those numbers cannot settle the
/// question they raise. A gap of that size is a doorway, or it is where a
/// partition meets a wall the analysis was never given - and the two mean
/// opposite things. The first says the model is right and there are no rooms
/// hidden here. The second says the search was looking at too small a set of
/// walls.
///
/// Nothing in the geometry distinguishes them. A person glancing at the plan
/// distinguishes them immediately, which is why this draws rather than counts.
///
/// The walls are recoloured rather than redrawn - Revit is told to show
/// existing elements differently in this view - and the gaps are detail lines
/// on a style of their own, so both come away in one undo.
/// </summary>
public static class PartitionMarker
{
    public const string OpenStyleName = "Spatial Analyzer - Partition Open End";
    public const string ClosedStyleName = "Spatial Analyzer - Partition Closed End";

    /// <summary>
    /// How long a tick to draw where an end sits exactly on a wall. Revit
    /// refuses a zero-length line, and the finding still has to be visible.
    /// </summary>
    private const double ShortestTickFeet = 0.25;

    /// <summary>Orange for the walls, so they read against a plan's own greys.</summary>
    private static readonly Color WallColour = new(255, 140, 0);

    /// <summary>Red where a loose end stands in open air.</summary>
    private static readonly Color OpenColour = new(220, 30, 30);

    /// <summary>Green where a loose end stops against a wall not in the search.</summary>
    private static readonly Color ClosedColour = new(0, 170, 60);

    private const int WallWeight = 6;
    private const int GapWeight = 7;

    public sealed record Result(
        int WallsMarked,
        int EndsInOpenAir,
        int EndsMeetingAnotherWall,
        double SmallestOpenGapInternalFeet,
        IReadOnlyList<string> Failures);

    /// <summary>
    /// Must be called inside an open transaction that the caller commits, so
    /// that recolouring and drawing arrive as one undoable step.
    /// </summary>
    public static Result Apply(AnalysisContext context, PartitionSurvey.Result survey)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(survey);

        Document document = context.Document;
        var failures = new List<string>();

        GraphicsStyle? openStyle = GapLineStyle(document, OpenStyleName, OpenColour, failures);
        GraphicsStyle? closedStyle = GapLineStyle(document, ClosedStyleName, ClosedColour, failures);

        OverrideGraphicSettings wallOverride = new OverrideGraphicSettings()
            .SetProjectionLineColor(WallColour)
            .SetProjectionLineWeight(WallWeight);

        int marked = MarkWalls(context, survey, wallOverride, failures);

        // Every wall in the view, not only the ones the search was given. That
        // is the whole point: a partition's loose end is either in open air or
        // it stops against a wall that was filtered out, and only the full set
        // can tell which.
        List<Curve> allWalls = AllWallCurves(context);

        double elevation = context.Level.Elevation;
        double shortest = document.Application.ShortCurveTolerance;
        var ignored = new HashSet<long>(
            survey.Arrangement.OpenChains.SelectMany(c => c.Walls).Select(w => w.Id.Value));

        int open = 0;
        int closed = 0;
        double smallestOpen = double.PositiveInfinity;

        foreach (Point2D end in survey.Arrangement.OpenChains.SelectMany(c => c.FreeEnds))
        {
            var at = new XYZ(end.X, end.Y, elevation);

            (double reach, XYZ? nearest) = NearestWallTo(at, allWalls, elevation);
            if (nearest is null)
            {
                continue;
            }

            // Touching within Revit's own threshold for geometry it cannot tell
            // apart. Not a gap being closed - a measurement being reported as
            // what it is, with the number written on the drawing either way.
            bool meetsAWall = reach <= shortest;

            if (meetsAWall)
            {
                closed++;
            }
            else
            {
                open++;
                smallestOpen = Math.Min(smallestOpen, reach);
            }

            try
            {
                // A zero-length line cannot be drawn, so an end sitting exactly
                // on a wall is shown by a short tick instead of a reach line.
                XYZ to = meetsAWall ? at + new XYZ(0, ShortestTickFeet, 0) : nearest;

                DetailCurve line = document.Create.NewDetailCurve(context.View, Line.CreateBound(at, to));

                GraphicsStyle? style = meetsAWall ? closedStyle : openStyle;
                if (style is not null)
                {
                    line.LineStyle = style;
                }
            }
            catch (Exception exception)
            {
                failures.Add($"end: {exception.GetType().Name}: {exception.Message}");
                continue;
            }

            // Only the ends that stand open get a number. An end that meets a
            // wall measures zero by definition, and forty-three labels reading
            // "0 mm" bury the drawing without adding to it - the green tick
            // already says everything that measurement found.
            if (!meetsAWall)
            {
                Label(context, at, reach, failures);
            }
        }

        return new Result(
            marked,
            open,
            closed,
            double.IsInfinity(smallestOpen) ? 0 : smallestOpen,
            failures);
    }

    /// <summary>
    /// The nearest point on any wall to a loose end, and how far away it is.
    /// </summary>
    private static (double Distance, XYZ? Point) NearestWallTo(XYZ at, List<Curve> walls, double elevation)
    {
        double nearest = double.MaxValue;
        XYZ? point = null;

        foreach (Curve wall in walls)
        {
            IntersectionResult? projection = wall.Project(at);
            if (projection is null || projection.Distance >= nearest)
            {
                continue;
            }

            nearest = projection.Distance;
            point = new XYZ(projection.XYZPoint.X, projection.XYZPoint.Y, elevation);
        }

        return point is null ? (0, null) : (nearest, point);
    }

    private static List<Curve> AllWallCurves(AnalysisContext context)
    {
        var curves = new List<Curve>();

        foreach (Wall wall in new FilteredElementCollector(context.Document, context.View.Id)
                     .OfCategory(BuiltInCategory.OST_Walls)
                     .WhereElementIsNotElementType()
                     .OfType<Wall>())
        {
            if (wall.Location is LocationCurve location)
            {
                curves.Add(location.Curve);
            }
        }

        return curves;
    }

    private static int MarkWalls(
        AnalysisContext context,
        PartitionSurvey.Result survey,
        OverrideGraphicSettings settings,
        List<string> failures)
    {
        // Every wall the survey considered, whether or not it took part in a
        // run. A wall that belongs to no run is itself worth seeing: it is a
        // partition the analysis has nothing to say about.
        var ids = survey.Arrangement.OpenChains
            .SelectMany(c => c.Walls)
            .Concat(survey.Arrangement.ClosedLoops.SelectMany(l => l.Walls))
            .Select(w => w.Id.Value)
            .Concat(survey.Arrangement.Tangled.Select(w => w.Id.Value))
            .Distinct()
            .ToList();

        int marked = 0;

        foreach (long id in ids)
        {
            try
            {
                context.View.SetElementOverrides(new ElementId(id), settings);
                marked++;
            }
            catch (Exception exception)
            {
                failures.Add($"wall {id}: {exception.GetType().Name}: {exception.Message}");
            }
        }

        return marked;
    }

    /// <summary>
    /// Writes the gap's size beside it, in millimetres.
    ///
    /// Millimetres because the question being asked is whether a person fits
    /// through, and nobody thinks about that in decimal feet.
    /// </summary>
    private static void Label(AnalysisContext context, XYZ at, double reachFeet, List<string> failures)
    {
        try
        {
            ElementId typeId = context.Document.GetDefaultElementTypeId(ElementTypeGroup.TextNoteType);
            if (typeId == ElementId.InvalidElementId)
            {
                return;
            }

            TextNote.Create(
                context.Document,
                context.View.Id,
                at,
                string.Create(CultureInfo.InvariantCulture, $"{reachFeet * 304.8:0} mm"),
                typeId);
        }
        catch (Exception exception)
        {
            // The line is the point; the number beside it is a convenience. A
            // model with no usable text type still gets the drawing.
            failures.Add($"end label: {exception.GetType().Name}: {exception.Message}");
        }
    }

    private static GraphicsStyle? GapLineStyle(
        Document document,
        string name,
        Color colour,
        List<string> failures)
    {
        try
        {
            Category lines = document.Settings.Categories.get_Item(BuiltInCategory.OST_Lines);

            Category style = lines.SubCategories.Contains(name)
                ? lines.SubCategories.get_Item(name)
                : document.Settings.Categories.NewSubcategory(lines, name);

            style.LineColor = colour;
            style.SetLineWeight(GapWeight, GraphicsStyleType.Projection);

            return style.GetGraphicsStyle(GraphicsStyleType.Projection);
        }
        catch (Exception exception)
        {
            failures.Add($"could not prepare the {name} line style ({exception.GetType().Name}: {exception.Message}); "
                       + "ends will be drawn in the current style and may be hard to see");
            return null;
        }
    }
}
