using System.Globalization;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using SpatialAnalyzer.Core.Diagnostics;
using SpatialAnalyzer.Revit.Context;

namespace SpatialAnalyzer.Revit.Diagnostics;

/// <summary>
/// Interrogates the open model and records what is actually there.
///
/// This exists so that the room-detection design is answerable to the model
/// rather than to assumptions about it. Every question here is one whose wrong
/// answer would send later phases down an expensive dead end: whether the model
/// already contains rooms, whether room separation lines are doing structural
/// work, whether geometry arrives through links, and whether Revit's own plan
/// topology divides the floor the way the brief expects.
///
/// It reads only, opens no transaction, and modifies nothing.
/// </summary>
public static class ModelAuditor
{
    private const int SampleLimit = 20;

    public static DiagnosticReport Audit(AnalysisContext context)
    {
        Document document = context.Document;
        var report = new DiagnosticReport("REVIT SPATIAL ANALYZER - MODEL AUDIT");

        WriteSession(report, document);
        WriteContext(report, context);
        WriteLevelsAndPlanViews(report, document);
        WriteCategoryCensus(report, context);
        WriteRooms(report, context);
        WriteRoomSeparationLines(report, context);
        WriteLinks(report, document);
        WriteDoors(report, context);
        WritePlanTopology(report, context);

        return report;
    }

    private static void WriteSession(DiagnosticReport report, Document document)
    {
        report.Section("SESSION");
        report.Item("Revit", document.Application.VersionName);
        report.Item("Build", document.Application.VersionBuild);
        report.Item("Document", document.Title);
        report.Item("Path", string.IsNullOrEmpty(document.PathName) ? "(unsaved)" : document.PathName);
        report.Item("Generated", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
    }

    private static void WriteContext(DiagnosticReport report, AnalysisContext context)
    {
        report.Section("ANALYSIS CONTEXT");
        report.Item("View", string.Create(CultureInfo.InvariantCulture, $"{context.View.Name}  (id {context.View.Id.Value})"));
        report.Item("View type", context.View.ViewType.ToString());
        report.Item("Level", string.Create(CultureInfo.InvariantCulture, $"{context.Level.Name}  (id {context.Level.Id.Value})"));
        report.Item("Level elevation (ft)", context.Level.Elevation);
        report.Item("Phase", string.Create(CultureInfo.InvariantCulture, $"{context.Phase.Name}  (id {context.Phase.Id.Value})"));
    }

    /// <summary>
    /// Enumerates levels and plan views so that the names used in the brief can
    /// be matched against the names the model actually uses, rather than
    /// assumed to agree.
    /// </summary>
    private static void WriteLevelsAndPlanViews(DiagnosticReport report, Document document)
    {
        report.Section("LEVELS");
        List<Level> levels = new FilteredElementCollector(document)
            .OfClass(typeof(Level))
            .Cast<Level>()
            .OrderBy(l => l.Elevation)
            .ToList();

        report.Item("Level count", levels.Count);
        report.Blank();
        foreach (Level level in levels)
        {
            report.Line(string.Create(CultureInfo.InvariantCulture, $"  {level.Name,-24} elevation {level.Elevation,10:0.####} ft   id {level.Id.Value}"));
        }

        report.Section("PLAN VIEWS");
        List<ViewPlan> planViews = new FilteredElementCollector(document)
            .OfClass(typeof(ViewPlan))
            .Cast<ViewPlan>()
            .Where(v => !v.IsTemplate)
            .OrderBy(v => v.ViewType.ToString(), StringComparer.Ordinal)
            .ThenBy(v => v.Name, StringComparer.Ordinal)
            .ToList();

        report.Item("Plan view count", planViews.Count);
        report.Blank();
        foreach (ViewPlan view in planViews)
        {
            string levelName = view.GenLevel?.Name ?? "(no level)";
            report.Line(string.Create(CultureInfo.InvariantCulture, $"  {view.ViewType,-18} {view.Name,-34} level {levelName,-16} id {view.Id.Value}"));
        }
    }

    /// <summary>
    /// Counts what is visible in the analysed view, by category.
    ///
    /// The view is used as the filter rather than the level because visibility
    /// is what the user sees and what the brief describes. Element counts per
    /// level would include things hidden or filtered out of this view.
    /// </summary>
    private static void WriteCategoryCensus(DiagnosticReport report, AnalysisContext context)
    {
        report.Section("CATEGORY CENSUS (elements visible in the analysed view)");

        List<Element> elements = new FilteredElementCollector(context.Document, context.View.Id)
            .WhereElementIsNotElementType()
            .ToList();

        report.Item("Total elements in view", elements.Count);
        report.Blank();

        var census = new Dictionary<string, long>(StringComparer.Ordinal);
        long noCategory = 0;
        foreach (Element element in elements)
        {
            Category? category = element.Category;
            if (category is null)
            {
                noCategory++;
                continue;
            }

            census.TryGetValue(category.Name, out long current);
            census[category.Name] = current + 1;
        }

        report.Census(census);
        report.Blank();
        report.Item("(elements with no category)", noCategory);
    }

    private static void WriteRooms(DiagnosticReport report, AnalysisContext context)
    {
        report.Section("EXISTING ROOMS");

        List<Room> allRooms = new FilteredElementCollector(context.Document)
            .OfCategory(BuiltInCategory.OST_Rooms)
            .WhereElementIsNotElementType()
            .OfType<Room>()
            .ToList();

        List<Room> onLevel = allRooms.Where(r => r.LevelId == context.Level.Id).ToList();

        // A room with zero area is present in the project but not placed in the
        // model. It contributes no boundary and must not be counted as a space.
        List<Room> placed = onLevel.Where(r => r.Area > 0).ToList();
        List<Room> unplaced = onLevel.Where(r => r.Area <= 0).ToList();

        report.Item("Rooms in document", allRooms.Count);
        report.Item("Rooms on this level", onLevel.Count);
        report.Item("  placed", placed.Count);
        report.Item("  unplaced (zero area)", unplaced.Count);
        report.Blank();
        report.Line("  Placed rooms on this level:");
        foreach (Room room in placed.OrderBy(r => r.Number, StringComparer.Ordinal).Take(SampleLimit))
        {
            double squareMetres = UnitUtils.ConvertFromInternalUnits(room.Area, UnitTypeId.SquareMeters);
            report.Line(string.Create(CultureInfo.InvariantCulture, $"    {room.Number,-8} {room.Name,-32} {squareMetres,9:0.##} m2   id {room.Id.Value}"));
        }

        if (placed.Count > SampleLimit)
        {
            report.Line(string.Create(CultureInfo.InvariantCulture, $"    ... and {placed.Count - SampleLimit} more"));
        }
    }

    private static void WriteRoomSeparationLines(DiagnosticReport report, AnalysisContext context)
    {
        report.Section("ROOM SEPARATION LINES");

        int inDocument = new FilteredElementCollector(context.Document)
            .OfCategory(BuiltInCategory.OST_RoomSeparationLines)
            .WhereElementIsNotElementType()
            .GetElementCount();

        int inView = new FilteredElementCollector(context.Document, context.View.Id)
            .OfCategory(BuiltInCategory.OST_RoomSeparationLines)
            .WhereElementIsNotElementType()
            .GetElementCount();

        report.Item("In document", inDocument);
        report.Item("Visible in analysed view", inView);
        report.Blank();
        report.Line("  These bound Revit rooms without being physical elements. Whether they");
        report.Line("  qualify as boundaries for this project is a decision the brief has to");
        report.Line("  settle, not something to infer from the count.");
    }

    private static void WriteLinks(DiagnosticReport report, Document document)
    {
        report.Section("REVIT LINKS");

        List<RevitLinkInstance> links = new FilteredElementCollector(document)
            .OfClass(typeof(RevitLinkInstance))
            .Cast<RevitLinkInstance>()
            .ToList();

        report.Item("Link instances", links.Count);
        report.Blank();
        foreach (RevitLinkInstance link in links.Take(SampleLimit))
        {
            Document? linked = link.GetLinkDocument();
            string state = linked is null ? "NOT LOADED" : "loaded";
            report.Line(string.Create(CultureInfo.InvariantCulture, $"  {link.Name,-52} {state}   id {link.Id.Value}"));
        }

        if (links.Count == 0)
        {
            report.Line("  None. Boundary geometry sourced from links is therefore not a");
            report.Line("  concern for this model, though the design must still not assume so.");
        }
    }

    /// <summary>
    /// Reports how well Revit's own door-to-room association is populated.
    ///
    /// This is reference information, not the adjacency answer. FromRoom and
    /// ToRoom are phase dependent and reflect Revit's room placement rather
    /// than our own spatial reasoning, so they are recorded here to be compared
    /// against, not adopted.
    /// </summary>
    private static void WriteDoors(DiagnosticReport report, AnalysisContext context)
    {
        report.Section("DOORS");

        List<FamilyInstance> doors = new FilteredElementCollector(context.Document, context.View.Id)
            .OfCategory(BuiltInCategory.OST_Doors)
            .WhereElementIsNotElementType()
            .OfType<FamilyInstance>()
            .ToList();

        report.Item("Doors visible in view", doors.Count);

        int both = 0, fromOnly = 0, toOnly = 0, neither = 0;
        var samples = new List<string>();

        foreach (FamilyInstance door in doors)
        {
            Room? from = door.get_FromRoom(context.Phase);
            Room? to = door.get_ToRoom(context.Phase);

            if (from is not null && to is not null) both++;
            else if (from is not null) fromOnly++;
            else if (to is not null) toOnly++;
            else neither++;

            if (samples.Count < SampleLimit)
            {
                samples.Add(string.Create(CultureInfo.InvariantCulture, $"    id {door.Id.Value,-10} from {Describe(from),-28} to {Describe(to),-28}"));
            }
        }

        report.Blank();
        report.Item("  both FromRoom and ToRoom", both);
        report.Item("  FromRoom only", fromOnly);
        report.Item("  ToRoom only", toOnly);
        report.Item("  neither", neither);
        report.Blank();
        report.Line("  Sample (phase-aware lookup):");
        foreach (string sample in samples)
        {
            report.Line(sample);
        }

        static string Describe(Room? room) =>
            room is null ? "(none)" : $"{room.Number} {room.Name}";
    }

    /// <summary>
    /// Records whether Revit's plan topology is available for this level and
    /// phase, and what it reports.
    ///
    /// This is the observation the next phase's decision rests on: whether
    /// PlanCircuits correspond closely enough to the spaces the brief expects
    /// to serve as candidate regions. Nothing is concluded here - the numbers
    /// are recorded so the comparison can be made against the drawing.
    /// </summary>
    private static void WritePlanTopology(DiagnosticReport report, AnalysisContext context)
    {
        report.Section("PLAN TOPOLOGY");

        PlanTopology topology;
        try
        {
            topology = context.Document.get_PlanTopology(context.Level, context.Phase);
        }
        catch (Exception exception)
        {
            report.Item("Available", "NO");
            report.Item("Failure", exception.GetType().Name);
            report.Line($"  {exception.Message}");
            return;
        }

        report.Item("Available", "yes");
        report.Item("Topology level", topology.Level?.Name);
        report.Item("Topology phase", topology.Phase?.Name);
        report.Item("Rooms known to topology", topology.GetRoomIds().Count);

        var circuits = new List<PlanCircuit>();
        foreach (PlanCircuit circuit in topology.Circuits)
        {
            circuits.Add(circuit);
        }

        report.Item("Plan circuits", circuits.Count);
        report.Item("  room located", circuits.Count(c => c.IsRoomLocated));
        report.Item("  not room located", circuits.Count(c => !c.IsRoomLocated));
        report.Blank();
        report.Line("  index      area (m2)   sides   room located   point inside (u, v)");

        int index = 0;
        foreach (PlanCircuit circuit in circuits.OrderByDescending(c => c.Area))
        {
            double squareMetres = UnitUtils.ConvertFromInternalUnits(circuit.Area, UnitTypeId.SquareMeters);

            // GetPointInside returns a UV: a point on the plan, not in space.
            // Lifting it to 3D requires the level elevation, which matters for
            // any later containment test.
            UV point = circuit.GetPointInside();

            report.Line(string.Create(CultureInfo.InvariantCulture, $"  {index,-8} {squareMetres,10:0.##}   {circuit.SideNum,5}   {circuit.IsRoomLocated,-12}   ({point.U:0.##}, {point.V:0.##})"));
            index++;
        }

        report.Blank();
        report.Line("  SideNum is Revit's own count for the circuit and must not be read as");
        report.Line("  the number of boundary curves the room would produce.");
    }
}
