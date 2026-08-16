using Autodesk.Revit.DB;
using SpatialAnalyzer.Core.Domain;
using SpatialAnalyzer.Core.Spatial;
using SpatialAnalyzer.Revit.Context;
using SpatialAnalyzer.Revit.Elements;

namespace SpatialAnalyzer.Revit.Analysis;

/// <summary>
/// Places every element in the view into the room it belongs to.
///
/// The selection workflow answers this one element at a time. Exporting a whole
/// plan asks it of everything at once, which is the same question and must give
/// the same answer, so the same resolver decides it.
///
/// What is deliberately not here: any notion of an element being "near" a room.
/// An element in no room is reported in no room. Walls, columns and the rest are
/// what rooms are made of rather than things inside them, and are left out of
/// the room contents entirely - they are already named as the boundary.
/// </summary>
public static class ElementPlacement
{
    public sealed record Placement(
        IReadOnlyDictionary<RegionId, IReadOnlyList<ElementDescriptor>> ByRoom,
        int Considered,
        int Placed,
        int InNoRoom,
        int WithoutAPosition,
        IReadOnlyDictionary<string, long> WithoutAPositionByCategory,
        IReadOnlyDictionary<string, long> InNoRoomByCategory);

    /// <summary>
    /// Model categories that describe a room rather than occupy it.
    ///
    /// Kept deliberately short. An earlier version of this was a list of
    /// everything to leave out, which is endless by construction and let
    /// cameras, help-button annotations and this add-in's own detail lines into
    /// the contents of rooms. What excludes those is asking Revit what kind of
    /// thing a category is, not naming them one at a time; this list is only for
    /// the few that are genuinely model geometry and genuinely not contents.
    ///
    /// A wall, a curtain panel, a mullion and a curtain grid line are what a
    /// room is bounded by, and are already reported as its boundary. Revit's own
    /// placed rooms are what this analysis replaces. Levels and grids are
    /// reference, not contents. A model group is a container whose members are
    /// collected separately, so counting the group as well would count them
    /// twice.
    /// </summary>
    private static readonly HashSet<BuiltInCategory> DescribesTheRoomRatherThanOccupiesIt = new()
    {
        BuiltInCategory.OST_Walls,
        BuiltInCategory.OST_Rooms,
        BuiltInCategory.OST_RoomSeparationLines,
        BuiltInCategory.OST_CurtainWallPanels,
        BuiltInCategory.OST_CurtainWallMullions,
        BuiltInCategory.OST_CurtainGrids,
        BuiltInCategory.OST_CurtainGridsWall,
        BuiltInCategory.OST_CurtainGridsSystem,
        BuiltInCategory.OST_CurtainGridsRoof,
        BuiltInCategory.OST_CurtainGridsCurtaSystem,
        BuiltInCategory.OST_Levels,
        BuiltInCategory.OST_Grids,
        BuiltInCategory.OST_IOSModelGroups,

        // Columns bound rooms in the same way walls do - a hundred and eight of
        // the boundary segments on this level come from them - and a room is cut
        // out around each one. They are part of what encloses a space rather
        // than something standing in it.
        BuiltInCategory.OST_Columns,
        BuiltInCategory.OST_StructuralColumns,

        // A camera is the marker for a three dimensional view. Revit classifies
        // it as a model category and it is not view-specific, so neither test
        // above excludes it, and without this it is reported as furniture in
        // whichever room it happens to sit over. It is not in the building at
        // all.
        BuiltInCategory.OST_Cameras,
    };

    /// <summary>
    /// Whether an element is part of the building rather than part of the
    /// drawing.
    ///
    /// Two questions Revit answers directly, in place of guessing at category
    /// names. A category has a kind, and only model categories describe things
    /// that physically exist - which excludes annotation, tags, cameras and
    /// Revit's internal bookkeeping in one stroke. And an element that belongs
    /// to a single view is a drawing element whatever its category says, which
    /// is what catches detail lines, including the ones this add-in draws
    /// itself when outlining regions.
    /// </summary>
    private static bool IsPartOfTheBuilding(Element element)
    {
        Category? category = element.Category;

        if (category is null || category.CategoryType != CategoryType.Model || category.IsTagCategory)
        {
            return false;
        }

        if (element.ViewSpecific)
        {
            return false;
        }

        return !DescribesTheRoomRatherThanOccupiesIt.Contains(category.BuiltInCategory);
    }

    public static Placement Of(AnalysisContext context, SpatialIndex index)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(index);

        var byRoom = new Dictionary<RegionId, List<ElementDescriptor>>();
        var withoutPositionByCategory = new Dictionary<string, long>(StringComparer.Ordinal);
        var inNoRoomByCategory = new Dictionary<string, long>(StringComparer.Ordinal);
        int considered = 0;
        int placed = 0;
        int inNoRoom = 0;
        int withoutAPosition = 0;

        static void Count(Dictionary<string, long> census, Element element)
        {
            string name = element.Category?.Name ?? "(no category)";
            census.TryGetValue(name, out long current);
            census[name] = current + 1;
        }

        // Filtered by the view, because what the user sees in the plan is what
        // the plan's analysis is about. Collecting by level instead would pull
        // in elements hidden or filtered out of this view.
        foreach (Element element in new FilteredElementCollector(context.Document, context.View.Id)
                     .WhereElementIsNotElementType())
        {
            if (!IsPartOfTheBuilding(element))
            {
                continue;
            }

            considered++;

            Core.Geometry.Point2D? location = ElementPlanPoint.RepresentativeOf(element);
            if (location is null)
            {
                withoutAPosition++;
                Count(withoutPositionByCategory, element);
                continue;
            }

            ElementDescriptor descriptor = ElementDescriptorFactory.Describe(element);
            RoomMembership membership = index.Resolve(descriptor, location.Value);

            if (membership.Rooms.Count == 0)
            {
                inNoRoom++;
                Count(inNoRoomByCategory, element);
                continue;
            }

            placed++;

            // A door belongs to both rooms it connects, so it is recorded under
            // each. That is the same answer the selection workflow gives.
            foreach (RegionId room in membership.Rooms)
            {
                if (!byRoom.TryGetValue(room, out List<ElementDescriptor>? contents))
                {
                    contents = new List<ElementDescriptor>();
                    byRoom[room] = contents;
                }

                contents.Add(descriptor);
            }
        }

        return new Placement(
            byRoom.ToDictionary(e => e.Key, e => (IReadOnlyList<ElementDescriptor>)e.Value),
            considered,
            placed,
            inNoRoom,
            withoutAPosition,
            withoutPositionByCategory,
            inNoRoomByCategory);
    }
}
