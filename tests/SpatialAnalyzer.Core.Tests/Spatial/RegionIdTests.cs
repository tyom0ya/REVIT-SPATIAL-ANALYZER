using System.Globalization;
using SpatialAnalyzer.Core.Spatial;

namespace SpatialAnalyzer.Core.Tests.Spatial;

public class RegionIdTests
{
    [Fact]
    public void SameOrdinalIsTheSameRegion()
    {
        Assert.Equal(new RegionId(7), new RegionId(7));
        Assert.Equal(new RegionId(7).GetHashCode(), new RegionId(7).GetHashCode());
    }

    [Fact]
    public void DifferentOrdinalsAreDifferentRegions()
    {
        Assert.NotEqual(new RegionId(7), new RegionId(8));
    }

    [Fact]
    public void RegionsSortByOrdinal()
    {
        var ids = new[] { new RegionId(3), new RegionId(1), new RegionId(2) };

        Array.Sort(ids);

        Assert.Equal(new[] { new RegionId(1), new RegionId(2), new RegionId(3) }, ids);
    }

    [Fact]
    public void ANegativeOrdinalIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new RegionId(-1));
    }

    /// <summary>
    /// Region identifiers appear in reports and in the exported JSON, so their
    /// text must not depend on the machine's regional settings. Cultures that
    /// use a different minus sign or digit shapes would otherwise change the
    /// output for the same model.
    /// </summary>
    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    [InlineData("sv-SE")]
    [InlineData("fa-IR")]
    public void ToString_IsCultureInvariant(string culture)
    {
        CultureInfo original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo(culture);

            Assert.Equal("R42", new RegionId(42).ToString());
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }
}
