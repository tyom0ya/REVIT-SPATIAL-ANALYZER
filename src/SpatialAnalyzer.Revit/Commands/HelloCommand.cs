using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace SpatialAnalyzer.Revit.Commands;

/// <summary>
/// Bootstrap command that proves the add-in is registered, loaded and executed
/// by Revit.
///
/// It reports the host version and the active document context rather than
/// simply saying "hello", because those are the first facts every later phase
/// depends on, and seeing them come back from a real session is what makes the
/// load meaningful. It performs no modification and opens no transaction.
///
/// This command is scaffolding for the bootstrap phase. The commands the
/// application actually ships - Analyze Selection, Analyze View and Clear
/// Analysis - arrive in later phases and this one is expected to be removed.
/// </summary>
[Transaction(TransactionMode.Manual)]
public class HelloCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        // Revit hands the command its context; never reach for a global.
        UIApplication uiApplication = commandData.Application;
        var application = uiApplication.Application;

        var lines = new List<string>
        {
            $"Revit:  {application.VersionName}",
            $"Build:  {application.VersionBuild}",
        };

        // A command can be invoked with no document open. That is a legitimate
        // state, not an error, so report it rather than throwing.
        UIDocument? uiDocument = uiApplication.ActiveUIDocument;
        if (uiDocument is null)
        {
            lines.Add("Document:  (none open)");
        }
        else
        {
            Document document = uiDocument.Document;
            lines.Add($"Document:  {document.Title}");

            View activeView = document.ActiveView;
            lines.Add($"View:  {activeView.Name}");
            lines.Add($"View type:  {activeView.ViewType}");
        }

        var dialog = new TaskDialog("Spatial Analyzer")
        {
            MainInstruction = "Add-in loaded successfully.",
            MainContent = string.Join(Environment.NewLine, lines),
        };
        dialog.Show();

        return Result.Succeeded;
    }
}
