using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using SpatialAnalyzer.Core.Diagnostics;
using SpatialAnalyzer.Revit.Context;
using SpatialAnalyzer.Revit.Diagnostics;

namespace SpatialAnalyzer.Revit.Commands;

/// <summary>
/// Writes a full audit of the open model for the current view, level and phase.
///
/// The result goes to a file rather than to a dialog. There is far more here
/// than a dialog can usefully hold, it needs to be read next to the drawing,
/// and two runs need to be comparable.
///
/// Reads only. No transaction is opened and nothing is modified.
/// </summary>
[Transaction(TransactionMode.Manual)]
public class AuditModelCommand : IExternalCommand
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

        string path;
        try
        {
            DiagnosticReport report = ModelAuditor.Audit(resolution.Context!);
            path = DiagnosticFileWriter.Write(report, "audit");
        }
        catch (Exception exception)
        {
            // An audit that fails half way is still worth knowing about
            // precisely; a swallowed error here would leave the impression the
            // model had been examined when it had not.
            message = $"The audit did not complete: {exception.GetType().Name}: {exception.Message}";
            return Result.Failed;
        }

        var dialog = new TaskDialog("Spatial Analyzer")
        {
            MainInstruction = "Audit written.",
            MainContent = path,
        };
        dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Open the containing folder");
        dialog.CommonButtons = TaskDialogCommonButtons.Close;
        dialog.DefaultButton = TaskDialogResult.Close;

        if (dialog.Show() == TaskDialogResult.CommandLink1)
        {
            System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{path}\"");
        }

        return Result.Succeeded;
    }
}
