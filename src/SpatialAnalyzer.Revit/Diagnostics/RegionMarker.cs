using System.Globalization;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using SpatialAnalyzer.Core.Geometry;
using SpatialAnalyzer.Revit.Boundaries;
using SpatialAnalyzer.Revit.Context;
using CoreBoundaryLoop = SpatialAnalyzer.Core.Geometry.BoundaryLoop;

namespace SpatialAnalyzer.Revit.Diagnostics;

/// <summary>
/// Draws the regions this analysis has found, so they can be looked at instead
/// of read about.
///
/// A report can say that region one is a hundred and seven square metres with no
/// entrance, and still leave someone unable to find it on the plan. This traces
/// each region's boundary and labels it with the number the reports use.
///
/// What it draws is detail lines: view-specific annotation that belongs to this
/// view alone. They are not model geometry, cannot bound a room, and cannot
/// affect any later analysis - which matters, because a tool that drew
/// room-bounding lines around a space would change the very thing it was meant
/// to illustrate. Nothing here closes a gap either; the outline traces the
/// boundary Revit already reports, discontinuities included.
///
/// Unlike the probe, this commits. It has to, or there would be nothing to look
/// at. Everything it creates goes into one transaction, so a single undo removes
/// all of it, and the temporary rooms it needs to read boundaries are deleted
/// before that transaction closes.
/// </summary>
public static class RegionMarker
{
    private const SpatialElementBoundaryLocation BoundaryLocation = SpatialElementBoundaryLocation.Finish;

    public const string TransactionName = "Spatial Analyzer - outline regions (undo to remove)";

    public sealed record MarkResult(
        int RegionsDrawn,
        int CurvesDrawn,
        int CurvesTooShortToDraw,
        int LabelsDrawn,
        double DrawingElevationFeet,
        int RoomsBefore,
        int RoomsAfter,
        List<string> Failures);

    private sealed record RegionOutline(int Index, IReadOnlyList<CoreBoundaryLoop> Loops, UV LabelPoint, double AreaFeet2);

    public static MarkResult Draw(AnalysisContext context)
    {
        Document document = context.Document;
        var failures = new List<string>();

        int roomsBefore = CountRooms(document);

        int curves = 0;
        int tooShort = 0;
        int labels = 0;
        double elevation = 0;
        int regions = 0;

        using (var transaction = new Transaction(document, TransactionName))
        {
            transaction.Start();
            try
            {
                List<RegionOutline> outlines = ReadOutlines(context, failures);

                // Temporary rooms are gone by now: their geometry has been copied
                // into plain data, and what gets committed is only annotation.
                elevation = ChooseDrawingElevation(context);

                foreach (RegionOutline outline in outlines)
                {
                    (int drawn, int skipped) = DrawOutline(document, context.View, outline, elevation, failures);
                    curves += drawn;
                    tooShort += skipped;
                    regions++;

                    if (DrawLabel(document, context.View, outline, elevation, failures))
                    {
                        labels++;
                    }
                }

                transaction.Commit();
            }
            catch (Exception exception)
            {
                transaction.RollBack();
                failures.Add($"{exception.GetType().Name}: {exception.Message}");
            }
        }

        return new MarkResult(
            regions,
            curves,
            tooShort,
            labels,
            elevation,
            roomsBefore,
            CountRooms(document),
            failures);
    }

    private static int CountRooms(Document document) =>
        new FilteredElementCollector(document)
            .OfCategory(BuiltInCategory.OST_Rooms)
            .WhereElementIsNotElementType()
            .GetElementCount();

    /// <summary>
    /// Reads every region's boundary, using the same extraction the analysis
    /// uses, and removes the temporary rooms it needed to do so.
    ///
    /// The rooms are deleted inside this transaction rather than by rolling it
    /// back, because the drawing has to survive and they must not.
    /// </summary>
    private static List<RegionOutline> ReadOutlines(AnalysisContext context, List<string> failures)
    {
        Document document = context.Document;
        var outlines = new List<RegionOutline>();

        PlanTopology topology = document.get_PlanTopology(context.Level, context.Phase);

        List<Room> existingRooms = new FilteredElementCollector(document)
            .OfCategory(BuiltInCategory.OST_Rooms)
            .WhereElementIsNotElementType()
            .OfType<Room>()
            .Where(r => r.LevelId == context.Level.Id && r.Area > 0)
            .ToList();

        var options = new SpatialElementBoundaryOptions
        {
            SpatialElementBoundaryLocation = BoundaryLocation,
        };

        var circuits = new List<PlanCircuit>();
        foreach (PlanCircuit circuit in topology.Circuits)
        {
            circuits.Add(circuit);
        }

        int index = 0;
        // Ordered by area descending, the same as the probe, so the numbers in
        // the drawing are the numbers in the reports.
        foreach (PlanCircuit circuit in circuits.OrderByDescending(c => c.Area))
        {
            int thisIndex = index++;

            try
            {
                Room? room = circuit.IsRoomLocated ? FindExistingRoom(circuit, context, existingRooms) : null;
                bool temporary = false;

                if (room is null)
                {
                    room = document.Create.NewRoom(null, circuit);
                    if (room is null)
                    {
                        failures.Add($"region {thisIndex}: Revit would not place a room in this circuit");
                        continue;
                    }

                    temporary = true;
                    document.Regenerate();
                }

                IReadOnlyList<CoreBoundaryLoop> loops = BoundaryExtractor.Extract(room, options);
                double area = room.Area;

                if (temporary)
                {
                    document.Delete(room.Id);
                }

                outlines.Add(new RegionOutline(thisIndex, loops, circuit.GetPointInside(), area));
            }
            catch (Exception exception)
            {
                failures.Add($"region {thisIndex}: {exception.GetType().Name}: {exception.Message}");
            }
        }

        document.Regenerate();
        return outlines;
    }

    private static Room? FindExistingRoom(PlanCircuit circuit, AnalysisContext context, List<Room> rooms)
    {
        UV point = circuit.GetPointInside();
        var probe = new XYZ(point.U, point.V, context.Level.Elevation + 1.0);

        foreach (Room room in rooms)
        {
            if (room.IsPointInRoom(probe))
            {
                return room;
            }
        }

        return null;
    }

    /// <summary>
    /// Finds the height a detail curve has to sit at to belong to this view.
    ///
    /// Detail lines live in the view's own plane, which is not the height the
    /// room boundary was measured at. The view is asked for its sketch plane
    /// first; where that is unavailable the view's origin and then the level
    /// stand in.
    /// </summary>
    private static double ChooseDrawingElevation(AnalysisContext context)
    {
        SketchPlane? sketchPlane = context.View.SketchPlane;
        if (sketchPlane is not null)
        {
            return sketchPlane.GetPlane().Origin.Z;
        }

        XYZ? origin = context.View.Origin;
        return origin?.Z ?? context.Level.Elevation;
    }

    private static (int Drawn, int TooShort) DrawOutline(
        Document document,
        View view,
        RegionOutline outline,
        double elevation,
        List<string> failures)
    {
        int drawn = 0;
        int tooShort = 0;

        // Revit refuses to create a curve shorter than this, and a boundary
        // tessellation repeats a point wherever one segment meets the next.
        double shortest = document.Application.ShortCurveTolerance;

        foreach (CoreBoundaryLoop loop in outline.Loops)
        {
            foreach (Core.Geometry.BoundarySegment segment in loop.Segments)
            {
                IReadOnlyList<Point2D> points = segment.Curve.Tessellation;

                for (int i = 0; i < points.Count - 1; i++)
                {
                    var from = new XYZ(points[i].X, points[i].Y, elevation);
                    var to = new XYZ(points[i + 1].X, points[i + 1].Y, elevation);

                    if (from.DistanceTo(to) <= shortest)
                    {
                        tooShort++;
                        continue;
                    }

                    try
                    {
                        document.Create.NewDetailCurve(view, Line.CreateBound(from, to));
                        drawn++;
                    }
                    catch (Exception exception)
                    {
                        failures.Add($"region {outline.Index}: {exception.GetType().Name}: {exception.Message}");
                        return (drawn, tooShort);
                    }
                }
            }
        }

        return (drawn, tooShort);
    }

    private static bool DrawLabel(
        Document document,
        View view,
        RegionOutline outline,
        double elevation,
        List<string> failures)
    {
        ElementId textTypeId = document.GetDefaultElementTypeId(ElementTypeGroup.TextNoteType);
        if (textTypeId == ElementId.InvalidElementId)
        {
            textTypeId = new FilteredElementCollector(document)
                .OfClass(typeof(TextNoteType))
                .FirstElementId();
        }

        if (textTypeId == ElementId.InvalidElementId)
        {
            failures.Add("this document has no text style, so regions could not be labelled");
            return false;
        }

        double squareMetres = UnitUtils.ConvertFromInternalUnits(outline.AreaFeet2, UnitTypeId.SquareMeters);
        string text = string.Create(
            CultureInfo.InvariantCulture,
            $"[{outline.Index}]  {squareMetres:0.##} m2  {outline.Loops.Count} loop(s)");

        try
        {
            TextNote.Create(
                document,
                view.Id,
                new XYZ(outline.LabelPoint.U, outline.LabelPoint.V, elevation),
                text,
                textTypeId);
            return true;
        }
        catch (Exception exception)
        {
            failures.Add($"region {outline.Index} label: {exception.GetType().Name}: {exception.Message}");
            return false;
        }
    }
}
