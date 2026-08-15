using Autodesk.Revit.DB;
using SpatialAnalyzer.Core.Geometry;

namespace SpatialAnalyzer.Revit.Elements;

/// <summary>
/// Where an element is, in plan.
///
/// One place answers this for the whole project. Two notions of where something
/// stands would eventually disagree, and the disagreement would show up as an
/// element belonging to one room when asked one way and another room when asked
/// another - a difference nobody would think to look for.
///
/// Revit gives an element's position in whichever of three ways suits it. A
/// door or a chair has a point. A wall or a beam has a curve. An opening cut
/// through a wall has neither and is known only by the space it occupies.
/// </summary>
public static class ElementPlanPoint
{
    /// <summary>
    /// Several points along the element, for asking whether any part of it
    /// touches something.
    ///
    /// A door is a point and its centre is enough. A wall five metres long runs
    /// alongside things, and taking only its middle would miss what it meets at
    /// either end.
    /// </summary>
    public static IEnumerable<XYZ> AllOn(Element element)
    {
        ArgumentNullException.ThrowIfNull(element);

        switch (element.Location)
        {
            case LocationPoint point:
                yield return point.Point;
                yield break;

            case LocationCurve locationCurve:
                Curve curve = locationCurve.Curve;
                yield return curve.Evaluate(0.0, true);
                yield return curve.Evaluate(0.5, true);
                yield return curve.Evaluate(1.0, true);
                yield break;
        }

        // The view argument is null deliberately: the model bounding box is
        // wanted, not what some view happens to crop it to.
        BoundingBoxXYZ? box = element.get_BoundingBox(null);
        if (box is not null)
        {
            yield return (box.Min + box.Max) / 2.0;
        }
    }

    /// <summary>
    /// One point standing for the whole element, for asking where it is.
    ///
    /// The middle, whichever way the position was given. Null where Revit offers
    /// nothing at all to go on, which is reported rather than guessed at.
    /// </summary>
    public static Point2D? RepresentativeOf(Element element)
    {
        ArgumentNullException.ThrowIfNull(element);

        switch (element.Location)
        {
            case LocationPoint point:
                return ToPlan(point.Point);

            case LocationCurve locationCurve:
                return ToPlan(locationCurve.Curve.Evaluate(0.5, true));
        }

        BoundingBoxXYZ? box = element.get_BoundingBox(null);
        return box is null ? null : ToPlan((box.Min + box.Max) / 2.0);
    }

    /// <summary>
    /// Drops the elevation. Plan analysis is two dimensional, and the height an
    /// element sits at says nothing about which room it stands in.
    /// </summary>
    public static Point2D ToPlan(XYZ point) => new(point.X, point.Y);
}
