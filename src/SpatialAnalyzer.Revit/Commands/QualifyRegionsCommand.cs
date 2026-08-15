using System.Globalization;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using SpatialAnalyzer.Core.Diagnostics;
using SpatialAnalyzer.Core.Domain;
using SpatialAnalyzer.Core.Spatial;
using SpatialAnalyzer.Revit.Boundaries;
using SpatialAnalyzer.Revit.Context;
using SpatialAnalyzer.Revit.Diagnostics;
using SpatialAnalyzer.Revit.Selection;

namespace SpatialAnalyzer.Revit.Commands;

/// <summary>
/// Decides which regions on this level are rooms, asking about the ones the rule
/// cannot settle.
///
/// Most regions need nobody. A door is a door, and a lift shaft ringed with lift
/// doors is not a room whatever it looks like. What is left is the case where
/// the model is genuinely ambiguous: a space enclosed by something that a person
/// would call a way in and the rule will not, of which a shopfront of glazed
/// curtain walling with no door modelled in it is the example this project ran
/// into.
///
/// Rather than loosen the rule until such a space qualifies - which would admit
/// every glazed wall in every model this is ever pointed at - the space is put to
/// whoever is running the analysis, with everything on its boundary listed and
/// the candidates highlighted. Their answer is recorded as theirs. A room that
/// exists because someone was asked is reported as such, everywhere, because it
/// is a different kind of claim from one the model supports on its own.
///
/// The model is read inside a transaction that is rolled back, and nothing is
/// written at any point.
/// </summary>
[Transaction(TransactionMode.Manual)]
public class QualifyRegionsCommand : IExternalCommand
{
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

        RegionQualification.Reading reading;
        try
        {
            reading = RegionQualification.Read(context);
        }
        catch (Exception exception)
        {
            message = $"The regions could not be read: {exception.GetType().Name}: {exception.Message}";
            return Result.Failed;
        }

        var qualifier = new RoomQualifier(EntranceRule.Default);

        // Asked only after the reading transaction has closed: Revit will not
        // allow a selection while one is open.
        (HashSet<RevitElementId> confirmed, bool cancelled) = AskAbout(uiDocument!, qualifier, reading);
        if (cancelled)
        {
            return Result.Cancelled;
        }

        var outcomes = reading.Regions
            .Select(r => (Reading: r, Outcome: qualifier.Qualify(r.Region, r.Features.Select(f => f.Feature).ToList(), confirmed)))
            .ToList();

        DiagnosticReport report = BuildReport(context, reading, outcomes, confirmed);

        string path;
        try
        {
            path = DiagnosticFileWriter.Write(report, "qualification");
        }
        catch (Exception exception)
        {
            message = $"The report could not be written: {exception.GetType().Name}: {exception.Message}";
            return Result.Failed;
        }

        int rooms = outcomes.Count(o => o.Outcome.IsQualified);
        int byJudgement = outcomes.Count(o => o.Outcome.Room?.RestsOnOperatorJudgement == true);

        var done = new TaskDialog("Spatial Analyzer")
        {
            MainInstruction = string.Create(
                CultureInfo.InvariantCulture,
                $"{rooms} room(s), {outcomes.Count - rooms} candidate(s) rejected."),
            MainContent = (byJudgement > 0
                              ? string.Create(CultureInfo.InvariantCulture, $"{byJudgement} of those rooms rests on your judgement rather than on the model alone, and the report says so.{Environment.NewLine}{Environment.NewLine}")
                              : string.Empty)
                          + path,
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
    /// Puts each undecidable region to the user, one at a time.
    ///
    /// The region is shown before the question is asked, because "is this a
    /// room" is unanswerable without seeing which space is meant.
    /// </summary>
    private static (HashSet<RevitElementId> Confirmed, bool Cancelled) AskAbout(
        UIDocument uiDocument,
        RoomQualifier qualifier,
        RegionQualification.Reading reading)
    {
        var confirmed = new HashSet<RevitElementId>();

        List<RegionQualification.RegionReading> undecidable = reading.Regions
            .Where(r => qualifier.NeedsAJudgement(r.Region, r.Features.Select(f => f.Feature).ToList()))
            .ToList();

        for (int i = 0; i < undecidable.Count; i++)
        {
            RegionQualification.RegionReading region = undecidable[i];

            // Highlighting the boundary is what makes the question answerable.
            List<ElementId> boundingIds = region.Region.BoundingReferences
                .Select(r => new ElementId(r.ElementId.Value))
                .ToList();

            if (boundingIds.Count > 0)
            {
                uiDocument.ShowElements(boundingIds);
                uiDocument.Selection.SetElementIds(boundingIds);
            }

            double squareMetres = UnitUtils.ConvertFromInternalUnits(region.RevitAreaFeet2, UnitTypeId.SquareMeters);

            var counted = region.Features
                .GroupBy(f => f.Feature.Kind)
                .OrderBy(g => g.Key)
                .Select(g => string.Create(CultureInfo.InvariantCulture, $"  {g.Count()} x {g.Key}"));

            var dialog = new TaskDialog("Spatial Analyzer")
            {
                MainInstruction = string.Create(
                    CultureInfo.InvariantCulture,
                    $"Region {region.Region.Id}: {squareMetres:0.##} m2 - is this a room?"),
                MainContent = string.Create(
                    CultureInfo.InvariantCulture,
                    $"It is fully enclosed, and its boundary is highlighted in the view. Nothing on it is a way in as far as this analysis can tell{Environment.NewLine}{Environment.NewLine}")
                    + string.Join(Environment.NewLine, counted)
                    + Environment.NewLine + Environment.NewLine
                    + "If one of those is in fact how the space is entered, pick it and it will be recorded as the entrance, on your authority rather than the model's."
                    + string.Create(CultureInfo.InvariantCulture, $"{Environment.NewLine}{Environment.NewLine}Region {i + 1} of {undecidable.Count} needing a decision."),
                AllowCancellation = true,
            };

            dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Pick the entrance in the view");
            dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "Not a room", "Leave it rejected.");
            dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink3, "Leave all the rest rejected", "Stop asking and finish.");
            dialog.CommonButtons = TaskDialogCommonButtons.Cancel;

            TaskDialogResult answer = dialog.Show();

            if (answer == TaskDialogResult.Cancel)
            {
                return (confirmed, true);
            }

            if (answer == TaskDialogResult.CommandLink3)
            {
                break;
            }

            if (answer != TaskDialogResult.CommandLink1)
            {
                continue;
            }

            // Restricted to the candidates this region's own geometry produced,
            // so a pick can settle which one is the entrance and can never
            // introduce one that is not on the boundary.
            var filter = new BoundaryElementSelectionFilter(
                region.Features.Select(f => f.Feature.Element.Id.Value));

            try
            {
                Reference reference = uiDocument.Selection.PickObject(
                    ObjectType.Element,
                    filter,
                    string.Create(CultureInfo.InvariantCulture, $"Pick what lets you into region {region.Region.Id}, or press Escape."));

                confirmed.Add(new RevitElementId(reference.ElementId.Value));
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                // Escape means "not this one after all", not a fault, and not a
                // reason to abandon the remaining regions.
            }
        }

        uiDocument.Selection.SetElementIds(new List<ElementId>());
        return (confirmed, false);
    }

    private static DiagnosticReport BuildReport(
        AnalysisContext context,
        RegionQualification.Reading reading,
        List<(RegionQualification.RegionReading Reading, QualificationOutcome Outcome)> outcomes,
        HashSet<RevitElementId> confirmed)
    {
        var report = new DiagnosticReport("REVIT SPATIAL ANALYZER - QUALIFICATION");

        report.Section("SESSION");
        report.Item("Document", context.Document.Title);
        report.Item("Path", string.IsNullOrEmpty(context.Document.PathName) ? "(unsaved)" : context.Document.PathName);
        report.Item("View", context.View.Name);
        report.Item("Level", context.Level.Name);
        report.Item("Phase", context.Phase.Name);
        report.Item("Closure tolerance (ft)", reading.ClosureToleranceFeet);
        report.Item("Generated", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));

        report.Section("MODEL UNCHANGED");
        report.Item("Transaction status", reading.Status.ToString());
        report.Item("Rooms before", reading.RoomsBefore);
        report.Item("Rooms after", reading.RoomsAfter);
        report.Item(
            "Model unchanged",
            reading.Status == TransactionStatus.RolledBack && reading.RoomsBefore == reading.RoomsAfter
                ? "yes"
                : "NO - INVESTIGATE");

        report.Section("RESULT");
        report.Item("Regions", outcomes.Count);
        report.Item("  rooms", outcomes.Count(o => o.Outcome.IsQualified));
        report.Item("    on the model's evidence", outcomes.Count(o => o.Outcome.IsQualified && !o.Outcome.Room!.RestsOnOperatorJudgement));
        report.Item("    on the operator's judgement", outcomes.Count(o => o.Outcome.Room?.RestsOnOperatorJudgement == true));
        report.Item("  rejected", outcomes.Count(o => !o.Outcome.IsQualified));

        report.Blank();
        report.Line("  Rooms:");
        foreach ((RegionQualification.RegionReading r, QualificationOutcome outcome) in outcomes.Where(o => o.Outcome.IsQualified))
        {
            double squareMetres = UnitUtils.ConvertFromInternalUnits(r.RevitAreaFeet2, UnitTypeId.SquareMeters);
            string mark = outcome.Room!.RestsOnOperatorJudgement ? " *" : "  ";
            report.Line(string.Create(
                CultureInfo.InvariantCulture,
                $"   {mark}[{r.Region.Id.ToString(),-4}] {squareMetres,9:0.###} m2   {outcome.Explanation}"));
        }

        report.Blank();
        report.Line("  * rests on the operator's judgement, not on the model alone.");

        report.Blank();
        report.Line("  Rejected:");
        foreach ((RegionQualification.RegionReading r, QualificationOutcome outcome) in outcomes.Where(o => !o.Outcome.IsQualified))
        {
            double squareMetres = UnitUtils.ConvertFromInternalUnits(r.RevitAreaFeet2, UnitTypeId.SquareMeters);
            report.Line(string.Create(
                CultureInfo.InvariantCulture,
                $"     [{r.Region.Id.ToString(),-4}] {squareMetres,9:0.###} m2   {outcome.Reason}"));
            report.Line($"            {outcome.Explanation}");
        }

        if (confirmed.Count > 0)
        {
            report.Section("CONFIRMED BY THE OPERATOR");
            report.Line("  These were not entrances as far as the rule is concerned. A person");
            report.Line("  was shown the region and its boundary and said otherwise.");
            report.Blank();

            foreach ((RegionQualification.RegionReading r, QualificationOutcome outcome) in outcomes)
            {
                foreach (RoomEntrance entrance in outcome.Room?.Entrances
                             .Where(e => e.Authority == EntranceAuthority.OperatorConfirmed) ?? Enumerable.Empty<RoomEntrance>())
                {
                    report.Line(string.Create(
                        CultureInfo.InvariantCulture,
                        $"    region {r.Region.Id}: {entrance.Kind} {entrance.Element}"));
                }
            }
        }

        report.Section("EVERYTHING ON EACH BOUNDARY");
        report.Line("  Tested geometrically: an insert counts only where it lands on a boundary");
        report.Line("  curve produced by its own host.");

        foreach (RegionQualification.RegionReading r in reading.Regions.Where(r => r.Features.Count > 0))
        {
            report.Blank();
            report.Line(string.Create(CultureInfo.InvariantCulture, $"    [{r.Region.Id}]"));

            foreach (BoundaryFeatureCollector.Found found in r.Features)
            {
                report.Line(string.Create(
                    CultureInfo.InvariantCulture,
                    $"        {found.Feature.Kind,-22} {found.Feature.Element}   host {found.HostElementId}   at {found.DistanceFeet:0.###} ft"));
            }
        }

        if (reading.Failures.Count > 0)
        {
            report.Section("FAILURES");
            foreach (string failure in reading.Failures)
            {
                report.Line("  " + failure);
            }
        }

        return report;
    }
}
