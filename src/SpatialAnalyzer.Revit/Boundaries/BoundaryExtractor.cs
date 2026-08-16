using Autodesk.Revit.DB;
using SpatialAnalyzer.Core.Domain;
using SpatialAnalyzer.Core.Geometry;
using CoreBoundarySegment = SpatialAnalyzer.Core.Geometry.BoundarySegment;
using RevitBoundarySegment = Autodesk.Revit.DB.BoundarySegment;

namespace SpatialAnalyzer.Revit.Boundaries;

/// <summary>
/// Copies a room's boundary out of Revit into plain owned data.
///
/// This is the one place Revit boundary geometry crosses into the rest of the
/// application. Everything downstream reasons about the copy, which is what
/// lets the spatial rules - the gap rule above all - be tested in seconds
/// without opening a model.
///
/// The copy is deliberately faithful rather than tidy. Nothing is merged,
/// reordered, straightened or closed on the way through: a boundary that
/// arrives discontinuous leaves discontinuous, and an arc leaves an arc. Any
/// judgement about what those mean is made later, in the open, by code that can
/// be tested.
/// </summary>
public static class BoundaryExtractor
{
    /// <summary>
    /// Extracts every boundary loop of a room.
    ///
    /// All loops are returned, not the first. A room with an interior void
    /// reports the void as a further loop, and the model contains several;
    /// taking only the first would silently fill them in.
    /// </summary>
    public static IReadOnlyList<BoundaryLoop> Extract(
        global::Autodesk.Revit.DB.Architecture.Room room,
        SpatialElementBoundaryOptions options)
    {
        IList<IList<RevitBoundarySegment>> revitLoops = room.GetBoundarySegments(options);

        var loops = new List<BoundaryLoop>(revitLoops.Count);
        foreach (IList<RevitBoundarySegment> revitLoop in revitLoops)
        {
            if (revitLoop.Count == 0)
            {
                // An empty loop carries no geometry and cannot be represented.
                // Skipping it is not the same as hiding a discontinuity: there
                // is nothing here to measure.
                continue;
            }

            var segments = new List<CoreBoundarySegment>(revitLoop.Count);
            foreach (RevitBoundarySegment revitSegment in revitLoop)
            {
                segments.Add(new CoreBoundarySegment(
                    ConvertCurve(revitSegment.GetCurve()),
                    ConvertReference(revitSegment)));
            }

            loops.Add(new BoundaryLoop(segments));
        }

        return loops;
    }

    /// <summary>
    /// Determines what produced a boundary segment.
    ///
    /// The linked case is tested first and on its own terms. When a boundary
    /// comes from a linked model, ElementId holds the link instance placed in
    /// the host and LinkElementId holds the element inside the link that
    /// actually bounds the space. Reading ElementId alone would name the link
    /// instance as the bounding wall, which is wrong in a way that reads
    /// entirely plausibly downstream.
    /// </summary>
    private static BoundaryReference ConvertReference(RevitBoundarySegment segment)
    {
        if (segment.LinkElementId != ElementId.InvalidElementId)
        {
            return BoundaryReference.Linked(
                new RevitElementId(segment.ElementId.Value),
                new RevitElementId(segment.LinkElementId.Value));
        }

        if (segment.ElementId != ElementId.InvalidElementId)
        {
            return BoundaryReference.Host(new RevitElementId(segment.ElementId.Value));
        }

        // Revit produced this segment without attributing it to anything. Short
        // closing stubs at wall ends arrive this way. It is real boundary that
        // no element can be named for, and saying so is more useful than
        // dropping it or attaching it to a neighbour.
        return BoundaryReference.Unattributed();
    }

    internal static BoundaryCurve ConvertCurve(Curve curve)
    {
        BoundaryCurveKind kind = curve switch
        {
            Line => BoundaryCurveKind.Line,
            Arc => BoundaryCurveKind.Arc,
            _ => BoundaryCurveKind.Other,
        };

        // Tessellation comes from Revit rather than being computed here, so no
        // curve mathematics is reimplemented and the points are the ones Revit
        // itself would draw. For a straight segment this is simply its ends.
        IList<XYZ> tessellated = curve.Tessellate();

        var points = new List<Point2D>(Math.Max(tessellated.Count, 2));
        foreach (XYZ point in tessellated)
        {
            points.Add(ToPlan(point));
        }

        // A degenerate tessellation would violate the curve's own invariant.
        // Falling back to the endpoints keeps the extraction honest about the
        // curve's extent rather than throwing away the segment.
        if (points.Count < 2)
        {
            points.Clear();
            points.Add(ToPlan(curve.GetEndPoint(0)));
            points.Add(ToPlan(curve.GetEndPoint(1)));
        }

        return new BoundaryCurve(
            kind,
            ToPlan(curve.GetEndPoint(0)),
            ToPlan(curve.GetEndPoint(1)),
            curve.Length,
            points);
    }

    /// <summary>
    /// Drops the elevation.
    ///
    /// Plan analysis is two dimensional, and the boundary of a room on a level
    /// is a plan figure. Carrying Z would invite comparisons between points at
    /// different heights, which say nothing about whether a space is enclosed.
    /// The elevation belongs to the level and is recorded once, in the analysis
    /// context.
    /// </summary>
    private static Point2D ToPlan(XYZ point) => new(point.X, point.Y);
}
