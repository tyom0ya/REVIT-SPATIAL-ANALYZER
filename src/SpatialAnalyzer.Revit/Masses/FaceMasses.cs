using System.Globalization;
using Autodesk.Revit.DB;
using SpatialAnalyzer.Core.Geometry;
using SpatialAnalyzer.Core.Spatial;
using SpatialAnalyzer.Revit.Boundaries;

namespace SpatialAnalyzer.Revit.Masses;

/// <summary>
/// One space, and everything known about it before anything is written.
/// </summary>
/// <param name="Depth">
/// How many other loops on this storey contain it. Nought is a space opening
/// onto the storey; one is something standing free inside another space, which
/// is a lift core as readily as a courtyard, and the plan cannot say which.
/// </param>
/// <param name="OpposingPairs">
/// How many pairs of its bounding faces look at each other across it. Two or
/// more is a space behaving like a room. Reported, never used to reject: a
/// triangular room has none, and it is still a room.
/// </param>
public sealed record FaceSpace(
    PlanFace Loop,
    ZBand Band,
    double CeilingInternalFeet,
    int Depth,
    int OpposingPairs);

/// <param name="RoomsInsideASpace">
/// How many of the rooms Revit already has fall inside a space found here.
/// This is the measure that matters: a mass that looks right in a view proves
/// nothing, while a room the model states the position of, with no space over
/// it, is this being wrong somewhere specific.
/// </param>
/// <param name="LoopsThatWereMaterial">
/// Loops whose bounding faces look out of them rather than into them: the walls
/// themselves. Feeding both sides of every wall into the arrangement means
/// every wall encloses its own cross section, and that loop is as closed and as
/// real as any room. What tells them apart is which side of its faces the
/// material is on.
/// </param>
public sealed record FaceSurvey(
    IReadOnlyList<FaceSpace> Spaces,
    IReadOnlyList<ZBand> Bands,
    int ElementsRead,
    int UprightFacesRead,
    int CurvedFacesPassedOver,
    int LoopsFound,
    int LoopsThatWereMaterial,
    int LoopsTooSmall,
    int RoomsPlaced,
    int RoomsInsideASpace,
    IReadOnlyList<string> Failures);

/// <summary>
/// Builds a solid for every space the faces of the model enclose.
///
/// The pipeline is the one the research describes, and most of it was already
/// here. Faces are read from the solids, the upright ones are projected to the
/// lines they draw in plan, those lines are split where they cross, the pieces
/// are assembled into a graph and its bounded cells are walked. That last half
/// is the same arrangement code the earlier work uses, unchanged: it never knew
/// what a wall was, so handing it faces instead of centre lines changed what
/// the answer means without changing how it is reached.
///
/// Two things here are genuinely new.
///
/// The first is that the height is cut before the plan is worked out. Flatten a
/// building and a wall on the second floor lies along a wall on the seventh,
/// and the graph joins them into a room that exists on neither. The cuts come
/// from the floor slabs the model contains, so each storey is solved on its own.
///
/// The second is that the loops now know which faces drew them, so a space can
/// be asked whether its bounding faces look at each other - which is what tells
/// the inside of a room from four walls that happen to meet.
///
/// One thing the research asks for is deliberately absent. It proposes bridging
/// unconnected wall ends up to some thirty or fifty millimetres apart, and
/// concedes in the same breath that doing so can invent rooms that do not
/// exist. It can. A forty millimetre gap between two partitions is a drafting
/// slip about as often as it is a real opening, and nothing in the geometry
/// says which. Gaps are measured and left alone here, as everywhere else in
/// this project.
/// </summary>
public static class FaceMasses
{
    /// <summary>
    /// Marks the masses this reads back on the next run, so running twice does
    /// not build everything twice.
    ///
    /// Distinct from the mark the level based masses use, because the two are
    /// answers to different questions and a model may reasonably hold both.
    /// </summary>
    public const string Marker = "Spatial Analyzer Face Mass";

    /// <summary>
    /// The smallest space worth building, in square feet - about half a square
    /// metre. The same figure the room placement uses, so the two commands
    /// agree about what counts as a space.
    /// </summary>
    public const double SmallestSpaceSquareFeet = 5.4;

    /// <summary>How coarsely a space is keyed by position, in feet.</summary>
    private const double KeyPrecisionFeet = 0.1;

    /// <summary>
    /// Floor elevations closer together than this are the same floor said
    /// twice. Fifty millimetres.
    /// </summary>
    private const double SameFloorFeet = 0.164;

    /// <summary>
    /// The shortest storey anyone stands in, in feet - about five hundred
    /// millimetres. Below this the band is the thickness of a slab.
    /// </summary>
    private const double ShortestStoreyFeet = 1.64;

    /// <summary>How far from opposite two faces may look and still pair.</summary>
    private const double SplayDegrees = 10.0;

    /// <summary>How much height a pair of opposing faces must share, in feet.</summary>
    private const double LeastOverlapFeet = 0.65;

    /// <summary>How far apart a pair of opposing faces may stand, in feet.</summary>
    private const double FurthestApartFeet = 160.0;

    /// <summary>
    /// Works out every space, without writing anything.
    ///
    /// Read-only and separate from the building on purpose: the operator is
    /// shown what was found and asked, and a run that is cancelled leaves the
    /// model exactly as it was found.
    /// </summary>
    public static FaceSurvey Survey(FaceReading reading, double toleranceInternalFeet)
    {
        ArgumentNullException.ThrowIfNull(reading);

        IReadOnlyList<ZBand> bands = ZBands.Between(
            reading.FloorTopsInternalFeet,
            reading.LowestInternalFeet,
            reading.HighestInternalFeet,
            SameFloorFeet,
            ShortestStoreyFeet);

        var spaces = new List<FaceSpace>();
        var failures = new List<string>(reading.Failures);
        var covered = new HashSet<int>();

        int found = 0;
        int material = 0;
        int tooSmall = 0;

        foreach (ZBand band in bands)
        {
            // Only the faces that reach into this storey. A face that stops
            // below the band or starts above it draws no wall here, and letting
            // it draw one is the whole reason a flattened building invents
            // rooms.
            List<PlanTrace> here = reading.Traces
                .Where(trace => Reaches(reading.Faces[trace.Face], band))
                .ToList();

            if (here.Count == 0)
            {
                continue;
            }

            PlanSubdivision subdivision;
            try
            {
                subdivision = PlanFaces.FindAmong(here, toleranceInternalFeet);
            }
            catch (Exception exception)
            {
                failures.Add(string.Create(
                    CultureInfo.InvariantCulture,
                    $"band {band.BottomInternalFeet:0.##}-{band.TopInternalFeet:0.##}: "
                    + $"{exception.GetType().Name}: {exception.Message}"));
                continue;
            }

            var keeping = new List<PlanFace>();

            foreach (PlanFace loop in subdivision.Faces)
            {
                found++;

                // Is the inside of this loop air or brick? Every wall encloses
                // its own cross section once both of its faces are in the
                // arrangement, and that loop is as closed and as real as any
                // room. The faces say which is which: Revit points a normal out
                // of the solid it bounds, so a room's faces look in at it and a
                // wall's look out of itself.
                //
                // This replaced a test on how many elements drew the loop,
                // which was wrong on the acceptance model. Revit joins walls,
                // so the ends of a wall's cross section are the faces of its
                // neighbours and the loop is drawn by three or four elements
                // exactly like a room.
                if (LoopFacing.InwardShare(loop, reading.Faces) < LoopFacing.EnoughToBeASpace)
                {
                    material++;
                    continue;
                }

                if (loop.Area.InternalSquareFeet < SmallestSpaceSquareFeet)
                {
                    tooSmall++;
                    continue;
                }

                keeping.Add(loop);
            }

            IReadOnlyList<int> depths = LoopNesting.DepthOf(keeping);
            double ceiling = CeilingOf(band, reading.FloorSoffitsInternalFeet);

            for (int room = 0; room < reading.RoomsPlaced.Count; room++)
            {
                PlacedRoom placed = reading.RoomsPlaced[room];

                if (!Holds(band, placed.ZInternalFeet))
                {
                    continue;
                }

                if (keeping.Any(loop => PlanFaces.Contains(loop, placed.At)))
                {
                    covered.Add(room);
                }
            }

            for (int i = 0; i < keeping.Count; i++)
            {
                spaces.Add(new FaceSpace(
                    keeping[i],
                    band,
                    ceiling,
                    depths[i],
                    OppositeFaces.Among(
                        keeping[i].Faces.Select(f => reading.Faces[f]).ToList(),
                        SplayDegrees,
                        LeastOverlapFeet,
                        FurthestApartFeet).Count));
            }
        }

        return new FaceSurvey(
            spaces.OrderByDescending(s => s.Loop.Area.InternalSquareFeet).ToList(),
            bands,
            reading.ElementsRead,
            reading.Faces.Count,
            reading.CurvedFacesPassedOver,
            found,
            material,
            tooSmall,
            reading.RoomsPlaced.Count,
            covered.Count,
            failures);
    }

    /// <summary>
    /// Whether a storey holds a room standing at this height.
    ///
    /// A room reports its position at the level it belongs to, and a storey is
    /// measured from the top of a slab, so the two differ by whatever the level
    /// datum was set relative to the floor. A foot of slack covers that without
    /// letting a room on one floor be counted against the storey below.
    /// </summary>
    private static bool Holds(ZBand band, double z) =>
        z >= band.BottomInternalFeet - RoomSitsWithinFeet &&
        z < band.TopInternalFeet;

    private const double RoomSitsWithinFeet = 1.0;

    /// <summary>
    /// Whether a face reaches into a storey at all.
    ///
    /// Overlap rather than containment. A wall running two storeys belongs to
    /// both of them, and a wall whose base was constrained to the level below
    /// still bounds the rooms of the level it stands in.
    /// </summary>
    private static bool Reaches(LoopFace face, ZBand band) =>
        face.ZMinInternalFeet < band.TopInternalFeet &&
        face.ZMaxInternalFeet > band.BottomInternalFeet;

    /// <summary>
    /// Where a space in this storey stops: the underside of the slab that forms
    /// its ceiling, or the top of that slab when the model has no underside to
    /// offer.
    ///
    /// A storey is measured between slab tops because that is what you stand
    /// on. A space inside it stops at the soffit, and the difference is the
    /// thickness of a slab - which is the amount by which a mass built to the
    /// level above stands too tall.
    /// </summary>
    private static double CeilingOf(ZBand band, IReadOnlyList<double> soffits)
    {
        double highest = double.MinValue;

        foreach (double soffit in soffits)
        {
            if (soffit > band.BottomInternalFeet &&
                soffit <= band.TopInternalFeet &&
                soffit > highest)
            {
                highest = soffit;
            }
        }

        return highest > double.MinValue ? highest : band.TopInternalFeet;
    }

    /// <param name="RefusedAsTooThinToDraw">
    /// Spaces whose outline collapsed once points closer together than Revit
    /// will accept as a curve were dropped, leaving fewer than three corners.
    /// </param>
    /// <param name="RefusedByRevit">
    /// Spaces Revit itself would not extrude. Kept apart from the count above
    /// because the two mean different things and want different answers: one is
    /// a shape too small to draw, the other a shape Revit objected to, and the
    /// objection is in the details.
    /// </param>
    public sealed record Built(
        int MassesMade,
        int AlreadyStanding,
        int RefusedAsTooThinToDraw,
        int RefusedByRevit,
        int Corroborated,
        IReadOnlyList<string> Failures);

    /// <summary>
    /// Raises a solid in each space. Must be called inside an open transaction
    /// that the caller commits.
    /// </summary>
    public static Built Build(Document document, IReadOnlyList<FaceSpace> spaces)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(spaces);

        HashSet<string> standing = AlreadyMade(document);
        var failures = new List<string>();

        int made = 0;
        int skipped = 0;
        int tooThin = 0;
        int objected = 0;
        int corroborated = 0;

        double shortest = document.Application.ShortCurveTolerance;

        foreach (FaceSpace space in spaces)
        {
            string key = KeyOf(space);

            if (standing.Contains(key))
            {
                skipped++;
                continue;
            }

            try
            {
                if (Raise(document, space, key, shortest) is null)
                {
                    tooThin++;
                    continue;
                }

                made++;

                if (space.OpposingPairs >= OppositeFaces.EnoughToCorroborate)
                {
                    corroborated++;
                }
            }
            catch (Exception exception)
            {
                objected++;
                failures.Add($"space {key}: {exception.GetType().Name}: {exception.Message}");
            }
        }

        return new Built(made, skipped, tooThin, objected, corroborated, failures);
    }

    private static DirectShape? Raise(
        Document document,
        FaceSpace space,
        string key,
        double shortest)
    {
        double floor = space.Band.BottomInternalFeet;
        double height = space.CeilingInternalFeet - floor;

        if (height <= 0)
        {
            return null;
        }

        // Duplicate points are dropped before any curve is made, not curves
        // dropped after. A CurveLoop demands that each curve begin where the
        // last one ended, so skipping a segment because it came out too short
        // leaves a hole and Revit refuses the whole loop.
        var points = new List<XYZ>(space.Loop.Outline.Count);

        foreach (Point2D at in space.Loop.Outline)
        {
            var next = new XYZ(at.X, at.Y, floor);

            if (points.Count == 0 || points[^1].DistanceTo(next) > shortest)
            {
                points.Add(next);
            }
        }

        while (points.Count > 1 && points[^1].DistanceTo(points[0]) <= shortest)
        {
            points.RemoveAt(points.Count - 1);
        }

        if (points.Count < 3)
        {
            return null;
        }

        var loop = new CurveLoop();

        for (int i = 0; i < points.Count; i++)
        {
            loop.Append(Line.CreateBound(points[i], points[(i + 1) % points.Count]));
        }

        Solid solid = GeometryCreationUtilities.CreateExtrusionGeometry(
            new List<CurveLoop> { loop },
            XYZ.BasisZ,
            height);

        DirectShape shape = DirectShape.CreateElement(
            document,
            new ElementId(BuiltInCategory.OST_GenericModel));

        shape.SetShape(new List<GeometryObject> { solid });

        // Written to Comments rather than to the name. A DirectShape does not
        // keep a name set through the API, so the masses of an earlier run read
        // back as nameless and every one of them was built again.
        shape.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.Set(Describe(space, key));

        return shape;
    }

    /// <summary>
    /// What the mass says about itself, in its comments: the mark that finds it
    /// again, then what is known about the space and how well.
    /// </summary>
    private static string Describe(FaceSpace space, string key)
    {
        CultureInfo plain = CultureInfo.InvariantCulture;

        return Marker
            + " " + key
            + " | storey " + space.Band.BottomInternalFeet.ToString("0.##", plain) + "ft"
            + " | " + (space.Loop.Area.InternalSquareFeet * SquareFeetToSquareMetres)
                .ToString("0.0", plain) + " m2"
            + " | " + space.Loop.Walls.Count.ToString(plain) + " elements"
            + " | " + space.OpposingPairs.ToString(plain) + " opposing pair(s)"
            + " | depth " + space.Depth.ToString(plain)
            + (space.OpposingPairs >= OppositeFaces.EnoughToCorroborate
                ? string.Empty
                : " | SHAPE NOT CORROBORATED");
    }

    private const double SquareFeetToSquareMetres = 0.09290304;

    private static HashSet<string> AlreadyMade(Document document)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);

        foreach (Element element in new FilteredElementCollector(document)
                     .OfClass(typeof(DirectShape)))
        {
            string mark = element
                .get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?
                .AsString() ?? string.Empty;

            if (!mark.StartsWith(Marker, StringComparison.Ordinal))
            {
                continue;
            }

            string rest = mark[Marker.Length..];
            int bar = rest.IndexOf('|', StringComparison.Ordinal);

            keys.Add((bar < 0 ? rest : rest[..bar]).Trim());
        }

        return keys;
    }

    /// <summary>
    /// A stable name for a space, from where it is and which storey it is on
    /// rather than from the order it was found in.
    ///
    /// The storey has to be in the key. A lift shaft stands in the same place
    /// on every floor of the building, and keying it by position alone would
    /// build it once and call every storey above already done.
    /// </summary>
    private static string KeyOf(FaceSpace space)
    {
        double x = space.Loop.Outline.Average(p => p.X);
        double y = space.Loop.Outline.Average(p => p.Y);

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{Math.Round(x / KeyPrecisionFeet):0}"
            + $":{Math.Round(y / KeyPrecisionFeet):0}"
            + $":{Math.Round(space.Band.BottomInternalFeet / KeyPrecisionFeet):0}");
    }
}
