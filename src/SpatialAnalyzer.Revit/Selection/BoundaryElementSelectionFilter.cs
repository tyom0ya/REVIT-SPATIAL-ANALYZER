using Autodesk.Revit.DB;
using Autodesk.Revit.UI.Selection;

namespace SpatialAnalyzer.Revit.Selection;

/// <summary>
/// Restricts picking to elements already established to lie on one region's
/// boundary.
///
/// This is what keeps a human decision honest. Being asked which of these things
/// is the way in is a reasonable question; being able to answer with anything at
/// all in the model would let a mistaken pick manufacture a room out of an
/// element that has nothing to do with the space. The question is narrowed to
/// the candidates the geometry actually produced, so the answer settles which
/// one it is and can never introduce one that is not there.
/// </summary>
public sealed class BoundaryElementSelectionFilter : ISelectionFilter
{
    private readonly HashSet<long> _allowed;

    public BoundaryElementSelectionFilter(IEnumerable<long> allowedElementIds)
    {
        ArgumentNullException.ThrowIfNull(allowedElementIds);
        _allowed = new HashSet<long>(allowedElementIds);
    }

    public bool AllowElement(Element element) => _allowed.Contains(element.Id.Value);

    /// <summary>
    /// References to faces and edges are refused. The question is which element
    /// is the entrance, and a pick that resolved to part of one would answer a
    /// different question.
    /// </summary>
    public bool AllowReference(Reference reference, XYZ position) => false;
}
