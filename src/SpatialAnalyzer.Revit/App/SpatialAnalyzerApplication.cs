using System.Reflection;
using Autodesk.Revit.UI;

namespace SpatialAnalyzer.Revit.App;

/// <summary>
/// Registers the Spatial Analyzer ribbon when Revit starts.
///
/// Its only responsibility is user interface registration. No document is open
/// at startup and no model data exists yet, so nothing here reads or analyses
/// anything - the commands do that when the user invokes them.
/// </summary>
public class SpatialAnalyzerApplication : IExternalApplication
{
    private const string TabName = "Spatial Analyzer";
    private const string PanelName = "Analysis";

    public Result OnStartup(UIControlledApplication application)
    {
        try
        {
            RibbonPanel panel = CreatePanel(application);
            AddHelloButton(panel);
        }
        catch (Exception exception)
        {
            // A failure here would otherwise surface to the user as a bare
            // Revit error at startup with no indication of which add-in caused
            // it. Returning Failed still tells Revit we did not start cleanly.
            TaskDialog.Show("Spatial Analyzer", $"Failed to build the ribbon.{Environment.NewLine}{Environment.NewLine}{exception.Message}");
            return Result.Failed;
        }

        return Result.Succeeded;
    }

    public Result OnShutdown(UIControlledApplication application) => Result.Succeeded;

    private static RibbonPanel CreatePanel(UIControlledApplication application)
    {
        // Revit throws if a tab of this name already exists, which happens when
        // another add-in has claimed the same name. Catching it and reusing the
        // tab is preferable to failing startup outright.
        try
        {
            application.CreateRibbonTab(TabName);
        }
        catch (Autodesk.Revit.Exceptions.ArgumentException)
        {
            // Tab already present; the CreateRibbonPanel call below will attach
            // to it.
        }

        foreach (RibbonPanel existing in application.GetRibbonPanels(TabName))
        {
            if (existing.Name == PanelName)
            {
                return existing;
            }
        }

        return application.CreateRibbonPanel(TabName, PanelName);
    }

    private static void AddHelloButton(RibbonPanel panel)
    {
        // A ribbon button locates its command by assembly path and class name,
        // so the command needs no separate registration in the .addin manifest.
        string assemblyPath = Assembly.GetExecutingAssembly().Location;

        var buttonData = new PushButtonData(
            name: "SpatialAnalyzerHello",
            text: "Hello",
            assemblyName: assemblyPath,
            className: "SpatialAnalyzer.Revit.Commands.HelloCommand")
        {
            ToolTip = "Reports Revit version and active document context.",
            LongDescription = "Bootstrap command confirming the Spatial Analyzer add-in is loaded. "
                            + "It performs no analysis and modifies nothing.",
        };

        panel.AddItem(buttonData);
    }
}
