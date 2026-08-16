using System.Reflection;
using Autodesk.Revit.UI;
using SpatialAnalyzer.Revit.Analysis;
using SpatialAnalyzer.Revit.Commands;

namespace SpatialAnalyzer.Revit.App;

/// <summary>
/// Registers the Spatial Analyzer ribbon when Revit starts.
///
/// Its only responsibility is user interface registration. No document is open
/// at startup and no model data exists yet, so nothing here reads or analyses
/// anything - the commands do that when the user invokes them.
///
/// The ribbon is arranged so that what the tool is for comes first. Four
/// commands answer the question the project exists to answer, and they get the
/// front panel. Everything else was built to interrogate the model while the
/// analysis was being worked out; it is still useful and still supported, but
/// somebody opening this for the first time should not have to sort the two
/// apart, so the investigative commands sit behind one button on a second panel.
/// </summary>
public class SpatialAnalyzerApplication : IExternalApplication
{
    private const string TabName = "Spatial Analyzer";
    private const string AnalysisPanelName = "Analysis";
    private const string ModelPanelName = "Model";

    public Result OnStartup(UIControlledApplication application)
    {
        try
        {
            CreateTab(application);

            RibbonPanel analysis = CreatePanel(application, AnalysisPanelName);
            analysis.AddItem(AnalyzeSelection());
            analysis.AddItem(HighlightRoom());
            analysis.AddStackedItems(ClearHighlight(), ExportAnalysis());

            RibbonPanel model = CreatePanel(application, ModelPanelName);
            model.AddItem(QualifyRegions());
            model.AddItem(OutlineRegions());
            model.AddSeparator();
            AddDiagnostics(model);

            // Listens for changes so a kept analysis is discarded the moment the
            // model it describes moves.
            PlanAnalysisCache.Attach(application.ControlledApplication);

            // A highlight remembers what each element looked like before it was
            // coloured. Once the document is gone those settings refer to
            // elements that no longer exist, so they are forgotten rather than
            // offered back.
            application.ControlledApplication.DocumentClosed += ForgetHighlight;
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

    public Result OnShutdown(UIControlledApplication application)
    {
        try
        {
            PlanAnalysisCache.Detach(application.ControlledApplication);
            application.ControlledApplication.DocumentClosed -= ForgetHighlight;
            RoomHighlight.Forget();
        }
        catch (Exception)
        {
            // Revit is closing. An add-in that throws on the way out achieves
            // nothing except an error message the user cannot act on.
        }

        return Result.Succeeded;
    }

    private static void ForgetHighlight(object? sender, Autodesk.Revit.DB.Events.DocumentClosedEventArgs e) =>
        RoomHighlight.Forget();

    private static void CreateTab(UIControlledApplication application)
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
            // Tab already present; the panels below will attach to it.
        }
    }

    private static RibbonPanel CreatePanel(UIControlledApplication application, string name)
    {
        foreach (RibbonPanel existing in application.GetRibbonPanels(TabName))
        {
            if (existing.Name == name)
            {
                return existing;
            }
        }

        return application.CreateRibbonPanel(TabName, name);
    }

    /// <summary>
    /// Everything built to interrogate the model, behind one button.
    ///
    /// These were written to answer questions while the analysis was being
    /// worked out, and each of them found something: which categories bound a
    /// space, that Revit's rooms are not granular, that a wall lends its doors
    /// to every space it touches. They are kept because the next model will
    /// raise questions of its own, and reaching for them should not mean
    /// rebuilding them.
    /// </summary>
    private static void AddDiagnostics(RibbonPanel panel)
    {
        var pulldown = (PulldownButton)panel.AddItem(new PulldownButtonData("SpatialAnalyzerDiagnostics", "Diagnostics")
        {
            ToolTip = "Interrogate the model: what is here, how Revit divides it, and what bounds each space.",
            LongDescription = "These read the model and write a report. Nothing is changed; where a command "
                            + "must place a temporary room to read a boundary, it rolls the change back and "
                            + "the report states whether the model was left as it was found.",
            Image = RibbonIcons.Diagnostics,
            LargeImage = RibbonIcons.Diagnostics,
        });

        pulldown.AddPushButton(Preflight());
        pulldown.AddSeparator();
        pulldown.AddPushButton(SurveyRoomBounding());
        pulldown.AddPushButton(ShowIgnoredWalls());
        pulldown.AddPushButton(InspectElement());
        pulldown.AddPushButton(AuditModel());
        pulldown.AddPushButton(ProbeCircuits());
    }

    private static string AssemblyPath => Assembly.GetExecutingAssembly().Location;

    /// <summary>
    /// A ribbon button locates its command by assembly path and class name, so
    /// the command needs no separate registration in the .addin manifest.
    ///
    /// The class name comes from typeof rather than a string literal. Revit
    /// resolves it by reflection at click time, so a literal would survive a
    /// rename or a namespace move and fail only when a user pressed the button.
    /// This way the compiler enforces it.
    /// </summary>
    private static PushButtonData Button<TCommand>(
        string name,
        string text,
        System.Windows.Media.ImageSource icon,
        string toolTip,
        string longDescription)
        where TCommand : IExternalCommand =>
        new(name, text, AssemblyPath, typeof(TCommand).FullName)
        {
            ToolTip = toolTip,
            LongDescription = longDescription,
            Image = icon,
            LargeImage = icon,
        };

    private static PushButtonData AnalyzeSelection() => Button<AnalyzeSelectionCommand>(
        "SpatialAnalyzerAnalyzeSelection",
        "Analyze\nSelection",
        RibbonIcons.AnalyzeSelection,
        "Pick an element and be told which granular room it is in.",
        "A door is answered by the two rooms it connects rather than by where it stands, because a door "
        + "stands inside a wall. Where an element is in no room, the region it does fall in is named along "
        + "with the reason that region was not reported as a room. Reads only; changes nothing.");

    private static PushButtonData HighlightRoom() => Button<HighlightRoomCommand>(
        "SpatialAnalyzerHighlightRoom",
        "Highlight\nRoom",
        RibbonIcons.HighlightRoom,
        "Pick an element and see its room coloured: red encloses, yellow is inside, green is a way through.",
        "Colours elements that already exist rather than drawing anything, in this view only. What each "
        + "element looked like before is remembered, so clearing puts back exactly what was there. One "
        + "transaction, so a single undo removes it. Offers to export that one room afterwards. Refuses to "
        + "touch the pristine model.");

    private static PushButtonData ClearHighlight() => Button<ClearHighlightCommand>(
        "SpatialAnalyzerClearHighlight",
        "Clear Highlight",
        RibbonIcons.ClearHighlight,
        "Put back everything the highlight changed.",
        "Undo does the same, but this does not require undoing whatever else has been done since.");

    private static PushButtonData ExportAnalysis() => Button<ExportAnalysisCommand>(
        "SpatialAnalyzerExportAnalysis",
        "Export Analysis",
        RibbonIcons.ExportAnalysis,
        "Write the whole plan out as JSON: every room, what is in it, and what was rejected.",
        "Lists each granular room with its area, what lets you in, and the elements it contains as "
        + "category, family, type and id. Doors appear under both rooms they connect. Regions that were "
        + "not reported as rooms are listed with the reason. Reads only; the only thing written is the file.");

    private static PushButtonData QualifyRegions() => Button<QualifyRegionsCommand>(
        "SpatialAnalyzerQualifyRegions",
        "Qualify\nRegions",
        RibbonIcons.QualifyRegions,
        "Decide which regions are rooms, asking about the ones the rule cannot settle.",
        "Applies the entrance rule to every region. Where a space is enclosed by something a person might "
        + "call a way in and the rule will not - a glazed shopfront with no door modelled in it, say - it "
        + "shows the space and asks. Any answer given is recorded as the operator's rather than the "
        + "model's. Reads only; the model is left as it was found.");

    private static PushButtonData OutlineRegions() => Button<OutlineRegionsCommand>(
        "SpatialAnalyzerOutlineRegions",
        "Outline\nRegions",
        RibbonIcons.OutlineRegions,
        "Draw and number every region on the plan, to find the ones the reports describe.",
        "Adds detail lines and text to this view only, in a line style of its own so the outline can be "
        + "seen against the walls it traces. Detail lines are annotation, not model geometry, and cannot "
        + "bound a room. Asks before running, refuses to touch the pristine model, and puts everything in "
        + "one transaction so a single undo removes it.");

    private static PushButtonData Preflight() => Button<PreflightCommand>(
        "SpatialAnalyzerPreflight",
        "Preflight",
        RibbonIcons.Preflight,
        "Check whether the current view, level and phase can support a spatial analysis.",
        "Reports the document, plan view, level and phase the analysis would run in, or explains why the "
        + "current state cannot support one. Reads only; changes nothing.");

    private static PushButtonData SurveyRoomBounding() => Button<SurveyRoomBoundingCommand>(
        "SpatialAnalyzerSurveyRoomBounding",
        "Room Bounding",
        RibbonIcons.RoomBounding,
        "Find the walls Revit ignores when working out rooms, and see how many rooms they hide.",
        "A wall whose Room Bounding flag is off is walked straight past, so a space it divides is "
        + "reported as one region. This selects those walls in the view and measures how many regions "
        + "would appear if they did divide rooms, by switching the flag on, asking Revit again and "
        + "rolling the change back. Nothing is written, and only you can say which of them really "
        + "divide a room.");

    private static PushButtonData ShowIgnoredWalls() => Button<ShowIgnoredWallsCommand>(
        "SpatialAnalyzerShowIgnoredWalls",
        "Show Ignored Walls",
        RibbonIcons.RoomBounding,
        "Draw the walls Revit ignores for rooms, and mark where each run of them stops short.",
        "Colours those walls orange and draws a red line across every place a run of them fails to "
        + "close, with the width written beside it. Where that line falls is the whole question: across "
        + "a doorway means no room is hidden there, while against another wall means that wall completes "
        + "the space and this analysis never saw it. One undo removes everything it draws.");

    private static PushButtonData InspectElement() => Button<InspectElementCommand>(
        "SpatialAnalyzerInspectElement",
        "Inspect Element",
        RibbonIcons.InspectElement,
        "Pick one element and report its category, family, type and id.",
        "Shows how the analysis identifies a selected element. Reads only; changes nothing.");

    private static PushButtonData AuditModel() => Button<AuditModelCommand>(
        "SpatialAnalyzerAuditModel",
        "Audit Model",
        RibbonIcons.AuditModel,
        "Write a full audit of this view, level and phase to a file.",
        "Records categories, existing rooms, room separation lines, links, doors and Revit's plan "
        + "topology, so the analysis design can be checked against the model rather than assumed. "
        + "Reads only; changes nothing.");

    private static PushButtonData ProbeCircuits() => Button<ProbeCircuitsCommand>(
        "SpatialAnalyzerProbeCircuits",
        "Probe Circuits",
        RibbonIcons.ProbeCircuits,
        "Investigate whether Revit's plan circuits can serve as candidate rooms.",
        "Places a temporary room in every plan circuit, reads the resulting boundaries and doors, then "
        + "rolls the change back. Asks before running. The model is left as it was found and the report "
        + "states whether that held.");
}
