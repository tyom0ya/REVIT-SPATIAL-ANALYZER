using System.Reflection;
using SpatialAnalyzer.Core.Domain;
using SpatialAnalyzer.Core.Geometry;

namespace SpatialAnalyzer.Core.Tests.Geometry;

/// <summary>
/// The rule this project cannot compromise on: a real opening between two
/// spaces is never closed programmatically.
///
/// These tests exist because the failure they guard against is invisible. A
/// boundary quietly joined across a doorway produces a room that looks
/// entirely plausible - closed, sensibly sized, correctly bounded - and is
/// simply not the building. Nothing downstream would flag it.
///
/// All distances are in Revit's internal unit, decimal feet.
/// </summary>
public class GapRuleTests
{
    private const double DraftingArtefact = 0.001;   // ~0.3 mm
    private const double DoorwayWidth = 3.0;         // ~915 mm

    private static BoundarySegment Wall(Point2D from, Point2D to, long id) =>
        new(BoundaryCurve.Straight(from, to), BoundaryReference.Host(new RevitElementId(id)));

    /// <summary>
    /// The shape the brief draws to describe this rule:
    ///
    ///     +----   ----+
    ///     |           |
    ///     +-----------+
    ///
    /// Four walls with a doorway-sized opening in the top. It must not be
    /// treated as an enclosed room.
    /// </summary>
    private static BoundaryLoop WallsWithARealOpening() => new(new[]
    {
        Wall(new Point2D(0, 0), new Point2D(10, 0), 1),      // bottom
        Wall(new Point2D(10, 0), new Point2D(10, 8), 2),     // right
        Wall(new Point2D(10, 8), new Point2D(6.5, 8), 3),    // top, right of opening
        // 3 ft of nothing here - no wall, no separation line, no element at all
        Wall(new Point2D(3.5, 8), new Point2D(0, 8), 4),     // top, left of opening
        Wall(new Point2D(0, 8), new Point2D(0, 0), 5),       // left
    });

    [Fact]
    public void OpenPhysicalGap_RemainsOpen()
    {
        BoundaryLoop loop = WallsWithARealOpening();

        // Not closed at any tolerance a person would defend as "the same place".
        Assert.False(loop.IsClosedWithin(DraftingArtefact));
        Assert.False(loop.IsClosedWithin(0.1));   // 30 mm
        Assert.False(loop.IsClosedWithin(1.0));   // 305 mm
    }

    [Fact]
    public void OpenPhysicalGap_IsReportedAtItsRealSize()
    {
        BoundaryLoop loop = WallsWithARealOpening();

        // The measurement is the evidence. A gap reported as smaller than it is
        // would invite someone to dismiss it.
        Assert.Equal(DoorwayWidth, loop.LargestGapInternalFeet, precision: 9);
    }

    [Fact]
    public void OpenPhysicalGap_IsLocatedNotJustCounted()
    {
        BoundaryLoop loop = WallsWithARealOpening();

        IReadOnlyList<int> open = loop.JunctionsExceeding(DraftingArtefact);

        // Exactly one discontinuity, at the junction after the third segment.
        Assert.Single(open);
        Assert.Equal(2, open[0]);
    }

    /// <summary>
    /// The counterpart. Endpoints a third of a millimetre apart describe one
    /// physical location recorded twice, and a caller may treat them as closed
    /// if it says so explicitly. This is the case the probe found in the model.
    /// </summary>
    [Fact]
    public void RepresentationGap_MayBeTreatedAsClosedWhenTheToleranceIsStated()
    {
        var loop = new BoundaryLoop(new[]
        {
            Wall(new Point2D(0, 0), new Point2D(10, 0), 1),
            Wall(new Point2D(10, 0), new Point2D(10, 8), 2),
            Wall(new Point2D(10, 8), new Point2D(0, 8), 3),
            Wall(new Point2D(0, 8), new Point2D(0, DraftingArtefact), 4),
        });

        Assert.True(loop.IsClosedWithin(DraftingArtefact));
        Assert.False(loop.IsClosedWithin(0));
    }

    /// <summary>
    /// A tolerance large enough to swallow a doorway is not a tolerance, and
    /// the type does not pretend otherwise: it answers the question it was
    /// asked. This test records that the protection is the caller's judgement
    /// about scale, not a hidden limit, which is why no default is offered
    /// anywhere.
    /// </summary>
    [Fact]
    public void AnAbsurdToleranceIsStillTheCallersStatedChoice()
    {
        BoundaryLoop loop = WallsWithARealOpening();

        Assert.True(loop.IsClosedWithin(DoorwayWidth));
    }

    /// <summary>
    /// Guards the shape of the type itself.
    ///
    /// A future change that added a Close, Snap, Heal or Repair operation would
    /// break the rule while every existing test still passed, because the tests
    /// above only check what the type currently does. This checks what it is
    /// not allowed to offer.
    /// </summary>
    [Fact]
    public void BoundaryLoop_OffersNoWayToCloseAGap()
    {
        string[] forbidden = { "close", "snap", "join", "weld", "heal", "repair", "fix", "bridge", "stitch", "force" };

        var offenders = typeof(BoundaryLoop)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(m => !m.IsSpecialName)
            .Where(m => forbidden.Any(f => m.Name.ToLowerInvariant().Contains(f)))
            // IsClosedWithin asks a question and changes nothing.
            .Where(m => m.Name != nameof(BoundaryLoop.IsClosedWithin))
            .Select(m => m.Name)
            .ToList();

        Assert.True(
            offenders.Count == 0,
            $"BoundaryLoop must not offer operations that alter a boundary's connectivity, but found: {string.Join(", ", offenders)}");
    }

    [Fact]
    public void BoundaryLoop_ExposesNoSettableState()
    {
        var settable = typeof(BoundaryLoop)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanWrite)
            .Select(p => p.Name)
            .ToList();

        Assert.True(settable.Count == 0, $"BoundaryLoop must be immutable, but these are settable: {string.Join(", ", settable)}");
    }

    [Fact]
    public void SegmentOrderIsPreservedExactlyAsExtracted()
    {
        BoundaryLoop loop = WallsWithARealOpening();

        long[] ids = loop.Segments.Select(s => s.Reference.ElementId.Value).ToArray();

        Assert.Equal(new[] { 1L, 2L, 3L, 4L, 5L }, ids);
    }

    [Fact]
    public void ANegativeToleranceIsRejectedRatherThanTreatedAsZero()
    {
        BoundaryLoop loop = WallsWithARealOpening();

        Assert.Throws<ArgumentOutOfRangeException>(() => loop.IsClosedWithin(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => loop.JunctionsExceeding(-1));
    }
}
