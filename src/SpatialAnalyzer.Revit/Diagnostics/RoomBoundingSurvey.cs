using System.Globalization;
using Autodesk.Revit.DB;
using SpatialAnalyzer.Core.Diagnostics;
using SpatialAnalyzer.Revit.Context;

namespace SpatialAnalyzer.Revit.Diagnostics;

/// <summary>
/// Finds the walls Revit is told to ignore when working out rooms, and measures
/// what difference they would make.
///
/// A wall carries a Room Bounding flag. With it off, Revit's plan topology walks
/// straight past the wall, so a space divided by such walls is reported as one
/// region and every room inside it is invisible to this analysis. That is not a
/// fault in the analysis: it is doing exactly what the model tells it.
///
/// Nor is the flag usually a mistake. It is switched off deliberately for
/// partitions that should not divide a space - a shelf modelled as a wall, a
/// glazed screen, an area meant to read as one room. So this reports and
/// measures; it never decides. Whether a particular wall really divides two
/// rooms is a question about the building, and the person looking at the plan is
/// the one who can answer it.
///
/// The measurement is made by switching the flag on, asking Revit for the plan
/// topology again, and rolling the whole thing back. The model is left exactly
/// as it was found, and the report says whether that held.
/// </summary>
public static class RoomBoundingSurvey
{
    private sealed record WallInfo(
        long Id,
        string TypeName,
        string Kind,
        double LengthFeet,
        double WidthFeet,
        bool CanBeChanged,
        string LevelName,
        string? GroupName);

    public sealed record Survey(
        DiagnosticReport Report,
        IReadOnlyList<long> NotBoundingWallIds,
        int CircuitsBefore,
        int CircuitsAfter,
        int Testable,
        int InGroups,
        bool ModelUnchanged);

    /// <summary>
    /// Catches Revit's complaints instead of letting them interrupt with a
    /// dialog.
    ///
    /// A wall inside a group cannot be changed outside group edit mode, and
    /// Revit says so with an error that cannot be dismissed. This survey avoids
    /// causing that by leaving grouped walls alone, so this exists as a net: if
    /// something else objects, the change is abandoned quietly and recorded,
    /// rather than a modal dialog appearing in the middle of a read-only
    /// diagnostic.
    /// </summary>
    private sealed class RecordFailuresQuietly : IFailuresPreprocessor
    {
        public List<string> Messages { get; } = new();

        public FailureProcessingResult PreprocessFailures(FailuresAccessor failures)
        {
            foreach (FailureMessageAccessor failure in failures.GetFailureMessages())
            {
                try
                {
                    Messages.Add(failure.GetDescriptionText());
                }
                catch (Exception)
                {
                    Messages.Add("(a failure whose description could not be read)");
                }
            }

            failures.DeleteAllWarnings();

            // Everything here is rolled back regardless, so abandoning on an
            // error costs nothing and spares the user a dialog they cannot act
            // on during a command that changes nothing.
            return failures.GetSeverity() == FailureSeverity.Error
                ? FailureProcessingResult.ProceedWithRollBack
                : FailureProcessingResult.Continue;
        }
    }

    public static Survey Run(AnalysisContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        Document document = context.Document;
        var report = new DiagnosticReport("REVIT SPATIAL ANALYZER - ROOM BOUNDING SURVEY");

        report.Section("SESSION");
        report.Item("Document", document.Title);
        report.Item("Path", string.IsNullOrEmpty(document.PathName) ? "(unsaved)" : document.PathName);
        report.Item("View", context.View.Name);
        report.Item("Level", context.Level.Name);
        report.Item("Phase", context.Phase.Name);
        report.Item("Generated", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));

        List<Wall> walls = new FilteredElementCollector(document, context.View.Id)
            .OfCategory(BuiltInCategory.OST_Walls)
            .WhereElementIsNotElementType()
            .OfType<Wall>()
            .ToList();

        var notBounding = new List<WallInfo>();
        int bounding = 0;

        foreach (Wall wall in walls)
        {
            Parameter? flag = wall.get_Parameter(BuiltInParameter.WALL_ATTR_ROOM_BOUNDING);
            if (flag is null)
            {
                continue;
            }

            if (flag.AsInteger() != 0)
            {
                bounding++;
                continue;
            }

            notBounding.Add(Describe(document, wall, flag));
        }

        report.Section("WALLS IN THIS VIEW");
        report.Item("Walls", walls.Count);
        report.Item("  room bounding", bounding);
        report.Item("  NOT room bounding", notBounding.Count);
        report.Blank();
        report.Line("  A wall that is not room bounding is walked straight past when Revit works");
        report.Line("  out rooms, so any space it divides is reported as one region. That is");
        report.Line("  often deliberate - a shelf modelled as a wall, a screen that should not");
        report.Line("  divide a space - and sometimes it is not.");

        List<WallInfo> grouped = notBounding.Where(w => w.GroupName is not null).ToList();
        List<WallInfo> testable = notBounding.Where(w => w.GroupName is null && w.CanBeChanged).ToList();

        report.Item("    of those, inside a group", grouped.Count);
        report.Item("    of those, changeable here", testable.Count);

        int before = 0;
        int after = 0;
        int roomsBefore = CountRooms(document);
        TransactionStatus status = TransactionStatus.Uninitialized;
        var failures = new RecordFailuresQuietly();

        report.Section("WHAT DIFFERENCE THEY WOULD MAKE");

        if (testable.Count > 0)
        {
            using var transaction = new Transaction(document, "Spatial Analyzer room bounding survey");

            FailureHandlingOptions options = transaction.GetFailureHandlingOptions();
            options.SetFailuresPreprocessor(failures);
            options.SetClearAfterRollback(true);
            transaction.SetFailureHandlingOptions(options);

            transaction.Start();
            try
            {
                before = CountCircuits(context);

                foreach (WallInfo info in testable)
                {
                    if (document.GetElement(new ElementId(info.Id)) is Wall wall)
                    {
                        wall.get_Parameter(BuiltInParameter.WALL_ATTR_ROOM_BOUNDING)?.Set(1);
                    }
                }

                // The topology is not recomputed until the model catches up, and
                // this is also where Revit raises anything it objects to.
                document.Regenerate();
                after = CountCircuits(context);

                report.Line("  Measured by switching the flag on, asking Revit for the plan topology");
                report.Line("  again, and rolling the change back. Nothing is kept.");
                report.Blank();
                report.Item("Walls tested", testable.Count);
                report.Item("Regions before", before);
                report.Item("Regions after", after);
                report.Item("Regions that would appear", after - before);
            }
            finally
            {
                status = transaction.RollBack();
            }
        }
        else
        {
            before = CountCircuits(context);
            after = before;
            report.Line("  Nothing could be tested.");
        }

        if (grouped.Count > 0)
        {
            report.Blank();
            report.Line(string.Create(
                CultureInfo.InvariantCulture,
                $"  {grouped.Count} of these walls are inside a model group and were NOT tested."));
            report.Line("  Revit refuses to change a grouped element outside group edit mode, and it");
            report.Line("  refuses at regeneration rather than when the parameter is set - so a");
            report.Line("  grouped wall reports as changed and then is not. Counting them would");
            report.Line("  have produced a number that looked like a measurement and was not.");
            report.Blank();
            report.Line("  The figure above therefore understates the problem. Rooms hidden behind");
            report.Line("  grouped walls are real and are not counted in it.");
            report.Blank();
            report.Line("  Making those walls bound rooms is a change to the model, not something");
            report.Line("  this tool can do for you: open the group, tick Room Bounding on the walls");
            report.Line("  that genuinely divide rooms, and finish the group. Every instance of that");
            report.Line("  group gets the change, which is usually what an apartment layout wants.");
        }

        if (failures.Messages.Count > 0)
        {
            report.Blank();
            report.Line("  Revit objected during the test:");
            foreach (string failure in failures.Messages.Distinct(StringComparer.Ordinal))
            {
                report.Line("    " + failure);
            }
        }

        int roomsAfter = CountRooms(document);
        bool unchanged = testable.Count == 0
            || (status == TransactionStatus.RolledBack && roomsBefore == roomsAfter);

        report.Section("MODEL UNCHANGED");
        report.Item("Transaction status", testable.Count == 0 ? "(not needed)" : status.ToString());
        report.Item("Rooms before", roomsBefore);
        report.Item("Rooms after", roomsAfter);
        report.Item("Model unchanged", unchanged ? "yes" : "NO - INVESTIGATE");

        report.Section("WALLS THAT ARE NOT ROOM BOUNDING");

        if (notBounding.Count == 0)
        {
            report.Line("  none");
        }

        foreach (WallInfo info in notBounding.OrderByDescending(w => w.LengthFeet))
        {
            double lengthMm = UnitUtils.ConvertFromInternalUnits(info.LengthFeet, UnitTypeId.Millimeters);
            double widthMm = UnitUtils.ConvertFromInternalUnits(info.WidthFeet, UnitTypeId.Millimeters);

            string note = info.GroupName is not null
                ? string.Create(CultureInfo.InvariantCulture, $"   IN GROUP \"{info.GroupName}\"")
                : info.CanBeChanged ? string.Empty : "   (flag cannot be changed)";

            report.Line(string.Create(
                CultureInfo.InvariantCulture,
                $"  id {info.Id,-10} {lengthMm,9:0} mm long  {widthMm,6:0} mm thick  {info.Kind,-8} level {info.LevelName,-10} \"{info.TypeName}\"{note}"));
        }

        report.Blank();
        report.Line("  These are selected in the view, so they can be seen on the plan.");

        return new Survey(
            report,
            notBounding.Select(w => w.Id).ToList(),
            before,
            after,
            testable.Count,
            grouped.Count,
            unchanged);
    }

    private static WallInfo Describe(Document document, Wall wall, Parameter flag)
    {
        string typeName = document.GetElement(wall.GetTypeId()) is ElementType type ? type.Name : "(no type)";
        string kind = (document.GetElement(wall.GetTypeId()) as WallType)?.Kind.ToString() ?? "(unknown)";
        double length = (wall.Location as LocationCurve)?.Curve.Length ?? 0;
        string level = document.GetElement(wall.LevelId) is Level l ? l.Name : "(none)";

        // A curtain wall reports no width.
        double width = wall.WallType?.Kind == WallKind.Curtain ? 0 : wall.Width;

        // A wall inside a group cannot be changed outside group edit mode.
        // Revit refuses with an error that cannot be dismissed, and the refusal
        // comes at regeneration rather than when the parameter is set - so a
        // grouped wall would report as changed and then not be. Knowing which
        // ones they are is the difference between a measurement and a guess.
        string? group = null;
        if (wall.GroupId != ElementId.InvalidElementId)
        {
            Element? owner = document.GetElement(wall.GroupId);
            group = owner is null
                ? "(a group)"
                : document.GetElement(owner.GetTypeId()) is ElementType groupType ? groupType.Name : owner.Name;
        }

        return new WallInfo(wall.Id.Value, typeName, kind, length, width, !flag.IsReadOnly, level, group);
    }

    private static int CountCircuits(AnalysisContext context)
    {
        PlanTopology topology = context.Document.get_PlanTopology(context.Level, context.Phase);

        int count = 0;
        foreach (PlanCircuit _ in topology.Circuits)
        {
            count++;
        }

        return count;
    }

    private static int CountRooms(Document document) =>
        new FilteredElementCollector(document)
            .OfCategory(BuiltInCategory.OST_Rooms)
            .WhereElementIsNotElementType()
            .GetElementCount();
}
