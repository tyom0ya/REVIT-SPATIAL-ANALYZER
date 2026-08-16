using System.Globalization;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using SpatialAnalyzer.Core.Spatial;
using SpatialAnalyzer.Revit.Boundaries;
using SpatialAnalyzer.Revit.Context;
using SpatialAnalyzer.Revit.Diagnostics;

namespace SpatialAnalyzer.Revit.Commands;

/// <summary>
/// Draws the walls Revit is told to ignore for rooms, and marks every place a
/// run of them stops short of enclosing anything.
///
/// This exists because the analysis reached a question it cannot answer. Those
/// walls hide rooms or they do not, and what decides it is where their gaps
/// fall: at doorways, or where a partition meets a wall that the search was
/// never given. The numbers are identical either way. The plan is not.
///
/// So this is a command for looking, not for concluding. It colours the walls,
/// draws a line across each gap and writes its width beside it, and stops.
/// </summary>
[Transaction(TransactionMode.Manual)]
public class ShowIgnoredWallsCommand : IExternalCommand
{
    /// <summary>
    /// The original model is never written to by this project's tooling, and
    /// this command commits detail lines and view overrides.
    /// </summary>
    private const string ProtectedPathFragment = @"\models\pristine\";

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

        if (survey.WallsConsidered == 0)
        {
            TaskDialog.Show(
                "Spatial Analyzer",
                "Every wall in this view is room bounding, so nothing is being walked past and there is nothing to draw.");
            return Result.Succeeded;
        }

        PartitionMarker.Result marked;
        using (var transaction = new Transaction(context.Document, "Spatial Analyzer show ignored walls"))
        {
            transaction.Start();

            try
            {
                marked = PartitionMarker.Apply(context, survey);
            }
            catch (Exception exception)
            {
                transaction.RollBack();
                message = $"The walls could not be drawn: {exception.GetType().Name}: {exception.Message}";
                return Result.Failed;
            }

            transaction.Commit();
        }

        uiDocument!.RefreshActiveView();

        var lines = new List<string>
        {
            string.Create(CultureInfo.InvariantCulture, $"Walls Revit ignores for rooms:  {survey.WallsConsidered}"),
            string.Create(CultureInfo.InvariantCulture, $"   orange on the plan:  {marked.WallsMarked}"),
            string.Empty,
            string.Empty,
            string.Create(
                CultureInfo.InvariantCulture,
                $"ROOMS THE MODEL DOES NOT REPORT:  {marked.RoomsFound}   (blue, with areas)"),
            string.Empty,

            // Every stage's count, so a floor where rooms are missing says
            // which stage lost them rather than only that they are absent.
            // Guessing at that twice has already cost two rebuilds.
            string.Create(
                CultureInfo.InvariantCulture,
                $"Spaces the walls enclose in all:  {survey.Subdivision.Faces.Count}"),
            string.Create(
                CultureInfo.InvariantCulture,
                $"   of those, touching an ignored wall:  {PartitionSurvey.HiddenBy(survey).Count}"),
            string.Create(
                CultureInfo.InvariantCulture,
                $"   set aside as too narrow to stand in:  {survey.Subdivision.FacesTooNarrowToStandIn}"),
            string.Create(
                CultureInfo.InvariantCulture,
                $"   wall pieces after splitting:  {survey.Subdivision.SegmentsAfterSplitting}"),
            string.Empty,
            "Loose ends of those walls, measured to the nearest wall of ANY kind:",
            string.Create(CultureInfo.InvariantCulture, $"   green, stopping against a wall:  {marked.EndsMeetingAnotherWall}"),
            string.Create(CultureInfo.InvariantCulture, $"   red, standing in open air:  {marked.EndsInOpenAir}"),
        };

        if (marked.SmallestOpenGapInternalFeet > 0)
        {
            lines.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"      nearest of those:  {marked.SmallestOpenGapInternalFeet * 304.8:0} mm"));
        }

        lines.Add(string.Empty);

        lines.Add(marked.RoomsFound > 0
            ? "Each blue outline is a space these walls enclose that Revit does not report"
              + Environment.NewLine
              + "as a room, because it is told to walk past the walls that close it."
            : "These walls close no space that Revit is not already reporting.");

        lines.Add(string.Empty);
        lines.Add("Press Ctrl+Z once to remove all of this.");

        var done = new TaskDialog("Spatial Analyzer")
        {
            MainInstruction = "Ignored walls drawn.",
            MainContent = string.Join(Environment.NewLine, lines),
            CommonButtons = TaskDialogCommonButtons.Close,
        };

        if (marked.Failures.Count > 0)
        {
            done.ExpandedContent = string.Join(
                Environment.NewLine,
                marked.Failures.Distinct(StringComparer.Ordinal).Take(20));
        }

        done.Show();
        return Result.Succeeded;
    }
}
