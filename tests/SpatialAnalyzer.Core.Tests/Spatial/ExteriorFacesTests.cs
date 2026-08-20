using SpatialAnalyzer.Core.Domain;
using SpatialAnalyzer.Core.Geometry;
using SpatialAnalyzer.Core.Spatial;
using Xunit;

namespace SpatialAnalyzer.Core.Tests.Spatial;

public class ExteriorFacesTests
{
    private const double StepAside = 1.5;
    private const double Reach = 4.0;

    /// <summary>A forty by thirty box, as a footprint to test faces against.</summary>
    private static BuildingFootprint Box()
    {
        var cloud = new List<Point2D>();

        for (double x = 0; x <= 40; x += 1)
        {
            cloud.Add(new Point2D(x, 0));
            cloud.Add(new Point2D(x, 30));
        }

        for (double y = 0; y <= 30; y += 1)
        {
            cloud.Add(new Point2D(0, y));
            cloud.Add(new Point2D(40, y));
        }

        return BuildingFootprint.Around(cloud, Reach);
    }

    private static WallFace Face(long id, double x, double y, double nx, double ny, double area) =>
        new(new RevitElementId(id), new Point2D(x, y), new Point2D(nx, ny), area);

    [Fact]
    public void AFaceOnTheFacadeIsExterior()
    {
        var verdict = Assert.Single(ExteriorFaces.Classify(
            new[] { Face(1, 20, 0, 0, -1, 100) },
            Box(),
            StepAside));

        Assert.Equal(WallExposure.Exterior, verdict.Exposure);
        Assert.Equal(1.0, verdict.Confidence, 9);
    }

    [Fact]
    public void AFaceInTheMiddleIsInterior()
    {
        var verdict = Assert.Single(ExteriorFaces.Classify(
            new[] { Face(1, 20, 15, 1, 0, 100) },
            Box(),
            StepAside));

        Assert.Equal(WallExposure.Interior, verdict.Exposure);
    }

    /// <summary>
    /// The case a centre line cannot express, and the reason for working from
    /// faces at all: a wall along the facade that turns and runs into the
    /// building is exterior for the part that faces the street.
    /// </summary>
    [Fact]
    public void AWallExteriorAlongPartOfItsRunIsExterior()
    {
        var verdict = Assert.Single(ExteriorFaces.Classify(
            new[]
            {
                Face(1, 10, 0, 0, -1, 60),
                Face(1, 20, 15, 1, 0, 90),
            },
            Box(),
            StepAside));

        Assert.Equal(WallExposure.Exterior, verdict.Exposure);

        // And says how sure it is, which one verdict per wall could not.
        Assert.Equal(60.0 / 150.0, verdict.Confidence, 9);
    }

    /// <summary>
    /// Weighed by area, not counted. Tessellation gives a long facade many
    /// small triangles and a short return few, and counting would let the
    /// return outvote the street.
    /// </summary>
    [Fact]
    public void FacesAreWeighedRatherThanCounted()
    {
        var verdict = Assert.Single(ExteriorFaces.Classify(
            new[]
            {
                Face(1, 10, 0, 0, -1, 400),
                Face(1, 20, 15, 1, 0, 10),
                Face(1, 21, 15, 1, 0, 10),
                Face(1, 22, 15, 1, 0, 10),
            },
            Box(),
            StepAside));

        Assert.Equal(WallExposure.Exterior, verdict.Exposure);
    }

    [Fact]
    public void ConfidenceSaysWhenAWallIsEvenlySplit()
    {
        var verdict = Assert.Single(ExteriorFaces.Classify(
            new[]
            {
                Face(1, 10, 0, 0, -1, 50),
                Face(1, 20, 15, 1, 0, 50),
            },
            Box(),
            StepAside));

        Assert.Equal(0.5, verdict.Confidence, 9);
    }

    [Fact]
    public void TopsAndBottomsAreNotAskedAbout()
    {
        Assert.True(ExteriorFaces.IsUpright(0.0));
        Assert.True(ExteriorFaces.IsUpright(0.2));
        Assert.False(ExteriorFaces.IsUpright(1.0));
        Assert.False(ExteriorFaces.IsUpright(-1.0));
    }

    [Fact]
    public void AStepAsideOfNothingIsRefused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => ExteriorFaces.Classify(new[] { Face(1, 20, 0, 0, -1, 10) }, Box(), 0));
    }

    [Fact]
    public void NoFacesIsNotAnError()
    {
        Assert.Empty(ExteriorFaces.Classify(Array.Empty<WallFace>(), Box(), StepAside));
    }
}
