using System.Globalization;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using SpatialAnalyzer.Core.Domain;
using SpatialAnalyzer.Core.Export;
using SpatialAnalyzer.Core.Geometry;
using SpatialAnalyzer.Core.Spatial;
using SpatialAnalyzer.Revit.Analysis;
using SpatialAnalyzer.Revit.Context;
using SpatialAnalyzer.Revit.Diagnostics;
using SpatialAnalyzer.Revit.Elements;

namespace SpatialAnalyzer.Revit.Commands;

/// <summary>
/// Pick an element and see the room it belongs to, coloured on the plan.
///
/// Red for what encloses the room, yellow for what is inside it, green for the
/// ways through. The same three answers the selection workflow gives in words,
/// shown where a person can check them against the drawing - which is the only
/// way to notice that a room is bounded by a wall nobody expected.
///
/// Nothing is drawn. Revit is told to draw elements that already exist in
/// different colours in this view, so the highlight describes the analysis
/// rather than adding to the model. What each element looked like before is
/// remembered, so clearing puts back exactly what was there.
/// </summary>
[Transaction(TransactionMode.Manual)]
public class HighlightRoomCommand : IExternalCommand
{
    /// <summary>
    /// The original model is never written to by this project's tooling, and
    /// this command commits view settings, so it is refused rather than trusted
    /// to habit.
    /// </summary>
    private const string ProtectedPathFragment = @"\models\pristine\";

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

        if (context.Document.PathName.Contains(ProtectedPathFragment, StringComparison.OrdinalIgnoreCase))
        {
            TaskDialog.Show(
                "Spatial Analyzer",
                "This document is the pristine model, which development tooling must not write to."
                + Environment.NewLine + Environment.NewLine
                + "Open the working copy under models\\dev and run this there.");
            return Result.Cancelled;
        }

        if (RoomHighlight.AnythingApplied && !AskWhetherToContinue(context.Document))
        {
            return Result.Cancelled;
        }

        Reference reference;
        try
        {
            reference = uiDocument!.Selection.PickObject(
                ObjectType.Element,
                "Select an element to highlight the room it belongs to.");
        }
        catch (Autodesk.Revit.Exceptions.OperationCanceledException)
        {
            return Result.Cancelled;
        }

        Element element = WhatWasActuallyClicked(context.Document, reference);
        ElementDescriptor descriptor = ElementDescriptorFactory.Describe(element);
        Point2D? location = ElementPlanPoint.RepresentativeOf(element);

        if (location is null)
        {
            TaskDialog.Show("Spatial Analyzer", "Revit gives this element no position, so it cannot be placed in a room.");
            return Result.Succeeded;
        }

        PlanAnalysis.Result? analysis = PlanAnalysisCache.TryGet(context);
        ElementPlacement.Placement placement;

        try
        {
            if (analysis is null)
            {
                analysis = PlanAnalysis.From(context, RegionQualification.Read(context));
                PlanAnalysisCache.Store(context, analysis);
            }

            placement = ElementPlacement.Of(context, analysis.Index);
        }
        catch (Exception exception)
        {
            message = $"The regions could not be read: {exception.GetType().Name}: {exception.Message}";
            return Result.Failed;
        }

        RoomMembership membership = analysis.Index.Resolve(descriptor, location.Value);

        if (membership.Rooms.Count == 0)
        {
            TaskDialog.Show(
                "Spatial Analyzer",
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{descriptor} is in no room, so there is nothing to highlight."));
            return Result.Succeeded;
        }

        // A door belongs to two rooms. Highlighting both at once would colour
        // one room's boundary in another's contents, so the first is shown and
        // the other named.
        RegionId room = membership.Rooms[0];

        RoomHighlight.Result result;
        try
        {
            result = RoomHighlight.Apply(
                context,
                analysis.Index,
                room,
                placement.ByRoom.TryGetValue(room, out var contents) ? contents : Array.Empty<ElementDescriptor>());
        }
        catch (Exception exception)
        {
            message = $"The highlight did not complete: {exception.GetType().Name}: {exception.Message}";
            return Result.Failed;
        }

        uiDocument.RefreshActiveView();

        var lines = new List<string>
        {
            string.Create(CultureInfo.InvariantCulture, $"Selected:  {descriptor}"),
            string.Empty,
            string.Create(CultureInfo.InvariantCulture, $"Room {room}"),
            string.Create(CultureInfo.InvariantCulture, $"   red     {result.BoundaryElements} element(s) enclosing it"),
            string.Create(CultureInfo.InvariantCulture, $"   yellow  {result.ContentElements} element(s) inside it"),
            string.Create(CultureInfo.InvariantCulture, $"   green   {result.Ways} way(s) through"),
        };

        if (membership.Rooms.Count > 1)
        {
            lines.Add(string.Empty);
            lines.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"This is a way through. It also belongs to {string.Join(", ", membership.Rooms.Skip(1))}, which is not highlighted."));
        }

        lines.Add(string.Empty);
        lines.Add("Press Ctrl+Z once to remove the colouring, or run Clear Highlight.");

        var done = new TaskDialog("Spatial Analyzer")
        {
            MainInstruction = "Room highlighted.",
            MainContent = string.Join(Environment.NewLine, lines),
            CommonButtons = TaskDialogCommonButtons.Close,
        };

        done.AddCommandLink(
            TaskDialogCommandLinkId.CommandLink1,
            string.Create(CultureInfo.InvariantCulture, $"Export room {room} as JSON"),
            "Just this room: what encloses it, what is in it, and the ways through.");

        if (done.Show() != TaskDialogResult.CommandLink1)
        {
            return Result.Succeeded;
        }

        return ExportOneRoom(context, analysis, placement, room, ref message);
    }

    /// <summary>
    /// The element under the cursor, seen past any group it belongs to.
    ///
    /// Revit answers a pick inside a model group with the group itself, and a
    /// group's position is its own origin - which for an apartment core sits
    /// out in the corridor. Taken at face value, clicking a bathroom fixture
    /// highlights the corridor, and the analysis looks wrong while being
    /// perfectly right about the element it was given.
    ///
    /// So the group is opened and its members are asked which of them the
    /// cursor was actually over. Members whose extent covers the clicked point
    /// are the candidates, and the smallest of those wins: a toilet inside a
    /// bathroom inside an apartment is covered by all three extents, and the
    /// one a person meant is the one they could see.
    /// </summary>
    private static Element WhatWasActuallyClicked(Document document, Reference reference)
    {
        Element element = document.GetElement(reference);

        if (reference.GlobalPoint is not XYZ clicked)
        {
            return element;
        }

        // Groups nest, so this descends until it reaches something that is not
        // one. The depth limit is a guard against a malformed model rather than
        // a real expectation; nothing sane nests that far.
        for (int depth = 0; element is Group group && depth < 8; depth++)
        {
            Element? member = SmallestMemberOver(document, group, clicked);
            if (member is null)
            {
                break;
            }

            element = member;
        }

        return element;
    }

    private static Element? SmallestMemberOver(Document document, Group group, XYZ clicked)
    {
        Element? smallest = null;
        double smallestExtent = double.MaxValue;

        foreach (ElementId id in group.GetMemberIds())
        {
            Element? member = document.GetElement(id);
            BoundingBoxXYZ? box = member?.get_BoundingBox(null);

            if (member is null || box is null)
            {
                continue;
            }

            // Compared in plan. A pick lands on a surface at whatever height
            // the view cuts, and holding the click to the element's vertical
            // extent would reject the very things a plan shows best.
            if (clicked.X < box.Min.X || clicked.X > box.Max.X ||
                clicked.Y < box.Min.Y || clicked.Y > box.Max.Y)
            {
                continue;
            }

            double extent = (box.Max.X - box.Min.X) * (box.Max.Y - box.Min.Y);
            if (extent < smallestExtent)
            {
                smallestExtent = extent;
                smallest = member;
            }
        }

        return smallest;
    }

    /// <summary>
    /// Writes a file describing one room.
    ///
    /// The same shape as the whole-plan export, scoped down: only this room,
    /// and only the doors that touch it. The file records that it covers one
    /// room rather than a plan, because its summary counts what it contains and
    /// a reader who could not tell the difference would take a fragment for the
    /// whole building.
    /// </summary>
    private static Result ExportOneRoom(
        AnalysisContext context,
        PlanAnalysis.Result analysis,
        ElementPlacement.Placement placement,
        RegionId room,
        ref string message)
    {
        string path;

        try
        {
            QualificationOutcome outcome = analysis.Outcomes
                .Select(o => o.Outcome)
                .First(o => o.Region.Id == room);

            SpatialExport export = SpatialExport.Build(
                SpatialExport.OneRoom(room),
                context.ToInfo(),
                context.Document.Title,
                string.IsNullOrEmpty(context.Document.PathName) ? "(unsaved)" : context.Document.PathName,
                analysis.Reading.ClosureToleranceFeet,
                new[] { outcome },
                analysis.Index.Doors.Touching(room),
                placement.ByRoom.TryGetValue(room, out var contents)
                    ? new Dictionary<RegionId, IReadOnlyList<ElementDescriptor>> { [room] = contents }
                    : new Dictionary<RegionId, IReadOnlyList<ElementDescriptor>>(),
                DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
                RegionQualification.Describe(analysis.Reading));

            path = DiagnosticFileWriter.WriteText(
                SpatialExportWriter.ToJson(export),
                string.Create(CultureInfo.InvariantCulture, $"room-{room}"),
                "json");
        }
        catch (Exception exception)
        {
            message = $"The room could not be exported: {exception.GetType().Name}: {exception.Message}";
            return Result.Failed;
        }

        var written = new TaskDialog("Spatial Analyzer")
        {
            MainInstruction = string.Create(CultureInfo.InvariantCulture, $"Room {room} exported."),
            MainContent = path,
            CommonButtons = TaskDialogCommonButtons.Close,
        };

        written.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Open the containing folder");

        if (written.Show() == TaskDialogResult.CommandLink1)
        {
            System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{path}\"");
        }

        return Result.Succeeded;
    }

    private static bool AskWhetherToContinue(Document document)
    {
        var dialog = new TaskDialog("Spatial Analyzer")
        {
            MainInstruction = "Another room is already highlighted.",
            MainContent = "Highlighting a different room puts the current one back to how it looked first. "
                        + "Both happen in one step, so a single undo removes the lot.",
            CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.Cancel,
            DefaultButton = TaskDialogResult.Yes,
        };

        return dialog.Show() == TaskDialogResult.Yes;
    }
}

/// <summary>
/// Puts back everything the highlight changed.
///
/// Undo does the same thing; this exists because a user who has done other work
/// since should not have to undo it as well to clear a colour.
/// </summary>
[Transaction(TransactionMode.Manual)]
public class ClearHighlightCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        UIDocument? uiDocument = commandData.Application.ActiveUIDocument;
        if (uiDocument is null)
        {
            TaskDialog.Show("Spatial Analyzer", "No document is open.");
            return Result.Succeeded;
        }

        if (!RoomHighlight.AnythingApplied)
        {
            TaskDialog.Show("Spatial Analyzer", "Nothing is highlighted.");
            return Result.Succeeded;
        }

        int restored;
        try
        {
            restored = RoomHighlight.Clear(uiDocument.Document);
        }
        catch (Exception exception)
        {
            message = $"The highlight could not be cleared: {exception.GetType().Name}: {exception.Message}";
            return Result.Failed;
        }

        uiDocument.RefreshActiveView();

        TaskDialog.Show(
            "Spatial Analyzer",
            string.Create(CultureInfo.InvariantCulture, $"{restored} element(s) put back to how they were."));

        return Result.Succeeded;
    }
}
