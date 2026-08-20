namespace SpatialAnalyzer.Core.Spatial;

/// <summary>
/// One slice of the building's height, between the top of one floor and the
/// top of the next.
/// </summary>
public sealed record ZBand(double BottomInternalFeet, double TopInternalFeet)
{
    public double HeightInternalFeet => TopInternalFeet - BottomInternalFeet;
}

/// <summary>
/// Cuts a building into the storeys its floor slabs actually make.
///
/// Everything before this worked one level at a time, taking the level from the
/// active view and trusting the view filter to hand back the walls belonging to
/// it. That is true often enough to be dangerous. A view shows what Revit thinks
/// is visible in it, and a wall spanning two storeys, a wall whose base
/// constraint was set to the level below, or a mezzanine appear where the plan
/// does not expect them.
///
/// Flattening the whole building at once is worse. A wall on the second floor
/// and a wall on the seventh can occupy the same line in plan, and a graph built
/// from both would join them into a room that exists on neither storey. Nothing
/// downstream could tell, because by then the height has been thrown away.
///
/// So the height is cut first, at the slabs, and the plan is worked out once
/// inside each slice. The cuts come from the floors the model contains rather
/// than from its levels: a level is a datum somebody named and can sit anywhere,
/// a floor is a thing you stand on.
///
/// Nothing here closes a gap. Elevations within the clustering distance are
/// treated as one because a slab modelled at 3000.0000001 and one modelled at
/// 3000 are the same slab said twice; two elevations further apart than that
/// stay two, however inconvenient the resulting band.
/// </summary>
public static class ZBands
{
    /// <summary>
    /// Groups values that are within <paramref name="within"/> of each other,
    /// and reports each group by its mean.
    ///
    /// Compared against the lowest value in the group, so no group ever spans
    /// more than the tolerance. Comparing against the running mean - or against
    /// the previous value - lets a chain of closely spaced elevations creep a
    /// cluster arbitrarily far from where it began, which would quietly make
    /// the tolerance transitive: three slabs a hundred millimetres apart become
    /// one floor because each is near the last. A tolerance says two things are
    /// the same thing measured twice, and that claim does not chain.
    /// </summary>
    public static IReadOnlyList<double> Cluster(IEnumerable<double> values, double within)
    {
        ArgumentNullException.ThrowIfNull(values);

        if (double.IsNaN(within) || within < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(within),
                within,
                "The clustering distance must be a number and cannot be negative.");
        }

        List<double> sorted = values.Where(v => !double.IsNaN(v)).OrderBy(v => v).ToList();
        if (sorted.Count == 0)
        {
            return Array.Empty<double>();
        }

        var clusters = new List<List<double>> { new() { sorted[0] } };

        foreach (double value in sorted.Skip(1))
        {
            List<double> current = clusters[^1];

            if (value - current[0] <= within)
            {
                current.Add(value);
            }
            else
            {
                clusters.Add(new List<double> { value });
            }
        }

        return clusters.Select(c => c.Average()).ToList();
    }

    /// <summary>
    /// The storeys between the given floor elevations, bounded below and above
    /// by the extent of the walls themselves.
    /// </summary>
    /// <param name="shortestUsableFeet">
    /// How tall a band must be before it counts as a storey. A slab modelled as
    /// two floors - structure and finish - puts two elevations a few inches
    /// apart, and the band between them is the thickness of the slab rather
    /// than a place anyone stands. Dropping it is not closing a gap: the band
    /// is real, it is simply not a storey, and it is reported as dropped.
    /// </param>
    public static IReadOnlyList<ZBand> Between(
        IEnumerable<double> floorTopsInternalFeet,
        double lowestInternalFeet,
        double highestInternalFeet,
        double clusterWithinFeet,
        double shortestUsableFeet)
    {
        ArgumentNullException.ThrowIfNull(floorTopsInternalFeet);

        if (highestInternalFeet <= lowestInternalFeet)
        {
            return Array.Empty<ZBand>();
        }

        // Floors outside the walls' own extent cut nothing. A roof slab far
        // above the tallest wall in the view would otherwise open a band with
        // no walls in it at all.
        var cuts = new List<double> { lowestInternalFeet, highestInternalFeet };

        cuts.AddRange(floorTopsInternalFeet
            .Where(z => z > lowestInternalFeet && z < highestInternalFeet));

        IReadOnlyList<double> levels = Cluster(cuts, clusterWithinFeet);

        var bands = new List<ZBand>();

        for (int i = 0; i < levels.Count - 1; i++)
        {
            var band = new ZBand(levels[i], levels[i + 1]);

            if (band.HeightInternalFeet >= shortestUsableFeet)
            {
                bands.Add(band);
            }
        }

        // Walls tall enough to stand in, but no slab anywhere between their
        // ends. One band covering the lot says so plainly; returning nothing
        // would look like a building with no storeys.
        if (bands.Count == 0 && highestInternalFeet - lowestInternalFeet >= shortestUsableFeet)
        {
            bands.Add(new ZBand(lowestInternalFeet, highestInternalFeet));
        }

        return bands;
    }
}
