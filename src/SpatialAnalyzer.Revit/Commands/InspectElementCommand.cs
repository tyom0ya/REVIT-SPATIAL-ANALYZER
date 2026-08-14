using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using SpatialAnalyzer.Core.Domain;
using SpatialAnalyzer.Revit.Context;
using SpatialAnalyzer.Revit.Elements;
using SpatialAnalyzer.Revit.Selection;

namespace SpatialAnalyzer.Revit.Commands;

/// <summary>
/// Asks the user to pick one element and reports what the analysis will see it
/// as: category, family, type and id, in the resolved view, level and phase.
///
/// This is the selection half of the eventual Analyze Selection workflow,
/// proven on its own before any spatial reasoning depends on it. If the
/// descriptor is wrong here, every downstream result is wrong in a way that is
/// far harder to notice.
///
/// Reads only. No transaction is opened and nothing is modified.
/// </summary>
[Transaction(TransactionMode.Manual)]
public class InspectElementCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        UIApplication uiApplication = commandData.Application;
        UIDocument? uiDocument = uiApplication.ActiveUIDocument;

        // The same context rules the real analysis uses. Requiring them here
        // means the selection path is exercised under the conditions it will
        // actually run in.
        AnalysisContextResolution resolution = AnalysisContextResolver.Resolve(uiDocument);
        if (!resolution.IsSuccess)
        {
            TaskDialog.Show("Spatial Analyzer", resolution.FailureReason!);
            return Result.Succeeded;
        }

        AnalysisContext context = resolution.Context!;
        AnalysisContextInfo info = context.ToInfo();

        Reference reference;
        try
        {
            reference = uiDocument!.Selection.PickObject(
                ObjectType.Element,
                new NonWallSelectionFilter(),
                "Select an element to inspect. Walls cannot be selected.");
        }
        catch (Autodesk.Revit.Exceptions.OperationCanceledException)
        {
            // Pressing Escape is a decision, not a fault. Cancelled tells Revit
            // to end quietly rather than report a failure.
            return Result.Cancelled;
        }

        Element element = context.Document.GetElement(reference);
        ElementDescriptor descriptor = ElementDescriptorFactory.Describe(element);

        var lines = new List<string>
        {
            $"Category:  {descriptor.CategoryName}",
            $"Family:  {descriptor.FamilyName}",
            $"Type:  {descriptor.TypeName}",
            $"Revit id:  {descriptor.Id}",
            string.Empty,
            $"Element class:  {element.GetType().Name}",
            string.Empty,
            $"View:  {info.ViewName}",
            $"Level:  {info.LevelName}",
            $"Phase:  {info.PhaseName}",
        };

        var dialog = new TaskDialog("Spatial Analyzer")
        {
            MainInstruction = "Selected element",
            MainContent = string.Join(Environment.NewLine, lines),
        };
        dialog.Show();

        return Result.Succeeded;
    }
}
