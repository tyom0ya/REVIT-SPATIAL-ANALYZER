namespace SpatialAnalyzer.Core.Spatial;

/// <summary>
/// Tells a space from the material around it, by asking which way its bounding
/// faces look.
///
/// This is the difference between a room and a wall, and until it was asked the
/// two were indistinguishable. Feed both faces of every wall into a planar
/// arrangement and the wall encloses its own cross section - a perfectly good
/// closed loop, the right shape, with real faces on every side of it. Nothing
/// about the loop says whether the inside of it is air or brick.
///
/// The faces say. Revit points the normal of a face out of the solid it bounds,
/// so a face on the room side of a wall looks into the room, and the same wall's
/// two long faces both look away from the material between them. A loop whose
/// faces look inward is a space. A loop whose faces look outward is the thing
/// the space is cut from.
///
/// No tolerance is involved and nothing is estimated. Loops are traced
/// anticlockwise, so the inside of one is to the left of every edge, and which
/// side a normal is on is a sign.
///
/// An earlier attempt rejected loops drawn by a single element, on the reasoning
/// that a wall is one element and a room is bounded by several. It was wrong on
/// the acceptance model and the reason is instructive: Revit joins walls, so the
/// ends of a wall's cross section are the faces of its neighbours, and the loop
/// is drawn by three or four elements like any room. Counting elements counts
/// how the model was drawn. This counts where the material is.
/// </summary>
public static class LoopFacing
{
    /// <summary>
    /// How much of a loop's boundary, by length, is drawn by a face looking
    /// into it.
    ///
    /// Weighed by length rather than counted, for the same reason the exterior
    /// wall test weighs by area: an arrangement gives a long side one edge and
    /// a short one several, and counting lets the short sides outvote the long.
    /// </summary>
    /// <param name="faces">
    /// The faces the loop's <c>EdgeFaces</c> index into.
    /// </param>
    public static double InwardShare(PlanFace loop, IReadOnlyList<LoopFace> faces)
    {
        ArgumentNullException.ThrowIfNull(loop);
        ArgumentNullException.ThrowIfNull(faces);

        double inward = 0;
        double measured = 0;

        for (int i = 0; i < loop.Outline.Count && i < loop.EdgeFaces.Count; i++)
        {
            int index = loop.EdgeFaces[i];
            if (index < 0 || index >= faces.Count)
            {
                // An edge drawn from a centre line has no face and no opinion.
                continue;
            }

            Geometry.Point2D from = loop.Outline[i];
            Geometry.Point2D to = loop.Outline[(i + 1) % loop.Outline.Count];

            double alongX = to.X - from.X;
            double alongY = to.Y - from.Y;
            double length = Math.Sqrt((alongX * alongX) + (alongY * alongY));

            if (length <= 0)
            {
                continue;
            }

            // The loop is traced anticlockwise, so its inside is to the left of
            // this edge. Left of a direction is that direction turned a quarter
            // turn: (x, y) becomes (-y, x).
            double leftX = -alongY / length;
            double leftY = alongX / length;

            Geometry.Point2D normal = faces[index].Normal;
            measured += length;

            if ((normal.X * leftX) + (normal.Y * leftY) > 0)
            {
                inward += length;
            }
        }

        return measured > 0 ? inward / measured : 0;
    }

    /// <summary>
    /// How much of a loop must be drawn by inward looking faces before it is a
    /// space rather than material.
    ///
    /// A half, which is a long way from either answer. A room comes out near
    /// one and a wall's cross section near nought; the room bounded by faces
    /// that mostly look in and one stray face that does not is still a room,
    /// and putting the line in the empty middle means neither has to be judged
    /// finely.
    /// </summary>
    public const double EnoughToBeASpace = 0.5;
}
