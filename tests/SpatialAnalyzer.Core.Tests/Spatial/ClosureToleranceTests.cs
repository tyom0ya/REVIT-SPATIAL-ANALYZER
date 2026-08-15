using System.Globalization;
using SpatialAnalyzer.Core.Spatial;

namespace SpatialAnalyzer.Core.Tests.Spatial;

public class ClosureToleranceTests
{
    /// <summary>
    /// The value Revit reported for ShortCurveTolerance on the acceptance
    /// machine, and the one this project uses. Kept here so that a change to
    /// the bound which would reject it fails a test rather than an add-in.
    /// </summary>
    private const double RevitShortCurveTolerance = 1.0 / 384.0;   // 1/32 inch

    private const double RevitVertexTolerance = 0.0005233832795;

    /// <summary>The narrowest doorway the acceptance model contains, near enough.</summary>
    private const double DoorwayWidth = 3.0;

    [Fact]
    public void TheToleranceThisProjectUsesIsAccepted()
    {
        var tolerance = new ClosureTolerance(RevitShortCurveTolerance);

        Assert.Equal(RevitShortCurveTolerance, tolerance.InternalFeet);
    }

    [Fact]
    public void RevitsVertexToleranceIsAlsoWithinTheBound()
    {
        // Not the value chosen, but a defensible one, and the bound must not be
        // so tight that the alternative is unrepresentable.
        Assert.Equal(RevitVertexTolerance, new ClosureTolerance(RevitVertexTolerance).InternalFeet);
    }

    /// <summary>
    /// The whole point of the type.
    ///
    /// A tolerance the width of a doorway would treat an opening between two
    /// spaces as closed, producing a room that looks entirely plausible and is
    /// not in the building. The geometry types will answer that question if
    /// asked, because measuring is not judging; this is the layer that decides,
    /// and it refuses.
    /// </summary>
    [Fact]
    public void ADoorwayWidthIsNotATolerance()
    {
        ArgumentOutOfRangeException error =
            Assert.Throws<ArgumentOutOfRangeException>(() => new ClosureTolerance(DoorwayWidth));

        Assert.Contains("describe a building that does not exist", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheBoundIsFarBelowAnythingThatCouldBeAnOpening()
    {
        // Recorded as a number rather than asserted in prose: the largest
        // tolerance this project will accept is hundreds of times smaller than
        // the narrowest way into a room.
        Assert.True(
            DoorwayWidth / ClosureTolerance.MaximumInternalFeet > 250,
            "the bound must stay far enough below a doorway that no tolerance can be confused with one");
    }

    [Fact]
    public void OneEighthOfAnInchIsAcceptedAndAnythingLargerIsNot()
    {
        Assert.Equal(ClosureTolerance.MaximumInternalFeet, new ClosureTolerance(ClosureTolerance.MaximumInternalFeet).InternalFeet);
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new ClosureTolerance(ClosureTolerance.MaximumInternalFeet * 1.0001));
    }

    [Fact]
    public void TheBoundIsExactlyOneEighthOfAnInch()
    {
        // An eighth of an inch is a ninety-sixth of a foot. Stated as a
        // fraction so it is exact, and checked so that editing the constant to
        // a rounded decimal is caught.
        Assert.Equal(0.125 / 12.0, ClosureTolerance.MaximumInternalFeet, precision: 15);
    }

    [Fact]
    public void RequiringExactCoincidenceIsAllowed()
    {
        Assert.Equal(0, ClosureTolerance.Exact.InternalFeet);
    }

    [Fact]
    public void ANegativeToleranceIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ClosureTolerance(-0.001));
    }

    /// <summary>
    /// A NaN would compare false against every bound and slip through as a
    /// tolerance that nothing is ever within, silently making every region
    /// unenclosed.
    /// </summary>
    [Fact]
    public void ANonNumberIsRejected()
    {
        Assert.Throws<ArgumentException>(() => new ClosureTolerance(double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ClosureTolerance(double.PositiveInfinity));
    }

    [Fact]
    public void TolerancesCompareByLength()
    {
        Assert.True(new ClosureTolerance(0.001) < new ClosureTolerance(0.002));
        Assert.True(ClosureTolerance.Exact < new ClosureTolerance(0.001));
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("sv-SE")]
    public void ToString_IsCultureInvariant(string culture)
    {
        CultureInfo original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo(culture);

            Assert.Equal("0.0026 ft", new ClosureTolerance(0.0026).ToString());
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }
}
