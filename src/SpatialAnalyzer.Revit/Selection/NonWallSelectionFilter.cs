using Autodesk.Revit.DB;
using Autodesk.Revit.UI.Selection;

namespace SpatialAnalyzer.Revit.Selection;

/// <summary>
/// Prevents walls from being picked.
///
/// The rejection happens in the filter rather than after the fact on purpose.
/// A filter makes the wall unpickable, so the cursor refuses it and the user
/// never completes an action that is about to be thrown away; accepting the
/// pick and then complaining teaches the user nothing until after they have
/// committed to it.
///
/// A wall is excluded on two grounds. The class test catches ordinary and
/// curtain walls, which are both <see cref="Wall"/>. The category test also
/// catches things that sit in the Walls category without being that class - an
/// in-place family modelled as a wall being the case that motivates it - which
/// the class test alone would let through.
/// </summary>
public sealed class NonWallSelectionFilter : ISelectionFilter
{
    public bool AllowElement(Element element)
    {
        if (element is Wall)
        {
            return false;
        }

        Category? category = element.Category;
        if (category is null)
        {
            // Elements with no category are not model content worth selecting
            // for spatial analysis.
            return false;
        }

        return category.Id.Value != (long)BuiltInCategory.OST_Walls;
    }

    /// <summary>
    /// Only whole elements are selectable. Allowing sub-element references such
    /// as faces and edges would hand the caller a Reference whose element is
    /// ambiguous for the purpose of "which room is this in".
    /// </summary>
    public bool AllowReference(Reference reference, XYZ position) => false;
}
