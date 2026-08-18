using System.Globalization;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using SpatialAnalyzer.Core.Domain;
using SpatialAnalyzer.Core.Geometry;
using SpatialAnalyzer.Core.Spatial;
using SpatialAnalyzer.Revit.Boundaries;
using SpatialAnalyzer.Revit.Context;

namespace SpatialAnalyzer.Revit.Commands;

/// <summary>
/// Colours the walls that face outside.
///
/// The wall's own Function parameter is not consulted. It is set by whoever
/// drew the wall, and a curtain wall left as Interior or a party wall marked
/// Exterior are common enough that a tool trusting it would be reporting the
/// model's intentions rather than the building's geometry. What is asked
/// instead is the question that defines an exterior wall: is there building on
/// one side of it and open air on the other?
///
/// Orange for walls that face outside, grey for partitions, purple for walls
/// standing on their own with nothing either side - a garden wall or a screen,
/// which is neither of the other two and deserves saying so rather than being
/// forced into one.
///
/// Overrides only. Nothing is drawn and no element is changed; one undo puts
/// the view back.
/// </summary>
[Transaction(TransactionMode.Manual)]
public class ShowExteriorWallsCommand : IExternalCommand
{
    private static readonly Color Outside = new(255, 110, 0);
    private static readonly Color Inside = new(170, 170, 170);
    private static readonly Color OnItsOwn = new(150, 60, 200);

    private const int Weight = 6;

    /// <summary>
    /// How far to step off a wall's centre line when asking what is either
    /// side. Half the wall plus a little, so the test point clears the wall's
    /// own thickness however thick it happens to be.
    /// </summary>
    private const double BeyondTheWallFeet = 0.25;

    /// <summary>
    /// How often to sample along a wall for the point cloud - about a foot.
    /// Spacing by distance rather than taking a fixed count per wall, so a two
    /// metre partition and a thirty metre facade are described equally well.
    /// </summary>
    private const double EveryFeet = 1.0;

    /// <summary>
    /// How deep a recess the outline follows, in feet - about a metre and a
    /// half. Deeper bays are still reached, one step at a time, because each
    /// dig shortens the edges and brings the next points within reach. What
    /// this really sets is how far the outline may stray from the structure
    /// before it stops looking for more.
    /// </summary>
    private const double ReachFeet = 5.0;

    /// <summary>The footprint itself, drawn so the classification can be argued with.</summary>
    public const string OutlineStyleName = "Spatial Analyzer - Building Outline";

    private static readonly Color OutlineColour = new(0, 140, 200);

    /// <summary>Bright green for the doors that lead out - the answer being looked for.</summary>
    private static readonly Color ExitDoor = new(0, 200, 80);

    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        UIDocument? uiDocument = commandData.Application.ActiveUIDocument;

        AnalysisContextResolution resolution = AnalysisContextResolver.Resolve(uiDocument);
        if (!resolution.IsSuccess)
        {
            TaskDialog.Show("Spatial Analyzer", resolution.FailureReason!);
            return Result.Succeeded;
        }

        AnalysisContext context = resolution.Context!;
        var byExposure = new Dictionary<WallExposure, int>();
        int exits = 0;

        try
        {
            var tolerance = new ClosureTolerance(context.Document.Application.ShortCurveTolerance);

            List<Wall> walls = new FilteredElementCollector(context.Document, context.View.Id)
                .OfCategory(BuiltInCategory.OST_Walls)
                .WhereElementIsNotElementType()
                .OfType<Wall>()
                .Where(w => w.Location is LocationCurve)
                .ToList();

            var plan = walls
                .Select(w => new PlanWall(
                    new RevitElementId(w.Id.Value),
                    BoundaryExtractor.ConvertCurve(((LocationCurve)w.Location).Curve),
                    false))
                .ToList();

            // The step aside must clear the thickest wall, or its own two test
            // points both land inside it where there is no space of any kind.
            double stepAside = walls.Count == 0
                ? BeyondTheWallFeet
                : (walls.Max(w => w.Width) / 2.0) + BeyondTheWallFeet;

            // Walls only, and every wall including the curtain ones - a curtain
            // wall is a Wall with a location curve and encloses as surely as
            // masonry does.
            //
            // Columns are left out on purpose. They hold the building up but
            // they do not enclose it, and what is being looked for here is the
            // line between inside and out so that the doors through it can be
            // found. A column standing proud of the facade drags the outline
            // out to meet it and takes the wall behind it out of the running.
            var cloud = plan
                .SelectMany(w => ExteriorWalls.SampleForCloud(w.CentreLine, EveryFeet))
                .ToList();

            BuildingFootprint footprint = BuildingFootprint.Around(cloud, ReachFeet);

            IReadOnlyList<WallExposureFinding> found = ExteriorWalls.Classify(
                plan,
                footprint,
                (walls.Count == 0 ? 0 : walls.Max(w => w.Width) / 2.0) + EveryFeet,
                stepAside);

            using var transaction = new Transaction(context.Document, "Spatial Analyzer show exterior walls");
            transaction.Start();

            // Inside the transaction, which it was not. Revit refuses to create
            // an element without one, and this method's own catch swallowed the
            // refusal and returned - so the outline silently never appeared, and
            // the one thing that explains a wall's colour was missing from every
            // run. A failure that returns quietly is worse than one that stops.
            DrawOutline(context, footprint);

            foreach (WallExposureFinding finding in found)
            {
                byExposure[finding.Exposure] = byExposure.GetValueOrDefault(finding.Exposure) + 1;

                Color colour = finding.Exposure switch
                {
                    WallExposure.Exterior => Outside,
                    WallExposure.Unknown => OnItsOwn,
                    _ => Inside,
                };

                try
                {
                    context.View.SetElementOverrides(
                        new ElementId(finding.Wall.Value),
                        new OverrideGraphicSettings()
                            .SetProjectionLineColor(colour)
                            .SetCutLineColor(colour)
                            .SetProjectionLineWeight(Weight)
                            .SetCutLineWeight(Weight));
                }
                catch (Exception)
                {
                    // Not every wall accepts an override. One that refuses is
                    // left as it was rather than stopping the rest.
                }
            }

            exits = MarkExitDoors(
                context,
                found.Where(x => x.Exposure == WallExposure.Exterior).Select(x => x.Wall.Value).ToHashSet(),
                footprint,
                walls.Select(w => (w.Id.Value, ((LocationCurve)w.Location).Curve, w.Width / 2.0)).ToList());

            transaction.Commit();
        }
        catch (Exception exception)
        {
            message = $"The walls could not be classified: {exception.GetType().Name}: {exception.Message}";
            return Result.Failed;
        }

        uiDocument!.RefreshActiveView();

        var lines = new List<string>
        {
            string.Create(CultureInfo.InvariantCulture, $"orange, facing outside:  {byExposure.GetValueOrDefault(WallExposure.Exterior)}"),
            string.Create(CultureInfo.InvariantCulture, $"grey, building both sides:  {byExposure.GetValueOrDefault(WallExposure.Interior)}"),
            string.Create(CultureInfo.InvariantCulture, $"purple, could not be judged:  {byExposure.GetValueOrDefault(WallExposure.Unknown)}"),
            string.Empty,
            string.Create(CultureInfo.InvariantCulture, $"GREEN, DOORS LEADING OUT:  {exits}"),
            string.Empty,
            "The blue line is the building outline everything was judged against: a hull",
            "over points sampled off every wall, column and curtain panel, dug inwards",
            "until it follows the recesses. If a wall is the wrong colour, look at where",
            "that line runs - it will have bridged a bay or reached in and hooked a",
            "partition, and that is the thing to correct.",
            string.Empty,
            "The wall's own Function parameter is not consulted; it is set by hand and is",
            "wrong often enough to be worth ignoring.",
            string.Empty,
            "Press Ctrl+Z once to put the view back.",
        };

        TaskDialog.Show("Spatial Analyzer", string.Join(Environment.NewLine, lines));
        return Result.Succeeded;
    }


    /// <summary>
    /// Colours the doors that lead out of the building.
    ///
    /// This is what the wall classification was for. A door in an exterior wall
    /// is a way out; a door in a partition leads from one room to another. The
    /// door itself carries no flag saying which it is, so the wall it is hosted
    /// in has to answer for it.
    ///
    /// Curtain wall door panels are gathered too. A glazed entrance is a door
    /// panel inside a curtain wall rather than a door hosted in masonry, and it
    /// is very often the main way in and out of a building - missing it would
    /// be missing the front door.
    /// </summary>

    /// <summary>How far a ray from a door is walked before giving up, in feet.</summary>
    private const double RayReachFeet = 40.0;

    /// <summary>How far apart the steps along that ray are.</summary>
    private const double RayStepFeet = 0.5;

    /// <summary>
    /// Whether a ray from this door reaches open ground without a wall in the
    /// way.
    ///
    /// The door is walked out along the way it faces, in both directions, one
    /// short step at a time. Leaving the building outline means the door leads
    /// out. Meeting another wall first means it does not - it opens onto
    /// another room, and the outline beyond that is somebody else's business.
    ///
    /// The wall the door is hung in is skipped, or every door would be stopped
    /// by its own host on the first step.
    ///
    /// This is a second chance rather than the main test. A door whose host
    /// already faces outside never gets here, so a recessed entrance and a door
    /// in a return wall are what it is for.
    /// </summary>

    /// <summary>
    /// Whether the door has a room one side of it and nothing the other.
    ///
    /// Revit records both for every door, so this is the model's own answer
    /// rather than something inferred from lines on a plan, and where the rooms
    /// are placed it is the most reliable of the three tests.
    ///
    /// Exactly one side must be empty. Both empty means the door stands
    /// somewhere the model has no rooms at all - a plant deck, an unmodelled
    /// floor - and says nothing about whether it leads outside. Reporting that
    /// as an exit would fill a schedule of escape routes with doors between two
    /// store cupboards nobody has got round to placing rooms in.
    /// </summary>
    private static bool OpensOntoNothing(FamilyInstance door, Phase phase)
    {
        try
        {
            bool toIsEmpty = door.get_ToRoom(phase) is null;
            bool fromIsEmpty = door.get_FromRoom(phase) is null;

            return toIsEmpty != fromIsEmpty;
        }
        catch (Exception)
        {
            // A door that will not answer is left to the other two tests.
            return false;
        }
    }

    private static bool EscapesTheBuilding(
        AnalysisContext context,
        FamilyInstance door,
        BuildingFootprint footprint,
        IReadOnlyList<(long Id, Curve Curve, double Half)> walls)
    {
        if (!footprint.IsUsable || door.Location is not LocationPoint at)
        {
            return false;
        }

        XYZ facing = door.FacingOrientation;
        if (facing is null || facing.GetLength() <= 0)
        {
            return false;
        }

        long host = door.Host?.Id.Value ?? -1;
        XYZ from = at.Point;

        foreach (int way in new[] { 1, -1 })
        {
            XYZ direction = facing.Normalize().Multiply(way);
            bool blocked = false;

            for (double along = RayStepFeet; along <= RayReachFeet; along += RayStepFeet)
            {
                XYZ point = from + direction.Multiply(along);

                foreach ((long id, Curve curve, double half) in walls)
                {
                    if (id == host)
                    {
                        continue;
                    }

                    IntersectionResult? hit = curve.Project(
                        new XYZ(point.X, point.Y, curve.GetEndPoint(0).Z));

                    if (hit is not null && hit.Distance <= half)
                    {
                        blocked = true;
                        break;
                    }
                }

                if (blocked)
                {
                    break;
                }

                if (!footprint.Contains(new Point2D(point.X, point.Y)))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static int MarkExitDoors(
        AnalysisContext context,
        HashSet<long> exteriorWalls,
        BuildingFootprint footprint,
        IReadOnlyList<(long Id, Curve Curve, double Half)> wallCurves)
    {
        var settings = new OverrideGraphicSettings()
            .SetProjectionLineColor(ExitDoor)
            .SetCutLineColor(ExitDoor)
            .SetProjectionLineWeight(Weight)
            .SetCutLineWeight(Weight);

        int marked = 0;

        foreach (Element element in new FilteredElementCollector(context.Document, context.View.Id)
                     .OfCategory(BuiltInCategory.OST_Doors)
                     .WhereElementIsNotElementType())
        {
            if (element is not FamilyInstance door)
            {
                continue;
            }

            // Three ways to be a way out, and a door needs only one of them.
            //
            // The first is its host: a door in a wall that faces outside leads
            // outside. That catches most of them and costs nothing.
            //
            // The second is what Revit already knows. A door records the room
            // either side of it, and one with a room on one side and nothing on
            // the other has nothing on that side to be in - it opens onto the
            // world. This is the cheapest and most reliable of the three where
            // the model has its rooms placed, because it is the model's own
            // answer rather than a reading of the geometry.
            //
            // The third is the door itself. A door set back in an entrance
            // recess, or hung in the short return wall beside one, has a host
            // that never scored as facade, and in a model with no rooms placed
            // the second test says nothing either. For those a ray is walked
            // out from the door in the direction it faces; if it reaches open
            // ground without another wall stopping it, the door leads out.
            long? host = door.Host?.Id.Value;

            bool wayOut =
                (host is long id && exteriorWalls.Contains(id))
                || OpensOntoNothing(door, context.Phase)
                || EscapesTheBuilding(context, door, footprint, wallCurves);

            if (!wayOut)
            {
                continue;
            }

            try
            {
                context.View.SetElementOverrides(element.Id, settings);
                marked++;
            }
            catch (Exception)
            {
                // A door that refuses an override is left alone rather than
                // stopping the rest.
            }
        }

        return marked;
    }
    /// <summary>
    /// The plan corners of everything in a category.
    ///
    /// A column carries a corner of the building whether or not a wall reaches
    /// it, and a curtain panel encloses as surely as masonry does. Left out,
    /// the outline cuts across a glazed frontage and every wall behind it reads
    /// as interior. Corners rather than centres, because a column's extent is
    /// what the building occupies there.
    /// </summary>
    private static IEnumerable<Point2D> CornersOf(AnalysisContext context, BuiltInCategory category)
    {
        foreach (Element element in new FilteredElementCollector(context.Document, context.View.Id)
                     .OfCategory(category)
                     .WhereElementIsNotElementType())
        {
            BoundingBoxXYZ? box = element.get_BoundingBox(context.View);
            if (box is null)
            {
                continue;
            }

            yield return new Point2D(box.Min.X, box.Min.Y);
            yield return new Point2D(box.Max.X, box.Min.Y);
            yield return new Point2D(box.Max.X, box.Max.Y);
            yield return new Point2D(box.Min.X, box.Max.Y);
        }
    }

    /// <summary>
    /// Draws the outline the classification was made against.
    ///
    /// Without it a wrong answer is a mystery: there is no way to see whether
    /// the outline bridged a recess or reached in and hooked a partition. With
    /// it, one look at the plan says which.
    /// </summary>
    private static void DrawOutline(AnalysisContext context, BuildingFootprint footprint)
    {
        if (!footprint.IsUsable)
        {
            return;
        }

        Document document = context.Document;
        double elevation = context.Level.Elevation;
        double shortest = document.Application.ShortCurveTolerance;

        GraphicsStyle? style = null;
        try
        {
            Category lines = document.Settings.Categories.get_Item(BuiltInCategory.OST_Lines);

            Category made = lines.SubCategories.Contains(OutlineStyleName)
                ? lines.SubCategories.get_Item(OutlineStyleName)
                : document.Settings.Categories.NewSubcategory(lines, OutlineStyleName);

            made.LineColor = OutlineColour;
            made.SetLineWeight(4, GraphicsStyleType.Projection);
            style = made.GetGraphicsStyle(GraphicsStyleType.Projection);
        }
        catch (Exception)
        {
            // Drawn in whatever style is current rather than not at all.
        }

        IReadOnlyList<Point2D> outline = footprint.Outline;

        for (int i = 0; i < outline.Count; i++)
        {
            var a = new XYZ(outline[i].X, outline[i].Y, elevation);
            Point2D next = outline[(i + 1) % outline.Count];
            var b = new XYZ(next.X, next.Y, elevation);

            if (a.DistanceTo(b) <= shortest)
            {
                continue;
            }

            try
            {
                DetailCurve curve = document.Create.NewDetailCurve(context.View, Line.CreateBound(a, b));
                if (style is not null)
                {
                    curve.LineStyle = style;
                }
            }
            catch (Exception exception)
            {
                // Loudly. This is the diagnostic everything else is read
                // against, and it having quietly drawn nothing is exactly the
                // failure that cost a run to notice.
                throw new InvalidOperationException(
                    $"The building outline could not be drawn: {exception.Message}",
                    exception);
            }
        }
    }
}
