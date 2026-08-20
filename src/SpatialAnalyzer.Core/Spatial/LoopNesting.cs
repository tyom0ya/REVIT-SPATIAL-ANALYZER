using SpatialAnalyzer.Core.Geometry;

namespace SpatialAnalyzer.Core.Spatial;

/// <summary>
/// Works out which loops sit inside which.
///
/// The faces of one connected arrangement never overlap, so on a plan whose
/// walls all touch, every loop comes back at depth nought. Buildings are not
/// like that. A free standing pod in an atrium, a lift core that touches
/// nothing, a detached outbuilding - each is its own component, and the walk
/// that traces the space around one of them does not know it is there. Both
/// loops are returned and one is inside the other.
///
/// The depth is reported and not acted on. Depth one is a shaft inside a room
/// when the thing inside is a lift core, and a courtyard inside a building when
/// the loop around it is the building - and no amount of looking at the plan
/// separates those, because in plan they are the same drawing. What can be said
/// is which contains which, and that is what this says.
/// </summary>
public static class LoopNesting
{
    /// <summary>
    /// For each face, how many of the others contain it.
    /// </summary>
    public static IReadOnlyList<int> DepthOf(IReadOnlyList<PlanFace> faces)
    {
        ArgumentNullException.ThrowIfNull(faces);

        var depths = new int[faces.Count];
        var inside = new Point2D?[faces.Count];

        for (int i = 0; i < faces.Count; i++)
        {
            inside[i] = PlanFaces.TryFindPointInside(faces[i], out Point2D at) ? at : null;
        }

        for (int i = 0; i < faces.Count; i++)
        {
            // A face with no interior point cannot be placed anywhere, so
            // nothing can be said about what contains it. Left at nought
            // rather than guessed at.
            if (inside[i] is not Point2D at)
            {
                continue;
            }

            for (int j = 0; j < faces.Count; j++)
            {
                // Larger, as well as containing. A face that contains another
                // is drawn without knowing the inner one is there, so its own
                // interior includes everything the inner one covers - which
                // means the containment test alone reports each of a nested
                // pair as being inside the other. Only one of them is bigger.
                if (i != j &&
                    faces[j].Area.InternalSquareFeet > faces[i].Area.InternalSquareFeet &&
                    PlanFaces.Contains(faces[j], at))
                {
                    depths[i]++;
                }
            }
        }

        return depths;
    }
}
