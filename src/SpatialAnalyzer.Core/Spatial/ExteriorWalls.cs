using SpatialAnalyzer.Core.Domain;
using SpatialAnalyzer.Core.Geometry;

namespace SpatialAnalyzer.Core.Spatial;

/// <summary>
/// What a wall separates.
/// </summary>
public enum WallExposure
{
    /// <summary>Not decided - nothing usable to test against.</summary>
    Unknown,

    /// <summary>Away from the outline, with structure both sides.</summary>
    Interior,

    /// <summary>On the outline, with the building on one side of it.</summary>
    Exterior,
}

/// <param name="Score">
/// What the wall scored. Kept so a verdict can be argued with rather than only
/// accepted: a wall at four is a different claim from one at five.
/// </param>
public sealed record WallExposureFinding(
    RevitElementId Wall,
    WallExposure Exposure,
    int Score,
    bool OnTheOutline,
    bool DividesInsideFromOutside);

/// <summary>
/// Decides which walls face outside, from the shape the structure occupies.
///
/// The wall's own Function parameter is not consulted. It is set by hand and a
/// curtain wall left as Interior or a party wall marked Exterior are common
/// enough that trusting it reports the model's intentions rather than the
/// building's geometry.
///
/// An earlier attempt asked whether each wall had an enclosed room on one side
/// and none on the other. That is the right question and it failed on a real
/// model, for a reason worth recording: it needs the rooms either side to have
/// closed, and on a working drawing they frequently have not. Facade walls that
/// never quite met the partitions behind them had no room on either side and
/// were reported as standing in open ground, while partitions between two
/// closed rooms were reported as facade. The answer was exact and about the
/// wrong thing.
///
/// This works from the footprint instead, which needs nothing to close. Points
/// are sampled off every wall, column and panel; their outline is taken; and a
/// wall is judged by how much of it lies along that outline and whether it has
/// building on one side. Three tests are scored rather than one relied on,
/// because each has a case it gets wrong on its own.
/// </summary>
public static class ExteriorWalls
{
    /// <summary>Lying along the outline is suggestive but not conclusive.</summary>
    private const int ForBeingOnTheOutline = 1;

    /// <summary>Having building one side and not the other is the strongest signal.</summary>
    private const int ForDividingInsideFromOutside = 2;

    /// <summary>As is having most of its length on the outline rather than a corner touching it.</summary>
    private const int ForRunningAlongIt = 2;

    /// <summary>What it takes to be called exterior.</summary>
    public const int Needed = 4;

    private const int MostSamples = 12;

    /// <param name="nearTheOutlineFeet">
    /// How close a sample must be to the outline to count as on it. Wants to
    /// cover half the thickest wall plus the sampling coarseness, since the
    /// outline is drawn through centre lines and a wall is not a line.
    /// </param>
    /// <param name="stepAsideFeet">
    /// How far to step off the centre line when asking what is either side.
    /// Must clear the wall's own thickness.
    /// </param>
    public static IReadOnlyList<WallExposureFinding> Classify(
        IReadOnlyList<PlanWall> walls,
        BuildingFootprint footprint,
        double nearTheOutlineFeet,
        double stepAsideFeet)
    {
        ArgumentNullException.ThrowIfNull(walls);
        ArgumentNullException.ThrowIfNull(footprint);

        if (double.IsNaN(stepAsideFeet) || stepAsideFeet <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(stepAsideFeet),
                stepAsideFeet,
                "The step aside must be a positive number, or both sides land inside the wall.");
        }

        var findings = new List<WallExposureFinding>(walls.Count);

        foreach (PlanWall wall in walls)
        {
            findings.Add(ClassifyOne(wall, footprint, nearTheOutlineFeet, stepAsideFeet));
        }

        return findings;
    }

    private static WallExposureFinding ClassifyOne(
        PlanWall wall,
        BuildingFootprint footprint,
        double near,
        double stepAside)
    {
        if (!footprint.IsUsable)
        {
            return new WallExposureFinding(wall.Id, WallExposure.Unknown, 0, false, false);
        }

        int onOutline = 0;
        int dividing = 0;
        int tested = 0;

        foreach ((Point2D at, Point2D along) in SamplesOf(wall))
        {
            tested++;

            if (footprint.DistanceToBoundary(at) <= near)
            {
                onOutline++;
            }

            double length = Math.Sqrt((along.X * along.X) + (along.Y * along.Y));
            if (length <= 0)
            {
                continue;
            }

            var oneWay = new Point2D(
                at.X - (along.Y / length * stepAside),
                at.Y + (along.X / length * stepAside));

            var theOther = new Point2D(
                at.X + (along.Y / length * stepAside),
                at.Y - (along.X / length * stepAside));

            if (footprint.Contains(oneWay) != footprint.Contains(theOther))
            {
                dividing++;
            }
        }

        if (tested == 0)
        {
            return new WallExposureFinding(wall.Id, WallExposure.Unknown, 0, false, false);
        }

        bool touchesOutline = onOutline > 0;
        bool mostlyOnOutline = onOutline * 2 >= tested;
        bool separates = dividing * 2 >= tested;

        int score =
            (touchesOutline ? ForBeingOnTheOutline : 0) +
            (mostlyOnOutline ? ForRunningAlongIt : 0) +
            (separates ? ForDividingInsideFromOutside : 0);

        return new WallExposureFinding(
            wall.Id,
            score >= Needed ? WallExposure.Exterior : WallExposure.Interior,
            score,
            touchesOutline,
            separates);
    }

    /// <summary>
    /// Points along the wall, each with the direction it runs there.
    ///
    /// Walked by distance rather than by vertex, so a long straight wall is
    /// sampled along its length instead of only at its two ends, and a curved
    /// one is followed round rather than cut across.
    /// </summary>
    private static IEnumerable<(Point2D At, Point2D Along)> SamplesOf(PlanWall wall)
    {
        IReadOnlyList<Point2D> points = wall.CentreLine.Tessellation;
        int steps = Math.Min(MostSamples, Math.Max(3, points.Count));

        for (int i = 0; i < steps; i++)
        {
            if (TryWalk(points, (i + 0.5) / steps, out Point2D at, out Point2D along))
            {
                yield return (at, along);
            }
        }
    }

    private static bool TryWalk(IReadOnlyList<Point2D> points, double t, out Point2D at, out Point2D along)
    {
        at = default;
        along = default;

        double total = 0;
        for (int i = 0; i < points.Count - 1; i++)
        {
            total += points[i].DistanceTo(points[i + 1]);
        }

        if (total <= 0)
        {
            return false;
        }

        double wanted = total * t;
        double travelled = 0;

        for (int i = 0; i < points.Count - 1; i++)
        {
            double leg = points[i].DistanceTo(points[i + 1]);
            if (leg <= 0)
            {
                continue;
            }

            if (travelled + leg >= wanted)
            {
                double into = (wanted - travelled) / leg;

                at = new Point2D(
                    points[i].X + ((points[i + 1].X - points[i].X) * into),
                    points[i].Y + ((points[i + 1].Y - points[i].Y) * into));

                along = new Point2D(points[i + 1].X - points[i].X, points[i + 1].Y - points[i].Y);
                return true;
            }

            travelled += leg;
        }

        return false;
    }

    /// <summary>
    /// Points along a wall for the cloud the footprint is built from.
    ///
    /// Spaced by distance rather than a fixed count, because a two metre
    /// partition and a thirty metre facade need different numbers of points to
    /// describe them equally well.
    /// </summary>
    public static IEnumerable<Point2D> SampleForCloud(BoundaryCurve centreLine, double everyFeet)
    {
        ArgumentNullException.ThrowIfNull(centreLine);

        IReadOnlyList<Point2D> points = centreLine.Tessellation;

        for (int i = 0; i < points.Count - 1; i++)
        {
            Point2D a = points[i];
            Point2D b = points[i + 1];

            yield return a;

            double leg = a.DistanceTo(b);
            int between = (int)(leg / Math.Max(everyFeet, 0.01));

            for (int s = 1; s <= between; s++)
            {
                double t = (double)s / (between + 1);
                yield return new Point2D(a.X + ((b.X - a.X) * t), a.Y + ((b.Y - a.Y) * t));
            }
        }

        yield return points[^1];
    }
}
