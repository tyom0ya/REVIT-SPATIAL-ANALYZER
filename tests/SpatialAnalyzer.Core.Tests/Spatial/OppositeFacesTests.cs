using SpatialAnalyzer.Core.Domain;
using SpatialAnalyzer.Core.Geometry;
using SpatialAnalyzer.Core.Spatial;
using Xunit;

namespace SpatialAnalyzer.Core.Tests.Spatial;

public class OppositeFacesTests
{
    private const double Splay = 10.0;
    private const double LeastOverlap = 0.65;
    private const double FurthestApart = 160.0;

    private static LoopFace Face(long element, double x, double y, double nx, double ny) =>
        new(new RevitElementId(element), new Point2D(x, y), new Point2D(nx, ny), 0, 10);

    private static IReadOnlyList<FacePair> Pair(params LoopFace[] faces) =>
        OppositeFaces.Among(faces, Splay, LeastOverlap, FurthestApart);

    /// <summary>
    /// A ten by six room: four faces looking inwards, two pairs.
    /// </summary>
    private static LoopFace[] Room() => new[]
    {
        Face(1, 0, 3, 1, 0),
        Face(2, 10, 3, -1, 0),
        Face(3, 5, 0, 0, 1),
        Face(4, 5, 6, 0, -1),
    };

    [Fact]
    public void FacesLookingAtEachOtherPair()
    {
        FacePair pair = Assert.Single(Pair(Room()[0], Room()[1]));

        Assert.Equal(0, pair.A);
        Assert.Equal(1, pair.B);
        Assert.Equal(1.0, pair.Squareness, 9);
    }

    [Fact]
    public void ARoomCorroboratesItself()
    {
        Assert.Equal(OppositeFaces.EnoughToCorroborate, Pair(Room()).Count);
    }

    /// <summary>
    /// Two faces of one element do not pair, however perfectly they oppose
    /// each other.
    ///
    /// Positioned here as a wall that wraps - the two faces look straight at
    /// one another across ten feet, so every geometric test is satisfied and
    /// only the identity of the element rejects them. That matters because the
    /// commonest version of this is the two sides of a single wall, which would
    /// otherwise report the thickness of the wall as a room.
    /// </summary>
    [Fact]
    public void TwoFacesOfOneElementDoNotPair()
    {
        Assert.Empty(Pair(
            Face(7, 0, 3, 1, 0),
            Face(7, 10, 3, -1, 0)));
    }

    /// <summary>
    /// And the same two faces on different elements do pair, which is what
    /// makes the test above about identity rather than about geometry.
    /// </summary>
    [Fact]
    public void TheSamePairOnDifferentElementsDoesPair()
    {
        Assert.Single(Pair(
            Face(7, 0, 3, 1, 0),
            Face(8, 10, 3, -1, 0)));
    }

    /// <summary>
    /// Two walls back to back also have opposite normals, and enclose nothing
    /// at all. Only the sign of the vector between them tells them from a room.
    /// </summary>
    [Fact]
    public void WallsStandingBackToBackDoNotPair()
    {
        Assert.Empty(Pair(
            Face(1, 0, 3, -1, 0),
            Face(2, 0.5, 3, 1, 0)));
    }

    [Fact]
    public void FacesThatShareNoHeightDoNotPair()
    {
        Assert.Empty(OppositeFaces.Among(
            new[]
            {
                new LoopFace(new RevitElementId(1), new Point2D(0, 3), new Point2D(1, 0), 0, 2),
                new LoopFace(new RevitElementId(2), new Point2D(10, 3), new Point2D(-1, 0), 8, 10),
            },
            Splay,
            LeastOverlap,
            FurthestApart));
    }

    [Fact]
    public void FacesFurtherApartThanTheReachDoNotPair()
    {
        Assert.Empty(OppositeFaces.Among(
            new[] { Face(1, 0, 3, 1, 0), Face(2, 500, 3, -1, 0) },
            Splay,
            LeastOverlap,
            FurthestApart));
    }

    [Fact]
    public void FacesWithinTheSplayStillPair()
    {
        double lean = 8.0 * Math.PI / 180.0;

        Assert.Single(Pair(
            Face(1, 0, 3, 1, 0),
            Face(2, 10, 3, -Math.Cos(lean), Math.Sin(lean))));
    }

    [Fact]
    public void FacesBeyondTheSplayDoNotPair()
    {
        double lean = 30.0 * Math.PI / 180.0;

        Assert.Empty(Pair(
            Face(1, 0, 3, 1, 0),
            Face(2, 10, 3, -Math.Cos(lean), Math.Sin(lean))));
    }

    [Fact]
    public void FacesAtRightAnglesDoNotPair()
    {
        Assert.Empty(Pair(Face(1, 0, 3, 1, 0), Face(3, 5, 0, 0, 1)));
    }

    [Fact]
    public void ASplayOfNinetyDegreesIsRefused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => OppositeFaces.Among(Room(), 90, LeastOverlap, FurthestApart));
    }

    [Fact]
    public void NoFacesIsNotAnError()
    {
        Assert.Empty(OppositeFaces.Among(Array.Empty<LoopFace>(), Splay, LeastOverlap, FurthestApart));
    }
}
