using System.Globalization;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using SpatialAnalyzer.Core.Domain;
using SpatialAnalyzer.Core.Geometry;
using SpatialAnalyzer.Core.Spatial;
using SpatialAnalyzer.Revit.Analysis;
using SpatialAnalyzer.Revit.Context;
using SpatialAnalyzer.Revit.Diagnostics;
using SpatialAnalyzer.Revit.Elements;

namespace SpatialAnalyzer.Revit.Commands;

/// <summary>
/// Pick an element and be told which granular room it is in.
///
/// This is what the whole project is for, and everything it needs has been built
/// and checked separately: the regions, the rule that decides which of them are
/// rooms, what lies on each boundary, what each door connects, and how to tell
/// whether a point is inside a region. This assembles them.
///
/// A door is answered from what it connects rather than from where it stands,
/// because a door stands inside a wall.
///
/// Where an element turns out to be in no room, the region it does fall in is
/// named along with the reason that region was not reported as a room. "No room"
/// on its own is unhelpful, and often the reason is the interesting part.
///
/// The model is read inside a transaction that is rolled back. Nothing is
/// written.
/// </summary>
[Transaction(TransactionMode.Manual)]
public class AnalyzeSelectionCommand : IExternalCommand
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

        // Asked before the model is read, so the user is not left waiting at a
        // prompt with no idea why.
        Reference reference;
        try
        {
            reference = uiDocument!.Selection.PickObject(
                ObjectType.Element,
                "Select an element to place in a room.");
        }
        catch (Autodesk.Revit.Exceptions.OperationCanceledException)
        {
            return Result.Cancelled;
        }

        Element element = context.Document.GetElement(reference);
        ElementDescriptor descriptor = ElementDescriptorFactory.Describe(element);
        Point2D? location = ElementPlanPoint.RepresentativeOf(element);

        // Reuses the analysis if one is held for this view and phase and nothing
        // has changed since. Any committed edit discards it, so a kept answer
        // can never outlive the model it describes.
        PlanAnalysis.Result? analysis = PlanAnalysisCache.TryGet(context);
        bool reused = analysis is not null;

        if (analysis is null)
        {
            try
            {
                analysis = PlanAnalysis.From(context, RegionQualification.Read(context));
            }
            catch (Exception exception)
            {
                message = $"The regions could not be read: {exception.GetType().Name}: {exception.Message}";
                return Result.Failed;
            }

            PlanAnalysisCache.Store(context, analysis);
        }

        var lines = new List<string>
        {
            $"Category:  {descriptor.CategoryName}",
            $"Family:  {descriptor.FamilyName}",
            $"Type:  {descriptor.TypeName}",
            $"Revit id:  {descriptor.Id}",
            string.Empty,
        };

        if (location is null)
        {
            lines.Add("Revit gives this element no position at all, so it cannot be placed.");
            Show(lines);
            return Result.Succeeded;
        }

        RoomMembership membership = analysis.Index.Resolve(descriptor, location.Value);

        lines.AddRange(Describe(
            membership,
            analysis.Index.RoomsBoundedBy(descriptor.Id),
            analysis,
            location.Value));

        lines.Add(string.Empty);
        lines.Add(reused
            ? string.Create(CultureInfo.InvariantCulture, $"Analysis reused ({PlanAnalysisCache.Reuses} time(s); built {PlanAnalysisCache.Builds}).")
            : string.Create(CultureInfo.InvariantCulture, $"Analysis rebuilt: {PlanAnalysisCache.LastMissReason}. (built {PlanAnalysisCache.Builds}, reused {PlanAnalysisCache.Reuses})"));

        Show(lines);
        return Result.Succeeded;
    }

    private static IEnumerable<string> Describe(
        RoomMembership membership,
        IReadOnlyList<RegionId> bounds,
        PlanAnalysis.Result analysis,
        Point2D location)
    {
        IReadOnlyList<(RegionQualification.RegionReading Reading, QualificationOutcome Outcome)> outcomes =
            analysis.Outcomes;
        int roomCount = analysis.Index.Rooms.Count;
        string AreaOf(RegionId id)
        {
            (RegionQualification.RegionReading Reading, QualificationOutcome Outcome) match =
                outcomes.FirstOrDefault(o => o.Reading.Region.Id == id);

            if (match.Reading is null)
            {
                return string.Empty;
            }

            double squareMetres = UnitUtils.ConvertFromInternalUnits(match.Reading.RevitAreaFeet2, UnitTypeId.SquareMeters);
            return string.Create(CultureInfo.InvariantCulture, $"  ({squareMetres:0.##} m2)");
        }

        // A wall or a column is not in a room, it is part of what makes rooms.
        // Where an element encloses rooms, that is the answer worth leading
        // with, and it is the one a containment test cannot give.
        if (bounds.Count > 0 && membership.Kind != MembershipKind.ConnectsRooms)
        {
            yield return bounds.Count == 1
                ? "This element encloses one room:"
                : string.Create(CultureInfo.InvariantCulture, $"This element encloses {bounds.Count} rooms:");

            foreach (RegionId room in bounds)
            {
                yield return string.Create(CultureInfo.InvariantCulture, $"   {room}{AreaOf(room)}");
            }

            yield return string.Empty;
            yield return "It is not in a room itself. A wall or a column is what a room is made of,";
            yield return "and is cut out of every space it bounds.";
            yield return string.Empty;
            yield return string.Create(CultureInfo.InvariantCulture, $"Position:  ({location.X:0.###}, {location.Y:0.###}) ft");
            yield return string.Create(CultureInfo.InvariantCulture, $"Rooms on this level:  {roomCount} of {outcomes.Count} regions");
            yield break;
        }

        switch (membership.Kind)
        {
            case MembershipKind.InOneRoom:
                yield return string.Create(CultureInfo.InvariantCulture, $"In room {membership.Rooms[0]}{AreaOf(membership.Rooms[0])}");
                break;

            case MembershipKind.ConnectsRooms:
                yield return "This is a way through. It belongs to both rooms it connects:";
                foreach (RegionId room in membership.Rooms)
                {
                    yield return string.Create(CultureInfo.InvariantCulture, $"   {room}{AreaOf(room)}");
                }

                break;

            case MembershipKind.OnABoundary:
                yield return "On the boundary of:";
                foreach (RegionId room in membership.Rooms)
                {
                    yield return string.Create(CultureInfo.InvariantCulture, $"   {room}{AreaOf(room)}");
                }

                yield return string.Empty;
                yield return "It sits on a wall rather than inside a room, which is where the answer";
                yield return "is genuinely ambiguous. Reported rather than decided by rounding.";
                break;

            case MembershipKind.InMoreThanOneRoom:
                yield return "Found inside more than one room at once, which cannot be:";
                foreach (RegionId room in membership.Rooms)
                {
                    yield return string.Create(CultureInfo.InvariantCulture, $"   {room}{AreaOf(room)}");
                }

                yield return string.Empty;
                yield return "Rooms do not overlap. Please report this.";
                break;

            default:
                yield return "In no room.";

                // The region it does fall in, and why that region is not a room,
                // is usually the useful part of the answer.
                foreach (string line in ExplainWhyNot(outcomes, location, analysis.Reading))
                {
                    yield return line;
                }

                break;
        }

        yield return string.Empty;
        yield return string.Create(CultureInfo.InvariantCulture, $"Position:  ({location.X:0.###}, {location.Y:0.###}) ft");
        yield return string.Create(CultureInfo.InvariantCulture, $"Rooms on this level:  {roomCount} of {outcomes.Count} regions");
    }

    private static IEnumerable<string> ExplainWhyNot(
        IReadOnlyList<(RegionQualification.RegionReading Reading, QualificationOutcome Outcome)> outcomes,
        Point2D location,
        RegionQualification.Reading reading)
    {
        foreach ((RegionQualification.RegionReading r, QualificationOutcome outcome) in outcomes)
        {
            if (outcome.IsQualified)
            {
                continue;
            }

            if (PlanContainment.Of(r.Region, location, reading.ClosureToleranceFeet) != Containment.Inside)
            {
                continue;
            }

            double squareMetres = UnitUtils.ConvertFromInternalUnits(r.RevitAreaFeet2, UnitTypeId.SquareMeters);

            yield return string.Empty;
            yield return string.Create(
                CultureInfo.InvariantCulture,
                $"It stands in region {r.Region.Id} ({squareMetres:0.##} m2), which was not reported as a room:");
            yield return string.Create(CultureInfo.InvariantCulture, $"   {outcome.Reason} - {outcome.Explanation}");
            yield break;
        }

        yield return string.Empty;
        yield return "It stands outside every region found on this level.";
    }

    private static void Show(List<string> lines)
    {
        var dialog = new TaskDialog("Spatial Analyzer")
        {
            MainInstruction = "Selected element",
            MainContent = string.Join(Environment.NewLine, lines),
        };

        dialog.Show();
    }
}
