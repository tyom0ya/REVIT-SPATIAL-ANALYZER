using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using SpatialAnalyzer.Core.Domain;
using SpatialAnalyzer.Core.Spatial;
using SpatialAnalyzer.Revit.Elements;
using RevitBoundarySegment = Autodesk.Revit.DB.BoundarySegment;

namespace SpatialAnalyzer.Revit.Boundaries;

/// <summary>
/// Finds what is actually set into a space's boundary.
///
/// The distinction this exists to make is between an element contained by a wall
/// that bounds a space, and an element that opens onto that space. They are not
/// the same, and taking the first for the second once credited a four square
/// metre cupboard with eight doors, because a long wall lends its doors to every
/// space it touches.
///
/// So each insert is tested geometrically. Its own position is projected onto
/// the boundary curves that its own host wall produced for this space, and it
/// counts only if it lands on one. The allowance comes from the host wall's
/// measured thickness rather than a chosen number: the boundary runs along the
/// finish face while an insert sits on the wall's centre line, so the two are
/// apart by about half the wall's width.
///
/// An insert that opens between two spaces lands on both their boundaries, and
/// is reported for both. That is correct - a door between two rooms belongs to
/// each of them.
/// </summary>
public static class BoundaryFeatureCollector
{
    /// <summary>
    /// Used when the bounding element is not a wall and has no thickness to
    /// borrow. Generous, and only reached for boundaries that are not walls.
    /// </summary>
    private const double FallbackAllowanceFeet = 1.0;

    public sealed record Found(BoundaryFeature Feature, long HostElementId, double DistanceFeet);

    public static IReadOnlyList<Found> Collect(
        Document document,
        Room room,
        SpatialElementBoundaryOptions options)
    {
        Dictionary<long, List<Curve>> curvesByElement = CollectCurvesByElement(room, options);
        var found = new List<Found>();

        foreach ((long boundingElementId, List<Curve> curves) in curvesByElement)
        {
            Element? bounding = document.GetElement(new ElementId(boundingElementId));
            if (bounding is not HostObject host)
            {
                continue;
            }

            double allowance = bounding is Wall wall ? wall.Width : FallbackAllowanceFeet;

            // Asks for rectangular openings and for embedded walls as well as
            // hosted family instances, because a space may be entered through
            // any of them.
            foreach (ElementId insertId in host.FindInserts(true, false, true, true))
            {
                Element? insert = document.GetElement(insertId);
                if (insert is null)
                {
                    continue;
                }

                if (!TryProjectOntoBoundary(insert, curves, allowance, out double distance))
                {
                    continue;
                }

                found.Add(new Found(
                    new BoundaryFeature(Describe(document, insert), Classify(insert)),
                    boundingElementId,
                    distance));

                // A curtain wall on the boundary may itself contain a door, and
                // that door is what admits a person rather than the wall.
                if (insert is Wall { CurtainGrid: not null } curtainWall)
                {
                    foreach (Found panel in DoorPanelsOf(document, curtainWall, boundingElementId, distance))
                    {
                        found.Add(panel);
                    }
                }
            }
        }

        return found
            .OrderBy(f => f.Feature.Kind)
            .ThenBy(f => f.Feature.Element.Id.Value)
            .ToList();
    }

    private static IEnumerable<Found> DoorPanelsOf(
        Document document,
        Wall curtainWall,
        long boundingElementId,
        double distance)
    {
        foreach (ElementId panelId in curtainWall.CurtainGrid.GetPanelIds())
        {
            Element? panel = document.GetElement(panelId);
            if (panel is null)
            {
                continue;
            }

            BoundaryFeatureKind kind = panel.Category?.BuiltInCategory == BuiltInCategory.OST_Doors
                ? BoundaryFeatureKind.CurtainWallDoorPanel
                : BoundaryFeatureKind.CurtainWallPanel;

            // The panel inherits its curtain wall's distance. The wall has
            // already been shown to lie on this boundary, and a panel is part of
            // it; projecting each panel separately would measure how far along
            // the wall the panel sits, which says nothing about the boundary.
            yield return new Found(new BoundaryFeature(Describe(document, panel), kind), boundingElementId, distance);
        }
    }

    /// <summary>
    /// Tests whether an insert lies on one of the boundary curves its host
    /// produced for this space.
    ///
    /// Several points along the insert are tried rather than one. A door is a
    /// point and its centre is enough, but an embedded wall five metres long
    /// runs along the boundary, and taking only its midpoint would miss one that
    /// meets the space at one end.
    /// </summary>
    private static bool TryProjectOntoBoundary(
        Element insert,
        List<Curve> curves,
        double allowance,
        out double distance)
    {
        distance = double.MaxValue;

        foreach (XYZ point in ElementPlanPoint.AllOn(insert))
        {
            foreach (Curve curve in curves)
            {
                // Compared in plan. The boundary sits at the room's computation
                // height and the insert at its own, and that vertical difference
                // is not evidence of anything.
                var flattened = new XYZ(point.X, point.Y, curve.GetEndPoint(0).Z);

                IntersectionResult? projection = curve.Project(flattened);
                if (projection is not null && projection.Distance < distance)
                {
                    distance = projection.Distance;
                }
            }
        }

        return distance <= allowance;
    }

    /// <summary>
    /// Translates a Revit category into this project's own terms.
    ///
    /// BuiltInCategory is compared rather than the category's name, because
    /// names are localised: matching on the text would make the analysis depend
    /// on the language the model was authored in.
    /// </summary>
    private static BoundaryFeatureKind Classify(Element element) =>
        element.Category?.BuiltInCategory switch
        {
            BuiltInCategory.OST_Doors => BoundaryFeatureKind.Door,
            BuiltInCategory.OST_Windows => BoundaryFeatureKind.Window,
            BuiltInCategory.OST_SpecialityEquipment => BoundaryFeatureKind.SpecialtyEquipment,
            BuiltInCategory.OST_SWallRectOpening => BoundaryFeatureKind.Opening,
            BuiltInCategory.OST_ArcWallRectOpening => BoundaryFeatureKind.Opening,
            BuiltInCategory.OST_ShaftOpening => BoundaryFeatureKind.Opening,
            BuiltInCategory.OST_CurtainWallPanels => BoundaryFeatureKind.CurtainWallPanel,
            BuiltInCategory.OST_Walls => BoundaryFeatureKind.EmbeddedWall,
            _ => BoundaryFeatureKind.Unknown,
        };

    private static ElementDescriptor Describe(Document document, Element element)
    {
        string? familyName = null;
        string? typeName = null;

        if (document.GetElement(element.GetTypeId()) is ElementType type)
        {
            familyName = type.FamilyName;
            typeName = type.Name;
        }

        return ElementDescriptor.Create(
            new RevitElementId(element.Id.Value),
            element.Category?.Name,
            familyName,
            typeName);
    }

    /// <summary>
    /// Groups the space's boundary curves by the element that produced them.
    ///
    /// Segments from linked models and segments Revit attributed to nothing are
    /// left out: neither can host an insert, so neither can contribute a way in.
    /// </summary>
    private static Dictionary<long, List<Curve>> CollectCurvesByElement(
        Room room,
        SpatialElementBoundaryOptions options)
    {
        var byElement = new Dictionary<long, List<Curve>>();

        foreach (IList<RevitBoundarySegment> loop in room.GetBoundarySegments(options))
        {
            foreach (RevitBoundarySegment segment in loop)
            {
                if (segment.LinkElementId != ElementId.InvalidElementId ||
                    segment.ElementId == ElementId.InvalidElementId)
                {
                    continue;
                }

                long elementId = segment.ElementId.Value;
                if (!byElement.TryGetValue(elementId, out List<Curve>? curves))
                {
                    curves = new List<Curve>();
                    byElement[elementId] = curves;
                }

                curves.Add(segment.GetCurve());
            }
        }

        return byElement;
    }
}
