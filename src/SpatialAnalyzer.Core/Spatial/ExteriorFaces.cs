using SpatialAnalyzer.Core.Domain;
using SpatialAnalyzer.Core.Geometry;

namespace SpatialAnalyzer.Core.Spatial;

/// <summary>
/// One upright face of an element, reduced to what the question needs: where it
/// is in plan, which way it looks, and how much of the element it accounts for.
/// </summary>
/// <param name="Normal">
/// The outward direction in plan, unit length. A face's own normal rather than
/// a perpendicular guessed from a centre line, which is the point of working
/// from the solid at all.
/// </param>
/// <param name="AreaInternalSquareFeet">
/// Weight. A wall that is exterior across a large face and interior across a
/// sliver is exterior, and counting faces rather than measuring them gets that
/// backwards.
/// </param>
public sealed record WallFace(
    RevitElementId Element,
    Point2D At,
    Point2D Normal,
    double AreaInternalSquareFeet);

/// <param name="Confidence">
/// How much of the element, by area, agreed with the verdict. One means every
/// face said the same thing; a half means it was evenly split and the answer
/// is worth doubting.
/// </param>
public sealed record FaceVerdict(
    RevitElementId Element,
    WallExposure Exposure,
    double Confidence,
    double ExteriorArea,
    double InteriorArea);

/// <summary>
/// Decides what each element faces, from the faces of its own solid.
///
/// The version before this worked from wall centre lines, and a centre line
/// says one thing about a wall for its whole height and length. A wall that
/// stands proud above a podium, or runs along the facade and then turns into
/// the building, has no single answer, and being forced to give one is how the
/// wrong walls came back coloured.
///
/// So each upright face is asked separately - it has its own position and its
/// own outward direction - and the element's verdict is the weight of what its
/// faces said. Weighed by area rather than counted, because tessellation gives
/// a large wall many small triangles and a small return many fewer, and
/// counting would let the returns outvote the facade.
///
/// Horizontal faces are dropped before any of this. The top and bottom of a
/// wall look at the slab above and the slab below, and neither has anything to
/// say about whether the wall faces the weather.
/// </summary>
public static class ExteriorFaces
{
    /// <summary>
    /// How upright a face must be to be asked. A face whose normal is within
    /// this of vertical is a top or a bottom rather than a side.
    /// </summary>
    private const double UprightEnough = 0.35;

    /// <summary>
    /// Keeps the faces worth asking about: the upright ones, with their plan
    /// normal and the area they cover.
    /// </summary>
    /// <param name="normalZ">
    /// The vertical part of the face's normal. Supplied rather than derived
    /// because the caller has the full three dimensional normal and this type
    /// deliberately does not.
    /// </param>
    public static bool IsUpright(double normalZ) => Math.Abs(normalZ) <= UprightEnough;

    public static IReadOnlyList<FaceVerdict> Classify(
        IReadOnlyList<WallFace> faces,
        BuildingFootprint footprint,
        double stepAsideFeet)
    {
        ArgumentNullException.ThrowIfNull(faces);
        ArgumentNullException.ThrowIfNull(footprint);

        if (double.IsNaN(stepAsideFeet) || stepAsideFeet <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(stepAsideFeet),
                stepAsideFeet,
                "The step aside must be a positive number, or both sides land inside the wall.");
        }

        var outside = new Dictionary<RevitElementId, double>();
        var inside = new Dictionary<RevitElementId, double>();

        foreach (WallFace face in faces)
        {
            double weight = face.AreaInternalSquareFeet;
            if (weight <= 0 || !footprint.IsUsable)
            {
                continue;
            }

            // One step out along the face's own normal, one step back. A face
            // of the envelope has the building behind it and open ground in
            // front; a face of a partition has building both ways.
            var ahead = new Point2D(
                face.At.X + (face.Normal.X * stepAsideFeet),
                face.At.Y + (face.Normal.Y * stepAsideFeet));

            var behind = new Point2D(
                face.At.X - (face.Normal.X * stepAsideFeet),
                face.At.Y - (face.Normal.Y * stepAsideFeet));

            bool buildingAhead = footprint.Contains(ahead);
            bool buildingBehind = footprint.Contains(behind);

            Dictionary<RevitElementId, double> into = buildingAhead == buildingBehind ? inside : outside;
            into[face.Element] = into.GetValueOrDefault(face.Element) + weight;
        }

        var verdicts = new List<FaceVerdict>();

        foreach (RevitElementId element in outside.Keys.Concat(inside.Keys).Distinct())
        {
            double out_ = outside.GetValueOrDefault(element);
            double in_ = inside.GetValueOrDefault(element);
            double all = out_ + in_;

            if (all <= 0)
            {
                verdicts.Add(new FaceVerdict(element, WallExposure.Unknown, 0, 0, 0));
                continue;
            }

            // A wall exterior across any real share of itself is a wall
            // somebody can stand outside of, and calling it interior would hide
            // it from anyone asking which walls face the weather or which doors
            // lead out. A third is enough.
            bool exterior = out_ / all >= EnoughOfIt;

            verdicts.Add(new FaceVerdict(
                element,
                exterior ? WallExposure.Exterior : WallExposure.Interior,
                (exterior ? out_ : in_) / all,
                out_,
                in_));
        }

        return verdicts;
    }

    /// <summary>
    /// How much of an element must face outside before the element does.
    ///
    /// Not a half. A wall along the facade that turns and runs twenty feet into
    /// the building is exterior for the part that faces the street, and the
    /// part that does not should not be allowed to outvote it.
    /// </summary>
    public const double EnoughOfIt = 1.0 / 3.0;
}
