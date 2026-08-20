using SpatialAnalyzer.Core.Spatial;
using Xunit;

namespace SpatialAnalyzer.Core.Tests.Spatial;

public class ZBandsTests
{
    private const double Within = 0.16;
    private const double ShortestUsable = 1.64;

    [Fact]
    public void ElevationsWithinTheToleranceBecomeOne()
    {
        double level = Assert.Single(ZBands.Cluster(new[] { 10.0, 10.05, 9.96 }, Within));
        Assert.Equal(10.0033333, level, 5);
    }

    [Fact]
    public void ElevationsFurtherApartThanTheToleranceStayApart()
    {
        Assert.Equal(2, ZBands.Cluster(new[] { 10.0, 10.5 }, Within).Count);
    }

    /// <summary>
    /// The tolerance does not chain. Nought and two tenths are further apart
    /// than the tolerance, so they are two elevations however many values sit
    /// between them - otherwise a row of slabs each near the last becomes one
    /// floor, and the storeys between them vanish.
    /// </summary>
    [Fact]
    public void CloseElevationsDoNotChainIntoOne()
    {
        Assert.Equal(2, ZBands.Cluster(new[] { 0.0, 0.1, 0.2 }, Within).Count);
    }

    [Fact]
    public void EachStoreyBecomesABand()
    {
        IReadOnlyList<ZBand> bands = ZBands.Between(
            new[] { 10.0, 20.0 },
            0.0,
            30.0,
            Within,
            ShortestUsable);

        Assert.Equal(3, bands.Count);
        Assert.Equal(0.0, bands[0].BottomInternalFeet, 9);
        Assert.Equal(10.0, bands[0].TopInternalFeet, 9);
        Assert.Equal(20.0, bands[2].BottomInternalFeet, 9);
        Assert.Equal(30.0, bands[2].TopInternalFeet, 9);
    }

    /// <summary>
    /// A slab modelled as structure plus finish puts two floor tops a few
    /// inches apart. The band between them is the thickness of the slab, not a
    /// storey.
    /// </summary>
    [Fact]
    public void TheThicknessOfASlabIsNotAStorey()
    {
        IReadOnlyList<ZBand> bands = ZBands.Between(
            new[] { 10.0, 10.5 },
            0.0,
            20.0,
            Within,
            ShortestUsable);

        Assert.DoesNotContain(bands, b => b.HeightInternalFeet < ShortestUsable);
    }

    [Fact]
    public void AFloorAboveTheWallsCutsNothing()
    {
        ZBand band = Assert.Single(ZBands.Between(
            new[] { 40.0 },
            0.0,
            10.0,
            Within,
            ShortestUsable));

        Assert.Equal(0.0, band.BottomInternalFeet, 9);
        Assert.Equal(10.0, band.TopInternalFeet, 9);
    }

    [Fact]
    public void WallsWithNoFloorBetweenTheirEndsStillMakeOneBand()
    {
        ZBand band = Assert.Single(ZBands.Between(
            Array.Empty<double>(),
            2.0,
            12.0,
            Within,
            ShortestUsable));

        Assert.Equal(2.0, band.BottomInternalFeet, 9);
        Assert.Equal(12.0, band.TopInternalFeet, 9);
    }

    [Fact]
    public void NothingTallEnoughToStandInMakesNoBand()
    {
        Assert.Empty(ZBands.Between(Array.Empty<double>(), 0.0, 1.0, Within, ShortestUsable));
    }

    [Fact]
    public void AHeightThatDoesNotIncreaseMakesNoBand()
    {
        Assert.Empty(ZBands.Between(new[] { 5.0 }, 10.0, 10.0, Within, ShortestUsable));
        Assert.Empty(ZBands.Between(new[] { 5.0 }, 10.0, 2.0, Within, ShortestUsable));
    }

    [Fact]
    public void ANegativeClusteringDistanceIsRefused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => ZBands.Cluster(new[] { 1.0 }, -1));
    }
}
