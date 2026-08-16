using System.Globalization;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using SpatialAnalyzer.Core.Diagnostics;
using SpatialAnalyzer.Revit.Context;
using SpatialAnalyzer.Revit.Diagnostics;

namespace SpatialAnalyzer.Revit.Commands;

/// <summary>
/// Finds the walls Revit is told to ignore when working out rooms, selects them
/// in the view, and reports what difference they would make.
///
/// A room divided by walls that are not room bounding is reported as one region,
/// and the rooms inside it are invisible to this analysis - not because the
/// analysis is wrong, but because it is doing what the model says. This shows
/// which walls those are, so a person looking at the plan can say which of them
/// really divide rooms.
///
/// Whether the flag is switched off deliberately is not something a tool can
/// judge. It is off for good reasons often enough - shelving modelled as walls,
/// glazed screens, areas meant to read as one space - that flipping them on
/// automatically would invent rooms as readily as it found them.
///
/// The model is read inside a transaction that is rolled back, including the
/// test that measures the difference. Nothing is written.
/// </summary>
[Transaction(TransactionMode.Manual)]
public class SurveyRoomBoundingCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        UIDocument? uiDocument = commandData.Application.ActiveUIDocument;

        AnalysisContextResolution resolution = AnalysisContextResolver.Resolve(uiDocument);
        if (!resolution.IsSuccess)
        {
            TaskDialog.Show("Spatial Analyzer", resolution.FailureReason!);
            return Result.Succeeded;
        }

        RoomBoundingSurvey.Survey survey;
        try
        {
            survey = RoomBoundingSurvey.Run(resolution.Context!);
        }
        catch (Exception exception)
        {
            message = $"The survey did not complete: {exception.GetType().Name}: {exception.Message}";
            return Result.Failed;
        }

        string path;
        try
        {
            path = DiagnosticFileWriter.Write(survey.Report, "room-bounding");
        }
        catch (Exception exception)
        {
            message = $"The report could not be written: {exception.GetType().Name}: {exception.Message}";
            return Result.Failed;
        }

        // Selected so they can be looked at. Seeing where a wall is on the plan
        // is the whole question: a tool cannot tell a partition that divides two
        // rooms from a shelf that does not.
        var ids = survey.NotBoundingWallIds.Select(id => new ElementId(id)).ToList();
        if (ids.Count > 0)
        {
            uiDocument!.Selection.SetElementIds(ids);
            uiDocument.ShowElements(ids);
        }

        var lines = new List<string>();

        if (survey.NotBoundingWallIds.Count == 0)
        {
            lines.Add("Every wall in this view is room bounding.");
            lines.Add(string.Empty);
            lines.Add("Nothing is being walked past, so no room is hidden for this reason.");
        }
        else
        {
            lines.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"{survey.NotBoundingWallIds.Count} wall(s) are not room bounding, and are now selected in the view."));
            lines.Add(string.Empty);
            if (survey.Testable > 0)
            {
                lines.Add(string.Create(
                    CultureInfo.InvariantCulture,
                    $"Regions found now:  {survey.CircuitsBefore}"));
                lines.Add(string.Create(
                    CultureInfo.InvariantCulture,
                    $"Regions if the {survey.Testable} changeable one(s) divided rooms:  {survey.CircuitsAfter}"));
                lines.Add(string.Create(
                    CultureInfo.InvariantCulture,
                    $"So at least {survey.CircuitsAfter - survey.CircuitsBefore} room(s) are invisible to the analysis."));
                lines.Add(string.Empty);
                lines.Add("Measured by switching the flag on, asking Revit again, and rolling back.");
            }

            if (survey.InGroups > 0)
            {
                lines.Add(string.Empty);
                lines.Add(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{survey.InGroups} of them are inside a model group and could NOT be measured."));
                lines.Add("Revit refuses to change a grouped element outside group edit mode, and it");
                lines.Add("refuses at regeneration rather than when the value is set - so counting");
                lines.Add("them would give a number that looked measured and was not.");
                lines.Add(string.Empty);
                lines.Add("The count above therefore understates the problem.");
                lines.Add(string.Empty);
                lines.Add("Those need a change to the model, which this tool will not make for you:");
                lines.Add("open the group, tick Room Bounding on the walls that genuinely divide");
                lines.Add("rooms, and finish the group. Every instance gets it, which is usually");
                lines.Add("what an apartment layout wants.");
            }

            lines.Add(string.Empty);
            lines.Add("Look at the selected walls: some are meant not to divide rooms - shelving,");
            lines.Add("screens, areas meant to read as one space - and some are partitions whose");
            lines.Add("flag is off by accident. Only you can tell which.");
        }

        if (!survey.ModelUnchanged)
        {
            lines.Add(string.Empty);
            lines.Add("THE MODEL MAY NOT HAVE BEEN LEFT AS IT WAS. Check the report and undo.");
        }

        lines.Add(string.Empty);
        lines.Add(path);

        var done = new TaskDialog("Spatial Analyzer")
        {
            MainInstruction = survey.NotBoundingWallIds.Count == 0
                ? "No hidden rooms from room bounding."
                : "Walls that are not room bounding.",
            MainContent = string.Join(Environment.NewLine, lines),
            CommonButtons = TaskDialogCommonButtons.Close,
        };

        done.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Open the containing folder");

        if (done.Show() == TaskDialogResult.CommandLink1)
        {
            System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{path}\"");
        }

        return Result.Succeeded;
    }
}
