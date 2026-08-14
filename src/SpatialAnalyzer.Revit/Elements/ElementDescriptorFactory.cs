using Autodesk.Revit.DB;
using SpatialAnalyzer.Core.Domain;

namespace SpatialAnalyzer.Revit.Elements;

/// <summary>
/// The single place that turns a Revit element into an
/// <see cref="ElementDescriptor"/>.
///
/// Category, family and type are read differently depending on what the element
/// is, and getting that wrong is easy to do quietly. Concentrating it here means
/// there is one behaviour to correct when the Snowdon audit turns up a case
/// this does not handle well, rather than several copies drifting apart.
/// </summary>
public static class ElementDescriptorFactory
{
    public static ElementDescriptor Describe(Element element)
    {
        Document document = element.Document;

        string? categoryName = element.Category?.Name;

        // Not every element is a FamilyInstance, so FamilyName cannot simply be
        // read off the symbol. Walls, floors and roofs are system families whose
        // type carries the family name; annotation and view elements may have no
        // type at all. ElementType covers both loadable and system families,
        // which is why the type is resolved through it rather than by casting to
        // FamilySymbol.
        ElementId typeId = element.GetTypeId();
        var elementType = typeId == ElementId.InvalidElementId
            ? null
            : document.GetElement(typeId) as ElementType;

        string? familyName = elementType?.FamilyName;
        string? typeName = elementType?.Name;

        // An element with no ElementType still has a name of its own, and it is
        // more useful than a placeholder. Levels and grids fall in here.
        if (typeName is null)
        {
            typeName = element.Name;
        }

        return ElementDescriptor.Create(
            new RevitElementId(element.Id.Value),
            categoryName,
            familyName,
            typeName);
    }
}
