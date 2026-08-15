using SpatialAnalyzer.Core.Geometry;

namespace SpatialAnalyzer.Core.Tests.Geometry;

public class PlanBoundsTests
{
    [Fact]
    public void ARectangleIsFoundAroundThePoints()
    {
        PlanBounds bounds = PlanBounds.Around(new[]
        {
            new Point2D(3, -1),
            new Point2D(-2, 5),
            new Point2D(1, 1),
        });

        Assert.Equal(-2, bounds.MinX);
        Assert.Equal(-1, bounds.MinY);
        Assert.Equal(3, bounds.MaxX);
        Assert.Equal(5, bounds.MaxY);
        Assert.Equal(5, bounds.Width);
        Assert.Equal(6, bounds.Height);
    }

    [Fact]
    public void ASinglePointBoundsItself()
    {
        PlanBounds bounds = PlanBounds.Around(new[] { new Point2D(2, 3) });

        Assert.Equal(0, bounds.Width);
        Assert.True(bounds.Contains(new Point2D(2, 3), 0));
    }

    [Fact]
    public void PointsInsideAreKeptAndPointsFarOutsideAreNot()
    {
        var bounds = new PlanBounds(0, 0, 10, 8);

        Assert.True(bounds.Contains(new Point2D(5, 4), 0));
        Assert.False(bounds.Contains(new Point2D(50, 4), 0));
        Assert.False(bounds.Contains(new Point2D(5, 40), 0));
    }

    /// <summary>
    /// The margin is the point of the tolerance. A point a hair outside a room
    /// may still be on its boundary, and a rectangle tested exactly would
    /// discard the room before the real test could say so.
    /// </summary>
    [Fact]
    public void TheToleranceGrowsTheRectangleSoNothingIsDiscardedTooEarly()
    {
        var bounds = new PlanBounds(0, 0, 10, 8);
        var justOutside = new Point2D(-0.001, 4);

        Assert.False(bounds.Contains(justOutside, 0));
        Assert.True(bounds.Contains(justOutside, 0.01));
    }

    /// <summary>
    /// It can rule out but never rule in. An L-shaped room's notch is inside its
    /// rectangle and outside the room, so this narrows the question and
    /// something else answers it.
    /// </summary>
    [Fact]
    public void BeingInsideTheRectangleIsNotBeingInsideTheShape()
    {
        // The rectangle around an L, and a point in the missing quarter.
        var bounds = PlanBounds.Around(new[]
        {
            new Point2D(0, 0), new Point2D(10, 0), new Point2D(10, 4),
            new Point2D(4, 4), new Point2D(4, 10), new Point2D(0, 10),
        });

        Assert.True(bounds.Contains(new Point2D(8, 8), 0));
    }

    [Fact]
    public void AnEmptySetHasNoRectangle()
    {
        Assert.Throws<ArgumentException>(() => PlanBounds.Around(Array.Empty<Point2D>()));
    }

    [Fact]
    public void AnInsideOutRectangleIsRejected()
    {
        Assert.Throws<ArgumentException>(() => new PlanBounds(10, 0, 0, 8));
    }

    [Fact]
    public void ANegativeToleranceIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PlanBounds(0, 0, 10, 8).Contains(new Point2D(5, 4), -1));
    }
}
