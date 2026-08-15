using SpatialAnalyzer.Core.Domain;

namespace SpatialAnalyzer.Core.Geometry;

/// <summary>
/// Where a boundary segment came from.
/// </summary>
public enum BoundarySource
{
    /// <summary>An element in the model being analysed.</summary>
    Host,

    /// <summary>
    /// An element inside a linked model. Two identifiers are involved and
    /// confusing them is easy: the link instance placed in the host, and the
    /// element within the link that actually bounds the space.
    /// </summary>
    Linked,

    /// <summary>
    /// Revit produced the segment without attributing it to any element. Short
    /// closing stubs at wall ends come back this way. Such a segment is real
    /// boundary that no element can be reported for, so it is represented
    /// explicitly rather than discarded or blamed on an arbitrary element.
    /// </summary>
    None,
}

/// <summary>
/// Identifies the element that produced a boundary segment.
///
/// The distinction between a link instance and the element inside it is
/// enforced by construction. Reporting a link instance as the wall that bounds
/// a room is a mistake that reads plausibly in output - a valid id, a real
/// element - and would be very hard to notice later, so this type does not let
/// the two be conflated.
/// </summary>
public sealed record BoundaryReference
{
    private BoundaryReference(BoundarySource source, RevitElementId elementId, RevitElementId linkedElementId)
    {
        Source = source;
        ElementId = elementId;
        LinkedElementId = linkedElementId;
    }

    public BoundarySource Source { get; }

    /// <summary>
    /// For <see cref="BoundarySource.Host"/>, the bounding element itself. For
    /// <see cref="BoundarySource.Linked"/>, the link instance placed in the
    /// host model - which is not the bounding element.
    /// </summary>
    public RevitElementId ElementId { get; }

    /// <summary>
    /// The element inside the linked model that bounds the space. Invalid for
    /// anything other than <see cref="BoundarySource.Linked"/>.
    /// </summary>
    public RevitElementId LinkedElementId { get; }

    /// <summary>
    /// Whether this segment can be reported as a model element at all. Segments
    /// Revit did not attribute cannot, and the export has to say so rather than
    /// omit them.
    /// </summary>
    public bool IsAttributed => Source != BoundarySource.None;

    public static BoundaryReference Host(RevitElementId elementId)
    {
        if (!elementId.IsValid)
        {
            throw new ArgumentException("A host boundary reference needs a valid element id.", nameof(elementId));
        }

        return new BoundaryReference(BoundarySource.Host, elementId, RevitElementId.Invalid);
    }

    public static BoundaryReference Linked(RevitElementId linkInstanceId, RevitElementId linkedElementId)
    {
        if (!linkInstanceId.IsValid)
        {
            throw new ArgumentException("A linked boundary reference needs a valid link instance id.", nameof(linkInstanceId));
        }

        if (!linkedElementId.IsValid)
        {
            throw new ArgumentException("A linked boundary reference needs a valid linked element id.", nameof(linkedElementId));
        }

        return new BoundaryReference(BoundarySource.Linked, linkInstanceId, linkedElementId);
    }

    public static BoundaryReference Unattributed() =>
        new(BoundarySource.None, RevitElementId.Invalid, RevitElementId.Invalid);

    public override string ToString() => Source switch
    {
        BoundarySource.Host => $"host {ElementId}",
        BoundarySource.Linked => $"link {ElementId} element {LinkedElementId}",
        _ => "unattributed",
    };
}
