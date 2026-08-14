using SpatialAnalyzer.Core.Domain;

namespace SpatialAnalyzer.Core.Tests.Domain;

public class ElementDescriptorTests
{
    private static readonly RevitElementId AnyId = new(123456789012);

    [Fact]
    public void SuppliedNames_ArePreserved()
    {
        ElementDescriptor descriptor = ElementDescriptor.Create(AnyId, "Doors", "Single-Flush", "36\" x 84\"");

        Assert.Equal("Doors", descriptor.CategoryName);
        Assert.Equal("Single-Flush", descriptor.FamilyName);
        Assert.Equal("36\" x 84\"", descriptor.TypeName);
        Assert.Equal(AnyId, descriptor.Id);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void MissingNames_BecomeAVisiblePlaceholder(string? missing)
    {
        ElementDescriptor descriptor = ElementDescriptor.Create(AnyId, missing, missing, missing);

        Assert.Equal(ElementDescriptor.Unspecified, descriptor.CategoryName);
        Assert.Equal(ElementDescriptor.Unspecified, descriptor.FamilyName);
        Assert.Equal(ElementDescriptor.Unspecified, descriptor.TypeName);
    }

    /// <summary>
    /// The export groups by these names. Padding differences would otherwise
    /// split one real type into two groups that look identical in the output.
    /// </summary>
    [Fact]
    public void SurroundingWhitespace_IsTrimmedSoGroupingIsNotSplit()
    {
        ElementDescriptor padded = ElementDescriptor.Create(AnyId, " Doors ", " Single-Flush ", " 36x84 ");
        ElementDescriptor clean = ElementDescriptor.Create(AnyId, "Doors", "Single-Flush", "36x84");

        Assert.Equal(clean.CategoryName, padded.CategoryName);
        Assert.Equal(clean.FamilyName, padded.FamilyName);
        Assert.Equal(clean.TypeName, padded.TypeName);
    }

    [Fact]
    public void DescriptorsGroupByValue()
    {
        ElementDescriptor first = ElementDescriptor.Create(AnyId, "Doors", "Single-Flush", "36x84");
        ElementDescriptor second = ElementDescriptor.Create(AnyId, "Doors", "Single-Flush", "36x84");

        Assert.Equal(first, second);
    }

    [Fact]
    public void ElementsDifferingOnlyByIdAreNotEqual()
    {
        ElementDescriptor first = ElementDescriptor.Create(new RevitElementId(1), "Doors", "Single-Flush", "36x84");
        ElementDescriptor second = ElementDescriptor.Create(new RevitElementId(2), "Doors", "Single-Flush", "36x84");

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void Id_KeepsFullWidthThroughTheDescriptor()
    {
        var wide = new RevitElementId(long.MaxValue);

        ElementDescriptor descriptor = ElementDescriptor.Create(wide, "Doors", "Single-Flush", "36x84");

        Assert.Equal(long.MaxValue, descriptor.Id.Value);
    }
}
