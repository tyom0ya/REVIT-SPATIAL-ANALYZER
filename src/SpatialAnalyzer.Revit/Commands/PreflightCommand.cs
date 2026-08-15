using System.Globalization;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using SpatialAnalyzer.Core.Domain;
using SpatialAnalyzer.Revit.Context;

namespace SpatialAnalyzer.Revit.Commands;

/// <summary>
/// Reports whether the current Revit state can support a spatial analysis, and
/// exactly which view, level and phase it would run in.
///
/// This exists so that assumptions about the model are checked against the
/// model rather than carried in someone's head. When analysis later produces a
/// surprising result, the first question is always "in which view, on which
/// level, in which phase" - and this answers it without running anything.
///
/// It reads only. No transaction is opened and nothing is modified.
/// </summary>
[Transaction(TransactionMode.Manual)]
public class PreflightCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        UIApplication uiApplication = commandData.Application;
        var application = uiApplication.Application;

        AnalysisContextResolution resolution = AnalysisContextResolver.Resolve(uiApplication.ActiveUIDocument);

        var lines = new List<string>
        {
            $"Revit:  {application.VersionName}  ({application.VersionBuild})",
        };

        if (!resolution.IsSuccess)
        {
            lines.Add(string.Empty);
            lines.Add(resolution.FailureReason!);

            ShowDialog("Not ready", lines, ready: false);

            // The user's situation is reportable, not a command failure; Result
            // .Failed would make Revit show its own error dialog on top of ours.
            return Result.Succeeded;
        }

        AnalysisContext context = resolution.Context!;
        AnalysisContextInfo info = context.ToInfo();

        lines.Add($"Document:  {context.Document.Title}");
        lines.Add(string.Empty);
        lines.Add($"View:  {info.ViewName}   (id {info.ViewId})");
        lines.Add($"View type:  {info.ViewType}");
        lines.Add($"Level:  {info.LevelName}   (id {info.LevelId})");
        lines.Add($"Elevation:  {FormatElevation(info.LevelElevationInternalFeet)}");
        lines.Add($"Phase:  {info.PhaseName}   (id {info.PhaseId})");

        ShowDialog("Ready", lines, ready: true);
        return Result.Succeeded;
    }

    /// <summary>
    /// Revit stores lengths internally in decimal feet whatever the project
    /// units say, so a raw elevation is meaningless to most users. This
    /// converts for display only; the underlying value is kept in internal
    /// units so nothing is lost to rounding.
    /// </summary>
    private static string FormatElevation(double internalFeet)
    {
        double millimetres = UnitUtils.ConvertFromInternalUnits(internalFeet, UnitTypeId.Millimeters);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{millimetres:0.#} mm  ({internalFeet:0.####} ft internal)");
    }

    private static void ShowDialog(string verdict, List<string> lines, bool ready)
    {
        var dialog = new TaskDialog("Spatial Analyzer")
        {
            MainInstruction = ready ? "Ready to analyse." : "Cannot analyse yet.",
            MainContent = string.Join(Environment.NewLine, lines),
        };
        dialog.Show();
    }
}
