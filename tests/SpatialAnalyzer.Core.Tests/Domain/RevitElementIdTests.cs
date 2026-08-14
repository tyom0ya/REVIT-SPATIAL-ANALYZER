using System.Globalization;
using SpatialAnalyzer.Core.Domain;

namespace SpatialAnalyzer.Core.Tests.Domain;

public class RevitElementIdTests
{
    [Theory]
    [InlineData(0L)]
    [InlineData(1L)]
    [InlineData(2147483647L)] // int.MaxValue
    [InlineData(2147483648L)] // the first id a 32-bit field loses
    [InlineData(long.MaxValue)]
    public void Value_IsPreservedAtFullWidth(long value)
    {
        var id = new RevitElementId(value);

        Assert.Equal(value, id.Value);
    }

    [Fact]
    public void Invalid_MatchesRevitsOwnInvalidId()
    {
        Assert.Equal(-1L, RevitElementId.Invalid.Value);
        Assert.False(RevitElementId.Invalid.IsValid);
    }

    [Fact]
    public void ZeroIsAValidId()
    {
        // Only -1 means "no element". Zero is a real id in Revit and treating
        // it as absent would silently drop elements.
        Assert.True(new RevitElementId(0).IsValid);
    }

    /// <summary>
    /// Ordering has to be numeric. Sorting ids as text would place 10 before 2
    /// and make exported JSON reorder itself depending on the values present.
    /// </summary>
    [Fact]
    public void Ordering_IsNumericRatherThanLexicographic()
    {
        var ids = new[]
        {
            new RevitElementId(10),
            new RevitElementId(2),
            new RevitElementId(1000),
            new RevitElementId(2147483648),
        };

        long[] sorted = ids.OrderBy(id => id).Select(id => id.Value).ToArray();

        Assert.Equal(new[] { 2L, 10L, 1000L, 2147483648L }, sorted);
    }

    [Fact]
    public void ComparisonOperators_AgreeWithCompareTo()
    {
        var small = new RevitElementId(2);
        var large = new RevitElementId(10);

        Assert.True(small < large);
        Assert.True(large > small);
        Assert.True(small <= new RevitElementId(2));
        Assert.True(large >= new RevitElementId(10));
    }

    [Fact]
    public void Equality_IsByValue()
    {
        Assert.Equal(new RevitElementId(42), new RevitElementId(42));
        Assert.NotEqual(new RevitElementId(42), new RevitElementId(43));
    }

    /// <summary>
    /// Exported output must not change with the operator's regional settings.
    ///
    /// The culture used here is deliberate. Digit grouping is irrelevant, since
    /// the default integer format never groups, so a test against de-DE would
    /// pass whether or not the implementation was invariant. The real exposure
    /// is the negative sign: sv-SE renders it as U+2212 MINUS SIGN rather than
    /// ASCII hyphen, and the only negative id this type produces is
    /// <see cref="RevitElementId.Invalid"/>, which is exactly the value most
    /// likely to be written out and read back.
    /// </summary>
    [Theory]
    [InlineData("sv-SE")]
    [InlineData("fa-IR")]
    public void ToString_IsCultureInvariantForNegativeIds(string culture)
    {
        CultureInfo original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo(culture);

            string text = RevitElementId.Invalid.ToString();

            Assert.Equal("-1", text);
            Assert.Equal('-', text[0]); // ASCII hyphen, not U+2212
            Assert.Equal("1234567", new RevitElementId(1234567).ToString());
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    /// <summary>
    /// Formatting and parsing have to agree under any culture. TryParse is
    /// invariant, so a culture-sensitive ToString would emit text this type
    /// could not read back.
    /// </summary>
    [Theory]
    [InlineData("sv-SE")]
    [InlineData("fa-IR")]
    public void ToStringAndTryParse_RoundTripUnderAnyCulture(string culture)
    {
        CultureInfo original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo(culture);

            Assert.True(RevitElementId.TryParse(RevitElementId.Invalid.ToString(), out RevitElementId parsed));
            Assert.Equal(RevitElementId.Invalid, parsed);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void TryParse_RoundTripsToString()
    {
        var original = new RevitElementId(2147483648);

        Assert.True(RevitElementId.TryParse(original.ToString(), out RevitElementId parsed));
        Assert.Equal(original, parsed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-number")]
    [InlineData("12.5")]
    public void TryParse_RejectsNonIntegerText(string? text)
    {
        Assert.False(RevitElementId.TryParse(text, out RevitElementId parsed));
        Assert.Equal(RevitElementId.Invalid, parsed);
    }
}
