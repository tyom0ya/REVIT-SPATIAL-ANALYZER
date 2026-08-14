using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using SpatialAnalyzer.Core.Diagnostics;
using SpatialAnalyzer.Revit.Context;
using SpatialAnalyzer.Revit.Diagnostics;

namespace SpatialAnalyzer.Revit.Commands;

/// <summary>
/// Runs the plan circuit probe and writes its report.
///
/// Unlike the other commands, this one modifies the model - it places a room in
/// every plan circuit to read the boundary that results - and then rolls the
/// whole thing back. The user is told so before it runs, because a command that
/// silently writes to a model is not something anyone should have to discover.
/// </summary>
[Transaction(TransactionMode.Manual)]
public class ProbeCircuitsCommand : IExternalCommand
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

        var confirm = new TaskDialog("Spatial Analyzer")
        {
            MainInstruction = "Run the plan circuit probe?",
            MainContent =
                "This places a temporary room in every plan circuit on this level, reads the resulting "
                + "boundaries, and then rolls the change back so the model is left as it was found. "
                + "Nothing is committed."
                + Environment.NewLine + Environment.NewLine
                + "Run it on a working copy rather than an original model.",
            CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No,
            DefaultButton = TaskDialogResult.No,
        };

        if (confirm.Show() != TaskDialogResult.Yes)
        {
            return Result.Cancelled;
        }

        string path;
        try
        {
            DiagnosticReport report = PlanCircuitProbe.Probe(resolution.Context!);
            path = DiagnosticFileWriter.Write(report, "circuit-probe");
        }
        catch (Exception exception)
        {
            message = $"The probe did not complete: {exception.GetType().Name}: {exception.Message}";
            return Result.Failed;
        }

        var done = new TaskDialog("Spatial Analyzer")
        {
            MainInstruction = "Probe written.",
            MainContent = path + Environment.NewLine + Environment.NewLine
                        + "Check the ROLLBACK VERIFICATION section first: it reports whether the model "
                        + "was left unchanged.",
        };
        done.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Open the containing folder");
        done.CommonButtons = TaskDialogCommonButtons.Close;
        done.DefaultButton = TaskDialogResult.Close;

        if (done.Show() == TaskDialogResult.CommandLink1)
        {
            System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{path}\"");
        }

        return Result.Succeeded;
    }
}
