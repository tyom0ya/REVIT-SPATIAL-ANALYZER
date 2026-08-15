using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using SpatialAnalyzer.Revit.Context;
using SpatialAnalyzer.Revit.Diagnostics;

namespace SpatialAnalyzer.Revit.Commands;

/// <summary>
/// Draws an outline and a number around every region the analysis has found.
///
/// This is the only command that leaves anything behind. It has to: the point of
/// it is to put something on the screen that a person can look at and compare
/// against the reports. What it leaves is annotation - detail lines and text in
/// this view only - and all of it goes into one transaction, so a single undo
/// takes it all away again.
/// </summary>
[Transaction(TransactionMode.Manual)]
public class OutlineRegionsCommand : IExternalCommand
{
    /// <summary>
    /// The original model is never written to by this project's tooling, and a
    /// command that commits is the one place that could go wrong by accident, so
    /// it is refused here rather than trusted to habit.
    /// </summary>
    private const string ProtectedPathFragment = @"\models\pristine\";

    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        UIDocument uiDocument = commandData.Application.ActiveUIDocument;

        AnalysisContextResolution resolution = AnalysisContextResolver.Resolve(uiDocument);
        if (!resolution.IsSuccess)
        {
            TaskDialog.Show("Spatial Analyzer", resolution.FailureReason!);
            return Result.Succeeded;
        }

        Document document = resolution.Context!.Document;

        if (document.PathName.Contains(ProtectedPathFragment, StringComparison.OrdinalIgnoreCase))
        {
            TaskDialog.Show(
                "Spatial Analyzer",
                "This document is the pristine model, which development tooling must not write to."
                + Environment.NewLine + Environment.NewLine
                + document.PathName
                + Environment.NewLine + Environment.NewLine
                + "Open the working copy under models\\dev and run this there.");
            return Result.Cancelled;
        }

        var confirm = new TaskDialog("Spatial Analyzer")
        {
            MainInstruction = "Outline every region in this view?",
            MainContent =
                "This draws a detail line around each region the analysis finds and labels it with the "
                + "number the reports use, so they can be found on the plan."
                + Environment.NewLine + Environment.NewLine
                + "It adds annotation to this view only. Detail lines are not model geometry and cannot "
                + "bound a room, so nothing about the building changes. Everything goes into one "
                + "transaction: a single undo removes all of it."
                + Environment.NewLine + Environment.NewLine
                + document.PathName,
            CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No,
            DefaultButton = TaskDialogResult.No,
        };

        if (confirm.Show() != TaskDialogResult.Yes)
        {
            return Result.Cancelled;
        }

        RegionMarker.MarkResult result;
        try
        {
            result = RegionMarker.Draw(resolution.Context!);
        }
        catch (Exception exception)
        {
            message = $"The outline did not complete: {exception.GetType().Name}: {exception.Message}";
            return Result.Failed;
        }

        uiDocument.RefreshActiveView();

        string roomNote = result.RoomsBefore == result.RoomsAfter
            ? $"Rooms in the document: {result.RoomsAfter}, unchanged."
            : $"ROOM COUNT CHANGED: {result.RoomsBefore} before, {result.RoomsAfter} after. Undo and report this.";

        var summary = new TaskDialog("Spatial Analyzer")
        {
            MainInstruction = result.RegionsDrawn > 0 ? "Regions outlined." : "Nothing was drawn.",
            MainContent =
                $"{result.RegionsDrawn} region(s), {result.CurvesDrawn} line(s), {result.LabelsDrawn} label(s)."
                + Environment.NewLine
                + $"{result.CurvesTooShortToDraw} segment(s) were shorter than Revit will draw and were left out."
                + Environment.NewLine + Environment.NewLine
                + roomNote
                + Environment.NewLine + Environment.NewLine
                + "Press Ctrl+Z once to remove everything this added.",
            CommonButtons = TaskDialogCommonButtons.Close,
        };

        if (result.Failures.Count > 0)
        {
            summary.ExpandedContent = string.Join(Environment.NewLine, result.Failures);
        }

        summary.Show();
        return Result.Succeeded;
    }
}
