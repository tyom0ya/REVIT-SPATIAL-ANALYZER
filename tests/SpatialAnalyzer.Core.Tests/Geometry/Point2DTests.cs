using System.Globalization;
using SpatialAnalyzer.Core.Geometry;

namespace SpatialAnalyzer.Core.Tests.Geometry;

public class Point2DTests
{
    [Fact]
    public void DistanceTo_IsEuclidean()
    {
        Assert.Equal(5.0, new Point2D(0, 0).DistanceTo(new Point2D(3, 4)), precision: 10);
    }

    [Fact]
    public void DistanceTo_IsSymmetric()
    {
        var a = new Point2D(1.5, -2.25);
        var b = new Point2D(-3, 7);

        Assert.Equal(a.DistanceTo(b), b.DistanceTo(a), precision: 12);
    }

    [Fact]
    public void IsWithin_AcceptsPointsInsideTheTolerance()
    {
        var a = new Point2D(0, 0);
        var b = new Point2D(0.0005, 0);

        Assert.True(a.IsWithin(b, 0.001));
    }

    [Fact]
    public void IsWithin_RejectsPointsBeyondTheTolerance()
    {
        var a = new Point2D(0, 0);
        var b = new Point2D(0.002, 0);

        Assert.False(a.IsWithin(b, 0.001));
    }

    [Fact]
    public void IsWithin_TreatsTheToleranceAsInclusive()
    {
        var a = new Point2D(0, 0);
        var b = new Point2D(0.001, 0);

        Assert.True(a.IsWithin(b, 0.001));
    }

    /// <summary>
    /// A negative tolerance is not a stricter comparison, it is a mistake in the
    /// caller. Rejecting it stops a sign error from silently making every
    /// comparison fail.
    /// </summary>
    [Fact]
    public void IsWithin_RejectsANegativeTolerance()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new Point2D(0, 0).IsWithin(new Point2D(1, 1), -0.1));
    }

    /// <summary>
    /// Proximity and identity are kept apart deliberately.
    ///
    /// If nearness made two points equal, they would also have to hash alike,
    /// and a tolerance-based equality is not transitive: three points can each
    /// be within tolerance of the next while the first and last are not. Sets
    /// and dictionaries built on that behave unpredictably. Nearness is a
    /// question callers ask explicitly.
    /// </summary>
    [Fact]
    public void NearnessDoesNotMakePointsEqual()
    {
        var a = new Point2D(0, 0);
        var b = new Point2D(0.0000001, 0);

        Assert.True(a.IsWithin(b, 0.001));
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void IdenticalCoordinatesAreEqualAndHashAlike()
    {
        var a = new Point2D(2.5, -1.25);
        var b = new Point2D(2.5, -1.25);

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Theory]
    [InlineData("de-DE")]
    [InlineData("sv-SE")]
    public void ToString_IsCultureInvariant(string culture)
    {
        CultureInfo original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo(culture);

            string text = new Point2D(1.5, -2.5).ToString();

            Assert.Equal("(1.5, -2.5)", text);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }
}
