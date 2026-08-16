using System.Globalization;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using SpatialAnalyzer.Core.Export;
using SpatialAnalyzer.Core.Spatial;
using SpatialAnalyzer.Revit.Analysis;
using SpatialAnalyzer.Revit.Context;
using SpatialAnalyzer.Revit.Diagnostics;

namespace SpatialAnalyzer.Revit.Commands;

/// <summary>
/// Writes the whole plan out as JSON: every granular room, what is in it, what
/// lets you in, what each door connects, and every region that was not reported
/// as a room together with the reason.
///
/// This is the assignment's "analyse the view" in full. It reuses the analysis
/// if one is already held for this view, so exporting straight after qualifying
/// carries through any answers a person gave rather than asking again.
///
/// Reads only. The regions are read inside a transaction that is rolled back,
/// and the only thing written is the file.
/// </summary>
[Transaction(TransactionMode.Manual)]
public class ExportAnalysisCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        AnalysisContextResolution resolution =
            AnalysisContextResolver.Resolve(commandData.Application.ActiveUIDocument);

        if (!resolution.IsSuccess)
        {
            TaskDialog.Show("Spatial Analyzer", resolution.FailureReason!);
            return Result.Succeeded;
        }

        AnalysisContext context = resolution.Context!;

        PlanAnalysis.Result? analysis = PlanAnalysisCache.TryGet(context);
        bool reused = analysis is not null;

        ElementPlacement.Placement placement;
        string json;

        try
        {
            if (analysis is null)
            {
                analysis = PlanAnalysis.From(context, RegionQualification.Read(context));
                PlanAnalysisCache.Store(context, analysis);
            }

            placement = ElementPlacement.Of(context, analysis.Index);

            SpatialExport export = SpatialExport.Build(
                context.ToInfo(),
                context.Document.Title,
                string.IsNullOrEmpty(context.Document.PathName) ? "(unsaved)" : context.Document.PathName,
                analysis.Reading.ClosureToleranceFeet,
                analysis.Outcomes.Select(o => o.Outcome).ToList(),
                analysis.Index.Doors,
                placement.ByRoom,

                // Written by the caller rather than read from a clock inside the
                // export, so that the only part of the file which differs
                // between two runs over an unchanged model is the part that
                // should.
                DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));

            json = SpatialExportWriter.ToJson(export);
        }
        catch (Exception exception)
        {
            message = $"The export did not complete: {exception.GetType().Name}: {exception.Message}";
            return Result.Failed;
        }

        string path;
        try
        {
            path = DiagnosticFileWriter.WriteText(json, "analysis", "json");
        }
        catch (Exception exception)
        {
            message = $"The file could not be written: {exception.GetType().Name}: {exception.Message}";
            return Result.Failed;
        }

        var lines = new List<string>
        {
            string.Create(CultureInfo.InvariantCulture, $"Rooms:  {analysis.Index.Rooms.Count}"),
            string.Create(CultureInfo.InvariantCulture, $"Regions rejected:  {analysis.Outcomes.Count - analysis.Index.Rooms.Count}"),
            string.Empty,
            string.Create(CultureInfo.InvariantCulture, $"Elements considered:  {placement.Considered}"),
            string.Create(CultureInfo.InvariantCulture, $"  placed in a room:  {placement.Placed}"),
            string.Create(CultureInfo.InvariantCulture, $"  in no room:  {placement.InNoRoom}   {Top(placement.InNoRoomByCategory)}"),
            string.Create(CultureInfo.InvariantCulture, $"  with no position at all:  {placement.WithoutAPosition}   {Top(placement.WithoutAPositionByCategory)}"),
            string.Empty,
            reused ? "Reused the analysis already held for this view." : "Built the analysis fresh.",
            string.Empty,
            path,
        };

        var done = new TaskDialog("Spatial Analyzer")
        {
            MainInstruction = "Analysis exported.",
            MainContent = string.Join(Environment.NewLine, lines),
        };

        done.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Open the containing folder");
        done.CommonButtons = TaskDialogCommonButtons.Close;

        if (done.Show() == TaskDialogResult.CommandLink1)
        {
            System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{path}\"");
        }

        return Result.Succeeded;
    }

    /// <summary>
    /// The commonest few categories in a census, so that a large count is a
    /// question with a lead rather than just a number.
    /// </summary>
    private static string Top(IReadOnlyDictionary<string, long> census)
    {
        if (census.Count == 0)
        {
            return string.Empty;
        }

        var top = census
            .OrderByDescending(e => e.Value)
            .ThenBy(e => e.Key, StringComparer.Ordinal)
            .Take(3)
            .Select(e => string.Create(CultureInfo.InvariantCulture, $"{e.Key} {e.Value}"));

        return "(" + string.Join(", ", top) + ")";
    }
}
