using System.Text.Json;

namespace SpatialAnalyzer.Core.Tests;

/// <summary>
/// Revit element ids are 64-bit. Every JSON export this project produces has to
/// carry them without silently narrowing to <see cref="int"/>, so these tests
/// pin that assumption down before any export code is written against it.
///
/// They double as the proof that the test harness discovers and runs tests at
/// all, which is why this project exists this early in the build.
/// </summary>
public class SixtyFourBitIdSerializationTests
{
    [Theory]
    [InlineData(0L)]
    [InlineData(1L)]
    [InlineData(2147483647L)] // int.MaxValue
    [InlineData(2147483648L)] // int.MaxValue + 1: the first id a 32-bit field loses
    [InlineData(long.MaxValue)]
    public void SixtyFourBitId_SurvivesJsonRoundTrip(long id)
    {
        string json = JsonSerializer.Serialize(new IdCarrier(id));

        IdCarrier? restored = JsonSerializer.Deserialize<IdCarrier>(json);

        Assert.NotNull(restored);
        Assert.Equal(id, restored!.Id);
    }

    /// <summary>
    /// Demonstrates the concrete failure this project must avoid, rather than
    /// merely asserting that it would be bad.
    /// </summary>
    [Fact]
    public void NarrowingA64BitIdTo32Bits_LosesTheValue()
    {
        const long id = 2147483648L;

        long narrowed = unchecked((int)id);

        Assert.NotEqual(id, narrowed);
    }

    public sealed record IdCarrier(long Id);
}
