using SpatialAnalyzer.Core.Domain;
using SpatialAnalyzer.Core.Geometry;
using SpatialAnalyzer.Core.Spatial;
using Xunit;

namespace SpatialAnalyzer.Core.Tests.Spatial;

public class LoopNestingTests
{
    private static PlanFace Square(double x, double y, double side) =>
        new(
            new[]
            {
                new Point2D(x, y),
                new Point2D(x + side, y),
                new Point2D(x + side, y + side),
                new Point2D(x, y + side),
            },
            AreaMeasurement.Measured(side * side, LoopWinding.CounterClockwise, 0.01, 0),
            new[] { new RevitElementId(1) },
            false,
            Array.Empty<int>(),
            Array.Empty<int>());

    [Fact]
    public void LoopsSideBySideAreBothAtTheSurface()
    {
        Assert.Equal(new[] { 0, 0 }, LoopNesting.DepthOf(new[] { Square(0, 0, 10), Square(20, 0, 10) }));
    }

    /// <summary>
    /// A pod standing free inside a larger space is its own component, so the
    /// walk around the space does not know it is there and both loops come
    /// back. Which one is inside is the only thing that can be said about them.
    /// </summary>
    [Fact]
    public void ALoopInsideAnotherIsOneDeeper()
    {
        Assert.Equal(new[] { 0, 1 }, LoopNesting.DepthOf(new[] { Square(0, 0, 30), Square(10, 10, 5) }));
    }

    [Fact]
    public void NestingCounts()
    {
        Assert.Equal(
            new[] { 0, 1, 2 },
            LoopNesting.DepthOf(new[] { Square(0, 0, 40), Square(5, 5, 20), Square(10, 10, 5) }));
    }

    [Fact]
    public void NoLoopsIsNotAnError()
    {
        Assert.Empty(LoopNesting.DepthOf(Array.Empty<PlanFace>()));
    }
}
