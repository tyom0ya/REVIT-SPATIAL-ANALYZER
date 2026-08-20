using SpatialAnalyzer.Core.Domain;
using SpatialAnalyzer.Core.Geometry;
using SpatialAnalyzer.Core.Spatial;
using Xunit;

namespace SpatialAnalyzer.Core.Tests.Spatial;

public class LoopFacingTests
{
    private const double Tolerance = 0.0026;

    /// <summary>
    /// A ten by six loop, traced anticlockwise as the arrangement traces them.
    /// </summary>
    private static PlanFace Loop(params int[] edgeFaces) =>
        new(
            new[]
            {
                new Point2D(0, 0),
                new Point2D(10, 0),
                new Point2D(10, 6),
                new Point2D(0, 6),
            },
            AreaMeasurement.Measured(60, LoopWinding.CounterClockwise, Tolerance, 0),
            new[] { new RevitElementId(1) },
            false,
            edgeFaces.Distinct().ToArray(),
            edgeFaces);

    private static LoopFace Face(long element, double nx, double ny) =>
        new(new RevitElementId(element), new Point2D(0, 0), new Point2D(nx, ny), 0, 10);

    /// <summary>
    /// Walls around a room: every face looks in at the space between them.
    /// The loop is traced anticlockwise, so along the bottom edge, which runs
    /// left to right, the inside is upward.
    /// </summary>
    private static LoopFace[] LookingIn() => new[]
    {
        Face(1, 0, 1),
        Face(2, -1, 0),
        Face(3, 0, -1),
        Face(4, 1, 0),
    };

    /// <summary>The same four faces of one wall, looking out of the material.</summary>
    private static LoopFace[] LookingOut() => new[]
    {
        Face(1, 0, -1),
        Face(2, 1, 0),
        Face(3, 0, 1),
        Face(4, -1, 0),
    };

    [Fact]
    public void ARoomIsBoundedEntirelyByFacesLookingIntoIt()
    {
        Assert.Equal(1.0, LoopFacing.InwardShare(Loop(0, 1, 2, 3), LookingIn()), 9);
    }

    /// <summary>
    /// The case this exists for. A wall encloses its own cross section, which
    /// is as closed a loop as any room and the same shape; the only thing that
    /// says it is brick rather than air is that its faces look out of it.
    /// </summary>
    [Fact]
    public void AWallCrossSectionIsBoundedEntirelyByFacesLookingOutOfIt()
    {
        Assert.Equal(0.0, LoopFacing.InwardShare(Loop(0, 1, 2, 3), LookingOut()), 9);
    }

    [Fact]
    public void ARoomIsASpaceAndAWallIsNot()
    {
        Assert.True(LoopFacing.InwardShare(Loop(0, 1, 2, 3), LookingIn()) >= LoopFacing.EnoughToBeASpace);
        Assert.False(LoopFacing.InwardShare(Loop(0, 1, 2, 3), LookingOut()) >= LoopFacing.EnoughToBeASpace);
    }

    /// <summary>
    /// Weighed by length, not counted. The two long sides here look inward and
    /// the two short ends look out; by length that is ten of every sixteen feet
    /// and the loop is a space, while counting would call it evenly split.
    /// </summary>
    [Fact]
    public void EdgesAreWeighedByLengthRatherThanCounted()
    {
        var mixed = new[]
        {
            Face(1, 0, 1),
            Face(2, 1, 0),
            Face(3, 0, -1),
            Face(4, -1, 0),
        };

        Assert.Equal(20.0 / 32.0, LoopFacing.InwardShare(Loop(0, 1, 2, 3), mixed), 9);
    }

    /// <summary>
    /// An edge drawn from a centre line names no face and has no opinion about
    /// which side the material is on, so it is left out of the reckoning rather
    /// than counted as disagreeing.
    /// </summary>
    [Fact]
    public void EdgesWithNoFaceAreNotCounted()
    {
        var faces = LookingIn();

        Assert.Equal(1.0, LoopFacing.InwardShare(Loop(0, PlanFaces.NoFace, 2, 3), faces), 9);
    }

    [Fact]
    public void ALoopWithNoFacesAtAllIsNotASpace()
    {
        double share = LoopFacing.InwardShare(
            Loop(PlanFaces.NoFace, PlanFaces.NoFace, PlanFaces.NoFace, PlanFaces.NoFace),
            LookingIn());

        Assert.Equal(0.0, share, 9);
        Assert.False(share >= LoopFacing.EnoughToBeASpace);
    }
}
