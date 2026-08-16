using Autodesk.Revit.DB;
using SpatialAnalyzer.Core.Domain;
using SpatialAnalyzer.Core.Spatial;
using SpatialAnalyzer.Revit.Context;

namespace SpatialAnalyzer.Revit.Analysis;

/// <summary>
/// Colours a room on the plan: what encloses it, what is in it, and what lets
/// you through.
///
/// Nothing is drawn. Revit can be told to draw elements that already exist in a
/// different colour in one view, which is what happens here, so the highlight
/// describes the analysis rather than adding to the model. A tool that drew
/// coloured lines around a room would be adding geometry to illustrate geometry,
/// and the copy could disagree with the original.
///
/// The overrides live in the view, so applying them is a change to the document
/// and needs a transaction. What each element looked like before is remembered,
/// so clearing puts back exactly what was there rather than resetting to
/// nothing - a wall someone had already coloured for their own reasons keeps
/// that colour when the highlight is removed.
/// </summary>
public static class RoomHighlight
{
    /// <summary>
    /// The colours the brief asks for: red for what encloses the room, yellow
    /// for what is inside it, green for the ways through.
    /// </summary>
    private static readonly Color Boundary = new(220, 30, 30);

    private static readonly Color Contents = new(230, 200, 0);

    private static readonly Color Ways = new(0, 170, 60);

    private const int EmphasisLineWeight = 6;

    public const string TransactionName = "Spatial Analyzer - highlight room";

    public const string ClearTransactionName = "Spatial Analyzer - clear highlight";

    private sealed record Previous(long ViewId, long ElementId, OverrideGraphicSettings Settings);

    private static readonly List<Previous> Applied = new();

    public sealed record Result(int BoundaryElements, int ContentElements, int Ways, int Restored);

    /// <summary>
    /// Whether anything is currently highlighted, so a command can offer to
    /// clear rather than asking a user to remember.
    /// </summary>
    public static bool AnythingApplied => Applied.Count > 0;

    public static Result Apply(
        AnalysisContext context,
        SpatialIndex index,
        RegionId room,
        IReadOnlyList<ElementDescriptor> contents)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(contents);

        GranularRoom? found = index.Room(room)
            ?? throw new ArgumentException($"There is no room {room} in this analysis.", nameof(room));

        Document document = context.Document;
        int restored;

        var boundary = found.BoundingReferences
            .Select(r => r.ElementId.Value)
            .Distinct()
            .ToList();

        // A way through is coloured as a way through rather than as contents,
        // even though it is in both lists. It is the more specific fact.
        var ways = index.Doors.Touching(room)
            .Select(d => d.Door.Id.Value)
            .Distinct()
            .ToHashSet();

        var inside = contents
            .Select(c => c.Id.Value)
            .Where(id => !ways.Contains(id))
            .Distinct()
            .ToList();

        using (var transaction = new Transaction(document, TransactionName))
        {
            transaction.Start();

            // Clearing first, in the same transaction, so that highlighting a
            // second room is one undo rather than two and never leaves the
            // previous room coloured.
            restored = RestoreWithinTransaction(document);

            Paint(document, context.View, boundary, Boundary);
            Paint(document, context.View, inside, Contents);
            Paint(document, context.View, ways.ToList(), Ways);

            transaction.Commit();
        }

        return new Result(boundary.Count, inside.Count, ways.Count, restored);
    }

    /// <summary>
    /// Puts back what each element looked like before it was highlighted.
    /// </summary>
    public static int Clear(Document document)
    {
        ArgumentNullException.ThrowIfNull(document);

        if (Applied.Count == 0)
        {
            return 0;
        }

        int restored;
        using (var transaction = new Transaction(document, ClearTransactionName))
        {
            transaction.Start();
            restored = RestoreWithinTransaction(document);
            transaction.Commit();
        }

        return restored;
    }

    /// <summary>
    /// Forgets what is held without touching the model.
    ///
    /// Used when the document has gone or has changed underneath: the remembered
    /// settings refer to elements that may no longer exist, and putting them
    /// back would be at best meaningless.
    /// </summary>
    public static void Forget() => Applied.Clear();

    private static int RestoreWithinTransaction(Document document)
    {
        int restored = 0;

        foreach (Previous previous in Applied)
        {
            if (document.GetElement(new ElementId(previous.ViewId)) is not View view)
            {
                continue;
            }

            Element? element = document.GetElement(new ElementId(previous.ElementId));
            if (element is null)
            {
                continue;
            }

            try
            {
                view.SetElementOverrides(new ElementId(previous.ElementId), previous.Settings);
                restored++;
            }
            catch (Exception)
            {
                // An element that can no longer carry an override is not worth
                // failing the whole restore for; the rest still go back.
            }
        }

        Applied.Clear();
        return restored;
    }

    private static void Paint(Document document, View view, IReadOnlyList<long> elementIds, Color colour)
    {
        ElementId solidFill = SolidFillPatternId(document);

        foreach (long id in elementIds)
        {
            var elementId = new ElementId(id);
            if (document.GetElement(elementId) is null)
            {
                continue;
            }

            OverrideGraphicSettings settings;
            try
            {
                // Remembered before anything is changed, so clearing restores
                // what was there rather than wiping settings someone else made.
                Applied.Add(new Previous(view.Id.Value, id, view.GetElementOverrides(elementId)));

                settings = new OverrideGraphicSettings()
                    .SetProjectionLineColor(colour)
                    .SetCutLineColor(colour)
                    .SetProjectionLineWeight(EmphasisLineWeight)
                    .SetCutLineWeight(EmphasisLineWeight);

                if (solidFill != ElementId.InvalidElementId)
                {
                    settings = settings
                        .SetSurfaceForegroundPatternVisible(true)
                        .SetSurfaceForegroundPatternId(solidFill)
                        .SetSurfaceForegroundPatternColor(colour)
                        .SetCutForegroundPatternVisible(true)
                        .SetCutForegroundPatternId(solidFill)
                        .SetCutForegroundPatternColor(colour);
                }

                view.SetElementOverrides(elementId, settings);
            }
            catch (Exception)
            {
                // Not every element accepts an override - some are controlled by
                // their category or by a view template. One that refuses is not
                // a reason to abandon the rest.
            }
        }
    }

    /// <summary>
    /// A solid fill pattern, found by asking each pattern whether it is solid
    /// rather than by looking for one called "&lt;Solid fill&gt;".
    ///
    /// Pattern names are localised, so matching the text would work on an
    /// English Revit and quietly do nothing on any other. Where no solid pattern
    /// exists the highlight falls back to lines alone, which is duller and still
    /// correct.
    /// </summary>
    private static ElementId SolidFillPatternId(Document document)
    {
        foreach (FillPatternElement pattern in new FilteredElementCollector(document)
                     .OfClass(typeof(FillPatternElement))
                     .Cast<FillPatternElement>())
        {
            FillPattern fill = pattern.GetFillPattern();
            if (fill.IsSolidFill && fill.Target == FillPatternTarget.Drafting)
            {
                return pattern.Id;
            }
        }

        return ElementId.InvalidElementId;
    }
}
