using Autodesk.Revit.DB;
using SpatialAnalyzer.Core.Domain;
using SpatialAnalyzer.Core.Geometry;
using SpatialAnalyzer.Core.Spatial;

namespace SpatialAnalyzer.Revit.Boundaries;

/// <summary>
/// Reads the upright faces of an element's solid.
///
/// Everything before this judged a wall by its centre line, which says one
/// thing about the wall for its whole height and length. A wall that runs along
/// the facade and then turns into the building has no single answer, and being
/// made to give one is how the wrong walls came back coloured.
///
/// Triangles rather than faces, for both planar and curved walls alike. A
/// curved wall's face is a cylinder whose normal is different at every point,
/// so asking the face for one normal would answer for a point the wall may not
/// even reach. A triangle is flat by definition and its normal is its own.
/// </summary>
public static class WallFaceReader
{
    /// <summary>
    /// Medium detail, deliberately. Fine tessellation multiplies triangles
    /// without changing which way a wall faces, and the classification weighs
    /// faces by area, so more of them buys nothing but time.
    /// </summary>
    private static readonly Options HowToRead = new()
    {
        ComputeReferences = false,
        IncludeNonVisibleObjects = false,
        DetailLevel = ViewDetailLevel.Medium,
    };

    public static List<WallFace> Read(Element element)
    {
        ArgumentNullException.ThrowIfNull(element);

        var faces = new List<WallFace>();
        var id = new RevitElementId(element.Id.Value);

        GeometryElement? geometry = element.get_Geometry(HowToRead);
        if (geometry is null)
        {
            return faces;
        }

        foreach (GeometryObject found in geometry)
        {
            Collect(found, id, faces);
        }

        return faces;
    }

    /// <summary>
    /// Walks whatever Revit hands back. A wall's geometry is usually a solid,
    /// but a stacked or curtain wall arrives as an instance holding others, and
    /// missing those would drop entire facades.
    /// </summary>
    private static void Collect(GeometryObject found, RevitElementId id, List<WallFace> faces)
    {
        switch (found)
        {
            case Solid solid:
                foreach (Face face in solid.Faces)
                {
                    Tessellate(face, id, faces);
                }

                break;

            case GeometryInstance instance:
                foreach (GeometryObject inside in instance.GetInstanceGeometry())
                {
                    Collect(inside, id, faces);
                }

                break;
        }
    }

    private static void Tessellate(Face face, RevitElementId id, List<WallFace> faces)
    {
        Mesh? mesh;
        try
        {
            mesh = face.Triangulate();
        }
        catch (Exception)
        {
            // A face Revit will not tessellate contributes nothing rather than
            // stopping the wall it belongs to.
            return;
        }

        if (mesh is null)
        {
            return;
        }

        for (int i = 0; i < mesh.NumTriangles; i++)
        {
            MeshTriangle triangle = mesh.get_Triangle(i);

            XYZ a = triangle.get_Vertex(0);
            XYZ b = triangle.get_Vertex(1);
            XYZ c = triangle.get_Vertex(2);

            XYZ cross = (b - a).CrossProduct(c - a);
            double twiceArea = cross.GetLength();

            // A degenerate triangle has no direction to report and no area to
            // weigh, so it says nothing either way.
            if (twiceArea <= 0)
            {
                continue;
            }

            XYZ normal = cross.Divide(twiceArea);

            if (!ExteriorFaces.IsUpright(normal.Z))
            {
                continue;
            }

            // Flattened after the uprightness test, not before. The vertical
            // part is what says whether this is a side or a lid, and dropping
            // it first would make every top look like a wall face.
            double flat = Math.Sqrt((normal.X * normal.X) + (normal.Y * normal.Y));
            if (flat <= 0)
            {
                continue;
            }

            faces.Add(new WallFace(
                id,
                new Point2D((a.X + b.X + c.X) / 3.0, (a.Y + b.Y + c.Y) / 3.0),
                new Point2D(normal.X / flat, normal.Y / flat),
                twiceArea / 2.0));
        }
    }
}
