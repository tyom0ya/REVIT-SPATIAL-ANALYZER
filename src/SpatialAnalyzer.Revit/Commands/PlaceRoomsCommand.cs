using System.Globalization;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using SpatialAnalyzer.Core.Geometry;
using SpatialAnalyzer.Core.Spatial;
using SpatialAnalyzer.Revit.Boundaries;
using SpatialAnalyzer.Revit.Context;

namespace SpatialAnalyzer.Revit.Commands;

/// <summary>
/// Places a real room in each space the model encloses but does not report.
///
/// Everything before this found those spaces and drew them. This is the part
/// that acts: it lays a room separation line along each wall the model ignores,
/// and then asks Revit to put a room in the space that results. Both are
/// additions. No existing element is touched, no wall's Room Bounding flag is
/// changed, and no group is opened - which is what makes it work at all, since
/// Revit refuses to modify a grouped wall from outside group edit mode.
///
/// It commits, unlike the rest of the analysis, and says so plainly. A single
/// undo removes every line and every room it made.
///
/// The check is built in and it is the point. Each room Revit places is
/// measured against the space this project computed. Agreement means the
/// separation lines did their work and the room sits where the analysis said
/// it would. A room that comes back far larger means Revit ignored the lines
/// and filled the whole apartment instead, and the report says so rather than
/// claiming a room was created correctly. Nothing about placing a room proves
/// the room is right; comparing its area does.
/// </summary>
[Transaction(TransactionMode.Manual)]
public class PlaceRoomsCommand : IExternalCommand
{
    private const string ProtectedPathFragment = @"\models\pristine\";

    /// <summary>
    /// How far a placed room's area may differ from the computed space before
    /// it is reported as a disagreement rather than a match.
    ///
    /// Generous, because the two are measured differently: this project works
    /// along wall centre lines, while Revit measures a room to the wall faces.
    /// A room bounded by partitions is smaller by half a wall thickness all
    /// round, and that is a real difference rather than a fault.
    /// </summary>
    private const double AgreementFraction = 0.25;

    /// <summary>
    /// The smallest space worth putting a room in, in square feet - about half
    /// a square metre.
    ///
    /// The analysis reports every space it finds, and should: a face of a
    /// seventh of a square metre is a real enclosure and saying so costs
    /// nothing. Writing a room into somebody's model is a different act. A
    /// space that small is a duct or a pipe shaft, and filling a floor with
    /// rooms for them makes the rooms that matter harder to see, in a model
    /// the person then has to tidy up.
    ///
    /// Reporting is free and generous; writing is conservative. The threshold
    /// belongs here rather than in the analysis for that reason.
    /// </summary>
    private const double SmallestWorthPlacingSquareFeet = 5.4;

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

        if (context.Document.PathName.Contains(ProtectedPathFragment, StringComparison.OrdinalIgnoreCase))
        {
            TaskDialog.Show(
                "Spatial Analyzer",
                "This document is the pristine model, which development tooling must not write to."
                + Environment.NewLine + Environment.NewLine
                + "Open the working copy under models\\dev and run this there.");
            return Result.Cancelled;
        }

        PartitionSurvey.Result survey;
        try
        {
            var tolerance = new ClosureTolerance(context.Document.Application.ShortCurveTolerance);
            survey = PartitionSurvey.Of(context, tolerance.InternalFeet);
        }
        catch (Exception exception)
        {
            message = $"The walls could not be read: {exception.GetType().Name}: {exception.Message}";
            return Result.Failed;
        }

        IReadOnlyList<PlanFace> wanted = PartitionSurvey.HiddenBy(survey)
            .Where(f => f.Area.InternalSquareFeet >= SmallestWorthPlacingSquareFeet)
            .ToList();

        if (wanted.Count == 0)
        {
            TaskDialog.Show(
                "Spatial Analyzer",
                "Every space these walls enclose is already reported as a room. There is nothing to place.");
            return Result.Succeeded;
        }

        if (!Confirm(wanted.Count))
        {
            return Result.Cancelled;
        }

        Placement placement;
        using (var transaction = new Transaction(context.Document, "Spatial Analyzer place rooms"))
        {
            transaction.Start();

            try
            {
                placement = Place(context, survey, wanted);
            }
            catch (Exception exception)
            {
                transaction.RollBack();
                message = $"The rooms could not be placed: {exception.GetType().Name}: {exception.Message}";
                return Result.Failed;
            }

            transaction.Commit();
        }

        uiDocument!.RefreshActiveView();
        Report(placement, wanted.Count);
        return Result.Succeeded;
    }

    private sealed record Placement(
        int SeparationLines,
        int RoomsPlaced,
        int AgreeingWithTheAnalysis,
        int LargerThanExpected,
        int Refused,
        IReadOnlyList<string> Failures);

    private static Placement Place(
        AnalysisContext context,
        PartitionSurvey.Result survey,
        IReadOnlyList<PlanFace> wanted)
    {
        Document document = context.Document;
        var failures = new List<string>();

        int lines = LaySeparationLines(context, survey, failures);

        // The topology is not recomputed until the model catches up, and the
        // rooms below are placed against it.
        document.Regenerate();

        int placed = 0;
        int agreeing = 0;
        int larger = 0;
        int refused = 0;

        foreach (PlanFace face in wanted)
        {
            if (!PlanFaces.TryFindPointInside(face, out Point2D inside))
            {
                refused++;
                continue;
            }

            try
            {
                Room? room = document.Create.NewRoom(context.Level, new UV(inside.X, inside.Y));
                if (room is null)
                {
                    refused++;
                    continue;
                }

                placed++;

                double expected = face.Area.InternalSquareFeet;
                double actual = room.Area;

                if (actual <= expected * (1 + AgreementFraction))
                {
                    agreeing++;
                }
                else
                {
                    larger++;
                }
            }
            catch (Exception exception)
            {
                refused++;
                failures.Add($"room: {exception.GetType().Name}: {exception.Message}");
            }
        }

        return new Placement(lines, placed, agreeing, larger, refused, failures);
    }

    /// <summary>
    /// Lays a room separation line along every wall the model ignores.
    ///
    /// The lines are what make Revit see the division; without them a room
    /// placed inside a bathroom fills the whole apartment, because the walls
    /// that divide it are ones Revit has been told to walk past.
    ///
    /// Built on a sketch plane made from the level itself rather than from a
    /// plane at the level's height. Those are not the same thing: a plane made
    /// by normal and origin belongs to no level, and lines drawn on it are
    /// accepted, given the right category, and bound nothing at all.
    /// </summary>
    private static int LaySeparationLines(
        AnalysisContext context,
        PartitionSurvey.Result survey,
        List<string> failures)
    {
        Document document = context.Document;
        SketchPlane sketch = SketchPlane.Create(document, context.Level.Id);

        double elevation = context.Level.Elevation;
        double shortest = document.Application.ShortCurveTolerance;
        int drawn = 0;

        foreach (PlanFace face in PartitionSurvey.HiddenBy(survey))
        {
            IReadOnlyList<Point2D> outline = face.Outline;

            for (int i = 0; i < outline.Count; i++)
            {
                Point2D from = outline[i];
                Point2D to = outline[(i + 1) % outline.Count];

                var a = new XYZ(from.X, from.Y, elevation);
                var b = new XYZ(to.X, to.Y, elevation);

                if (a.DistanceTo(b) <= shortest)
                {
                    continue;
                }

                try
                {
                    var one = new CurveArray();
                    one.Append(Line.CreateBound(a, b));

                    ModelCurveArray created = document.Create.NewRoomBoundaryLines(sketch, one, context.View);
                    foreach (ModelCurve _ in created)
                    {
                        drawn++;
                    }
                }
                catch (Exception exception)
                {
                    // One line at a time, because Revit rejects a whole batch
                    // for a single offending curve and a wholesale failure
                    // looks exactly like one that did nothing.
                    failures.Add($"line: {exception.GetType().Name}: {exception.Message}");
                }
            }
        }

        return drawn;
    }

    private static bool Confirm(int count)
    {
        var ask = new TaskDialog("Spatial Analyzer")
        {
            MainInstruction = string.Create(CultureInfo.InvariantCulture, $"Place {count} room(s) in this model?"),
            MainContent = "This one writes to the model, unlike the rest of the analysis."
                        + Environment.NewLine + Environment.NewLine
                        + "It adds a room separation line along each wall Revit is told to ignore, and "
                        + "places a room in each space that results. Nothing existing is changed: no wall "
                        + "is edited, no flag is switched, no group is opened."
                        + Environment.NewLine + Environment.NewLine
                        + "One undo removes all of it.",
            CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.Cancel,
            DefaultButton = TaskDialogResult.Cancel,
        };

        return ask.Show() == TaskDialogResult.Yes;
    }

    private static void Report(Placement placement, int wanted)
    {
        var lines = new List<string>
        {
            string.Create(CultureInfo.InvariantCulture, $"Spaces found:  {wanted}"),
            string.Create(CultureInfo.InvariantCulture, $"Separation lines laid:  {placement.SeparationLines}"),
            string.Create(CultureInfo.InvariantCulture, $"Rooms placed:  {placement.RoomsPlaced}"),
            string.Empty,
            "Each room's area against the space it was meant to fill:",
            string.Create(CultureInfo.InvariantCulture, $"   agreeing:  {placement.AgreeingWithTheAnalysis}"),
            string.Create(CultureInfo.InvariantCulture, $"   larger than expected:  {placement.LargerThanExpected}"),
        };

        if (placement.Refused > 0)
        {
            lines.Add(string.Create(CultureInfo.InvariantCulture, $"   Revit would not place:  {placement.Refused}"));
        }

        lines.Add(string.Empty);

        // Placing a room proves nothing on its own. A room that came back the
        // size of the whole apartment was placed just as successfully as one
        // that fits, and only the comparison tells them apart.
        lines.Add(placement.LargerThanExpected > 0
            ? "A room larger than expected means Revit did not respect the separation lines"
              + Environment.NewLine
              + "and filled the surrounding space instead. Undo, and treat those as not placed."
            : "Every room fits the space it was placed in.");

        lines.Add(string.Empty);
        lines.Add("Press Ctrl+Z once to remove the rooms and the lines together.");

        var done = new TaskDialog("Spatial Analyzer")
        {
            MainInstruction = placement.RoomsPlaced > 0 ? "Rooms placed." : "No room could be placed.",
            MainContent = string.Join(Environment.NewLine, lines),
            CommonButtons = TaskDialogCommonButtons.Close,
        };

        if (placement.Failures.Count > 0)
        {
            done.ExpandedContent = string.Join(
                Environment.NewLine,
                placement.Failures.Distinct(StringComparer.Ordinal).Take(20));
        }

        done.Show();
    }
}
