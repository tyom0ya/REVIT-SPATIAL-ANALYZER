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

        var adjacency = DoorAdjacencyIndex.Build(
            reading.Regions.ToDictionary(
                r => r.Region.Id,
                r => (IReadOnlyList<BoundaryFeature>)r.Features.Select(f => f.Feature).ToList()),
            EntranceRule.Default);

        DiagnosticReport report = BuildReport(context, reading, outcomes, confirmed, adjacency);

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

    /// <summary>
    /// Reports what each door connects, and sets it beside what Revit's own
    /// parameters say.
    ///
    /// The comparison is the justification for not using FromRoom and ToRoom.
    /// They describe placed rooms rather than granular spaces, so where a placed
    /// room spans several of the regions this analysis finds, they name the same
    /// room on both sides of a door that plainly divides something; and where no
    /// room has been placed at all, which is most of this level, they are silent.
    /// Printing both columns lets that be seen rather than asserted.
    /// </summary>
    private static void WriteAdjacency(
        DiagnosticReport report,
        AnalysisContext context,
        RegionQualification.Reading reading,
        DoorAdjacencyIndex adjacency)
    {
        report.Section("WHAT EACH DOOR CONNECTS");

        report.Item("Connectors found", adjacency.Adjacencies.Count);
        foreach (DoorConnection connection in Enum.GetValues<DoorConnection>())
        {
            report.Item(
                string.Create(CultureInfo.InvariantCulture, $"  {connection}"),
                adjacency.Adjacencies.Count(a => a.Connection == connection));
        }

        report.Item("Ambiguous (on more than two boundaries)", adjacency.Ambiguous.Count);
        report.Blank();

        // Which regions became rooms, so the report can say when a door leads
        // somewhere this analysis decided was not a room.
        var rooms = new HashSet<RegionId>(
            reading.Regions.Select(r => r.Region.Id));

        report.Line("  ours                                  |  revit FromRoom / ToRoom");
        report.Line("  --------------------------------------+--------------------------");

        foreach (DoorAdjacency door in adjacency.Adjacencies)
        {
            string ours = door.Connection == DoorConnection.BetweenTwoRegions
                ? string.Create(CultureInfo.InvariantCulture, $"{door.Regions[0]} <-> {door.Regions[1]}")
                : string.Create(CultureInfo.InvariantCulture, $"{string.Join(", ", door.Regions)} only");

            string revit = DescribeRevitsView(context, door.Door.Id);

            string type = door.Door.TypeName.Length > 28
                ? door.Door.TypeName[..28]
                : door.Door.TypeName;

            report.Line(string.Create(
                CultureInfo.InvariantCulture,
                $"  [{door.Door.Id.Value,-9}] {type,-29} {ours,-14}|  {revit}"));
        }

        foreach (AmbiguousConnector ambiguous in adjacency.Ambiguous)
        {
            report.Blank();
            report.Line(string.Create(CultureInfo.InvariantCulture, $"  AMBIGUOUS  {ambiguous}"));
        }

        report.Blank();
        report.Line("  A door reporting one region only may open to the outside, to another");
        report.Line("  level, or to a region this analysis did not accept as a room. The");
        report.Line("  evidence does not distinguish those, and this does not pretend it does.");

        if (rooms.Count == 0)
        {
            report.Line("  No regions were read, so nothing could be connected.");
        }
    }

    private static string DescribeRevitsView(AnalysisContext context, RevitElementId doorId)
    {
        if (context.Document.GetElement(new ElementId(doorId.Value)) is not FamilyInstance door)
        {
            // Curtain wall door panels are family instances too, but anything
            // that is not one has no such parameters to report.
            return "(not a family instance)";
        }

        string Name(Autodesk.Revit.DB.Architecture.Room? room) =>
            room is null ? "(none)" : $"{room.Number} {room.Name}".Trim();

        try
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"{Name(door.get_FromRoom(context.Phase))}  ->  {Name(door.get_ToRoom(context.Phase))}");
        }
        catch (Exception exception)
        {
            return $"({exception.GetType().Name})";
        }
    }

    private static DiagnosticReport BuildReport(
        AnalysisContext context,
        RegionQualification.Reading reading,
        List<(RegionQualification.RegionReading Reading, QualificationOutcome Outcome)> outcomes,
        HashSet<RevitElementId> confirmed,
        DoorAdjacencyIndex adjacency)
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

        WriteAdjacency(report, context, reading, adjacency);

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
