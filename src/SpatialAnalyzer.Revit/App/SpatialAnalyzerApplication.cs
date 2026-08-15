using System.Reflection;
using Autodesk.Revit.UI;
using SpatialAnalyzer.Revit.Commands;

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
            AddPreflightButton(panel);
            AddInspectElementButton(panel);
            AddAuditModelButton(panel);
            AddProbeCircuitsButton(panel);
            AddOutlineRegionsButton(panel);
            AddQualifyRegionsButton(panel);
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

    private static void AddPreflightButton(RibbonPanel panel)
    {
        // A ribbon button locates its command by assembly path and class name,
        // so the command needs no separate registration in the .addin manifest.
        //
        // The class name comes from typeof rather than a string literal. Revit
        // resolves it by reflection at click time, so a literal would survive a
        // rename or a namespace move and fail only when a user pressed the
        // button. This way the compiler enforces it.
        string assemblyPath = Assembly.GetExecutingAssembly().Location;

        var buttonData = new PushButtonData(
            name: "SpatialAnalyzerPreflight",
            text: "Preflight",
            assemblyName: assemblyPath,
            className: typeof(PreflightCommand).FullName)
        {
            ToolTip = "Checks whether the current view, level and phase can support a spatial analysis.",
            LongDescription = "Reports the document, plan view, level and phase the analysis would run in, "
                            + "or explains why the current state cannot support one. Reads only; changes nothing.",
        };

        panel.AddItem(buttonData);
    }

    private static void AddInspectElementButton(RibbonPanel panel)
    {
        string assemblyPath = Assembly.GetExecutingAssembly().Location;

        var buttonData = new PushButtonData(
            name: "SpatialAnalyzerInspectElement",
            text: "Inspect Element",
            assemblyName: assemblyPath,
            className: typeof(InspectElementCommand).FullName)
        {
            ToolTip = "Pick one element and report its category, family, type and id.",
            LongDescription = "Shows how the analysis identifies a selected element. Walls cannot be picked. "
                            + "Reads only; changes nothing.",
        };

        panel.AddItem(buttonData);
    }

    private static void AddAuditModelButton(RibbonPanel panel)
    {
        string assemblyPath = Assembly.GetExecutingAssembly().Location;

        var buttonData = new PushButtonData(
            name: "SpatialAnalyzerAuditModel",
            text: "Audit Model",
            assemblyName: assemblyPath,
            className: typeof(AuditModelCommand).FullName)
        {
            ToolTip = "Write a full audit of this view, level and phase to a file.",
            LongDescription = "Records categories, existing rooms, room separation lines, links, doors and "
                            + "Revit's plan topology, so the analysis design can be checked against the "
                            + "model rather than assumed. Reads only; changes nothing.",
        };

        panel.AddItem(buttonData);
    }

    private static void AddProbeCircuitsButton(RibbonPanel panel)
    {
        string assemblyPath = Assembly.GetExecutingAssembly().Location;

        var buttonData = new PushButtonData(
            name: "SpatialAnalyzerProbeCircuits",
            text: "Probe Circuits",
            assemblyName: assemblyPath,
            className: typeof(ProbeCircuitsCommand).FullName)
        {
            ToolTip = "Investigate whether Revit's plan circuits can serve as candidate rooms.",
            LongDescription = "Places a temporary room in every plan circuit, reads the resulting boundaries "
                            + "and doors, then rolls the change back. Asks before running. The model is left "
                            + "as it was found and the report states whether that held.",
        };

        panel.AddItem(buttonData);
    }

    private static void AddOutlineRegionsButton(RibbonPanel panel)
    {
        string assemblyPath = Assembly.GetExecutingAssembly().Location;

        var buttonData = new PushButtonData(
            name: "SpatialAnalyzerOutlineRegions",
            text: "Outline Regions",
            assemblyName: assemblyPath,
            className: typeof(OutlineRegionsCommand).FullName)
        {
            ToolTip = "Draw and number every region on the plan, to find the ones the reports describe.",
            LongDescription = "Adds detail lines and text to this view only. Detail lines are annotation, not "
                            + "model geometry, and cannot bound a room. Asks before running, refuses to touch "
                            + "the pristine model, and puts everything in one transaction so a single undo "
                            + "removes it.",
        };

        panel.AddItem(buttonData);
    }

    private static void AddQualifyRegionsButton(RibbonPanel panel)
    {
        string assemblyPath = Assembly.GetExecutingAssembly().Location;

        var buttonData = new PushButtonData(
            name: "SpatialAnalyzerQualifyRegions",
            text: "Qualify Regions",
            assemblyName: assemblyPath,
            className: typeof(QualifyRegionsCommand).FullName)
        {
            ToolTip = "Decide which regions are rooms, asking about the ones the rule cannot settle.",
            LongDescription = "Applies the entrance rule to every region. Where a space is enclosed by "
                            + "something a person might call a way in and the rule will not - a glazed "
                            + "shopfront with no door modelled in it, say - it shows the space and asks. "
                            + "Any answer given is recorded as the operator's rather than the model's. "
                            + "Reads only; the model is left as it was found.",
        };

        panel.AddItem(buttonData);
    }
}
