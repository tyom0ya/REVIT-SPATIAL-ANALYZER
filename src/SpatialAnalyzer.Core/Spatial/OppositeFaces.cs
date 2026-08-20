using SpatialAnalyzer.Core.Domain;
using SpatialAnalyzer.Core.Geometry;

namespace SpatialAnalyzer.Core.Spatial;

/// <summary>
/// One upright planar face, as much of it as the pairing needs to know.
/// </summary>
/// <param name="Normal">
/// Which way the face looks, in plan, unit length. Revit points the normal of a
/// face out of the solid it bounds, so the face on the room side of a wall
/// looks into the room.
/// </param>
public sealed record LoopFace(
    RevitElementId Element,
    Point2D At,
    Point2D Normal,
    double ZMinInternalFeet,
    double ZMaxInternalFeet);

/// <param name="Squareness">
/// How exactly the two faces oppose each other: one when they are dead
/// parallel and looking straight at one another, falling towards the tolerance
/// as they splay.
/// </param>
public sealed record FacePair(int A, int B, double Squareness);

/// <summary>
/// Finds the faces that look at each other across a space.
///
/// A closed loop in plan is already good evidence of a room, and it is the loop
/// the mass is built from - this does not supply the shape. What it supplies is
/// a reason to believe the shape. Four walls that enclose a courtyard look, in
/// plan, exactly like four walls that enclose a room; four faces that look at
/// each other in pairs across a space are behaving like the inside of a room,
/// and a stray loop closed by the outsides of unrelated walls is not.
///
/// Three things have to hold, and each of them is here because the other two
/// let something through.
///
/// The normals must be roughly opposite, which is the obvious one.
///
/// The faces must belong to different elements. The two sides of a single wall
/// have exactly opposite normals and would otherwise pair perfectly, reporting
/// the thickness of the wall as a room.
///
/// And each must lie in front of the other rather than behind. Two walls
/// standing back to back - the outside of one against the outside of the next -
/// also have opposite normals, and they enclose nothing whatever. The
/// difference is the sign of the vector between them, which is the only thing
/// that tells a room from the gap behind its wall.
/// </summary>
public static class OppositeFaces
{
    /// <summary>
    /// Every pair of faces in the given set that face each other.
    /// </summary>
    /// <param name="splayDegrees">
    /// How far from dead opposite two faces may look and still be counted. A
    /// room is rarely square to the foot and never square to the micron.
    /// </param>
    /// <param name="leastOverlapFeet">
    /// How much height two faces must share. A skirting and a bulkhead on
    /// opposite sides of a corridor are opposite in plan and never meet in
    /// space, and pairing them would report a room between two things that
    /// cannot see each other.
    /// </param>
    /// <param name="furthestApartFeet">
    /// Beyond this the two faces are taken to be bounding different things that
    /// happen to align. Without it the far side of the building pairs with the
    /// near side across everything in between.
    /// </param>
    public static IReadOnlyList<FacePair> Among(
        IReadOnlyList<LoopFace> faces,
        double splayDegrees,
        double leastOverlapFeet,
        double furthestApartFeet)
    {
        ArgumentNullException.ThrowIfNull(faces);

        if (double.IsNaN(splayDegrees) || splayDegrees < 0 || splayDegrees >= 90)
        {
            throw new ArgumentOutOfRangeException(
                nameof(splayDegrees),
                splayDegrees,
                "The splay must be between zero and ninety degrees; at ninety every face "
                + "opposes every other and the test says nothing.");
        }

        double leastOpposition = Math.Cos(splayDegrees * Math.PI / 180.0);
        var pairs = new List<FacePair>();

        for (int i = 0; i < faces.Count; i++)
        {
            for (int j = i + 1; j < faces.Count; j++)
            {
                LoopFace a = faces[i];
                LoopFace b = faces[j];

                // The two sides of one wall are opposite and enclose only the
                // wall. Rejected on identity rather than on geometry, because
                // geometrically they are a perfect pair.
                if (a.Element == b.Element)
                {
                    continue;
                }

                double opposition = -((a.Normal.X * b.Normal.X) + (a.Normal.Y * b.Normal.Y));
                if (opposition < leastOpposition)
                {
                    continue;
                }

                double overlap =
                    Math.Min(a.ZMaxInternalFeet, b.ZMaxInternalFeet) -
                    Math.Max(a.ZMinInternalFeet, b.ZMinInternalFeet);

                if (overlap < leastOverlapFeet)
                {
                    continue;
                }

                double apart = a.At.DistanceTo(b.At);
                if (apart <= 0 || apart > furthestApartFeet)
                {
                    continue;
                }

                // Does b lie the way a is looking? Walls standing back to back
                // fail here and only here.
                double towards =
                    (((b.At.X - a.At.X) * a.Normal.X) + ((b.At.Y - a.At.Y) * a.Normal.Y)) / apart;

                if (towards < leastOpposition)
                {
                    continue;
                }

                pairs.Add(new FacePair(i, j, Math.Min(opposition, towards)));
            }
        }

        return pairs;
    }

    /// <summary>
    /// How many pairs a loop wants before its shape is taken as corroborated.
    ///
    /// Two, because a room has two of them - one across and one along - and a
    /// single pair is satisfied by any two walls that happen to be parallel.
    /// This is a confidence figure and not a filter: a loop is still a loop
    /// with fewer, and the caller says so rather than dropping it.
    /// </summary>
    public const int EnoughToCorroborate = 2;
}
