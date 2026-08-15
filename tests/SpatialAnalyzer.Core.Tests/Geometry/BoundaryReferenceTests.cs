using SpatialAnalyzer.Core.Domain;
using SpatialAnalyzer.Core.Geometry;

namespace SpatialAnalyzer.Core.Tests.Geometry;

public class BoundaryReferenceTests
{
    [Fact]
    public void Host_CarriesTheBoundingElement()
    {
        BoundaryReference reference = BoundaryReference.Host(new RevitElementId(748731));

        Assert.Equal(BoundarySource.Host, reference.Source);
        Assert.Equal(748731, reference.ElementId.Value);
        Assert.False(reference.LinkedElementId.IsValid);
        Assert.True(reference.IsAttributed);
    }

    /// <summary>
    /// The link instance and the element inside it are different things, and
    /// reporting the first as the wall that bounds a room would read as
    /// perfectly valid output. Both are kept, distinctly.
    /// </summary>
    [Fact]
    public void Linked_KeepsTheLinkInstanceAndTheLinkedElementApart()
    {
        var linkInstance = new RevitElementId(2403932);
        var wallInsideLink = new RevitElementId(559012);

        BoundaryReference reference = BoundaryReference.Linked(linkInstance, wallInsideLink);

        Assert.Equal(BoundarySource.Linked, reference.Source);
        Assert.Equal(linkInstance, reference.ElementId);
        Assert.Equal(wallInsideLink, reference.LinkedElementId);
        Assert.NotEqual(reference.ElementId, reference.LinkedElementId);
    }

    [Fact]
    public void Unattributed_ReportsThatNoElementCanBeNamed()
    {
        BoundaryReference reference = BoundaryReference.Unattributed();

        Assert.Equal(BoundarySource.None, reference.Source);
        Assert.False(reference.IsAttributed);
        Assert.False(reference.ElementId.IsValid);
    }

    [Fact]
    public void Host_RejectsAnInvalidElementId()
    {
        Assert.Throws<ArgumentException>(() => BoundaryReference.Host(RevitElementId.Invalid));
    }

    [Fact]
    public void Linked_RequiresBothIdentifiers()
    {
        var valid = new RevitElementId(1);

        Assert.Throws<ArgumentException>(() => BoundaryReference.Linked(RevitElementId.Invalid, valid));
        Assert.Throws<ArgumentException>(() => BoundaryReference.Linked(valid, RevitElementId.Invalid));
    }

    [Fact]
    public void ALinkedReferenceIsNeverEqualToAHostReferenceWithTheSameId()
    {
        var id = new RevitElementId(500);

        BoundaryReference host = BoundaryReference.Host(id);
        BoundaryReference linked = BoundaryReference.Linked(id, new RevitElementId(501));

        Assert.NotEqual(host, linked);
    }

    [Fact]
    public void UnattributedSegmentsAreSurfacedByTheLoop()
    {
        var loop = new BoundaryLoop(new[]
        {
            new BoundarySegment(
                BoundaryCurve.Straight(new Point2D(0, 0), new Point2D(1, 0)),
                BoundaryReference.Host(new RevitElementId(10))),
            new BoundarySegment(
                BoundaryCurve.Straight(new Point2D(1, 0), new Point2D(1, 1)),
                BoundaryReference.Unattributed()),
        });

        Assert.Single(loop.UnattributedSegments);
        Assert.Equal(BoundarySource.None, loop.UnattributedSegments[0].Reference.Source);
    }
}
