using SpatialAnalyzer.Core.Domain;
using SpatialAnalyzer.Core.Geometry;
using SpatialAnalyzer.Core.Spatial;

namespace SpatialAnalyzer.Core.Tests.Spatial;

public class RoomQualifierTests
{
    private const double Tolerance = 1.0 / 384.0;   // Revit's ShortCurveTolerance

    private static long _nextId = 5000;

    private static RoomQualifier Qualifier() => new(EntranceRule.Default);

    private static BoundaryFeature Feature(BoundaryFeatureKind kind, string category) =>
        new(ElementDescriptor.Create(new RevitElementId(Interlocked.Increment(ref _nextId)), category, "F", "T"), kind);

    private static BoundaryLoop Rect(double width, double height)
    {
        var corners = new[]
        {
            new Point2D(0, 0),
            new Point2D(width, 0),
            new Point2D(width, height),
            new Point2D(0, height),
        };

        var segments = new List<BoundarySegment>(4);
        for (int i = 0; i < corners.Length; i++)
        {
            segments.Add(new BoundarySegment(
                BoundaryCurve.Straight(corners[i], corners[(i + 1) % corners.Length]),
                BoundaryReference.Host(new RevitElementId(Interlocked.Increment(ref _nextId)))));
        }

        return new BoundaryLoop(segments);
    }

    private static CandidateRegion Enclosed(int ordinal = 0) =>
        new(new RegionId(ordinal), new[] { Rect(10, 8) }, Tolerance);

    private static CandidateRegion OpenToTheNextSpace(int ordinal = 1)
    {
        BoundarySegment Wall(Point2D from, Point2D to) => new(
            BoundaryCurve.Straight(from, to),
            BoundaryReference.Host(new RevitElementId(Interlocked.Increment(ref _nextId))));

        var loop = new BoundaryLoop(new[]
        {
            Wall(new Point2D(0, 0), new Point2D(10, 0)),
            Wall(new Point2D(10, 0), new Point2D(10, 8)),
            Wall(new Point2D(10, 8), new Point2D(6.5, 8)),
            // a 3 ft doorway-sized opening
            Wall(new Point2D(3.5, 8), new Point2D(0, 8)),
            Wall(new Point2D(0, 8), new Point2D(0, 0)),
        });

        return new CandidateRegion(new RegionId(ordinal), new[] { loop }, Tolerance);
    }

    [Fact]
    public void AnEnclosedSpaceWithADoorIsARoom()
    {
        QualificationOutcome outcome = Qualifier().Qualify(
            Enclosed(),
            new[] { Feature(BoundaryFeatureKind.Door, "Doors") });

        Assert.True(outcome.IsQualified);
        Assert.Null(outcome.Reason);
        Assert.Equal(80.0, outcome.Room!.Area.InternalSquareFeet, precision: 9);
    }

    [Fact]
    public void ARoomNamesWhatLetYouIn()
    {
        QualificationOutcome outcome = Qualifier().Qualify(
            Enclosed(),
            new[]
            {
                Feature(BoundaryFeatureKind.Door, "Doors"),
                Feature(BoundaryFeatureKind.Window, "Windows"),
            });

        // The window is on the boundary but is not why this is a room.
        Assert.Single(outcome.Room!.Entrances);
        Assert.Contains("1 x Door", outcome.Explanation, StringComparison.Ordinal);
    }

    /// <summary>
    /// The gap rule, reached through the qualifier. A space open to the one next
    /// door is part of it, and no amount of doors on its boundary changes that.
    /// </summary>
    [Fact]
    public void ASpaceOpenToTheNextOneIsNotARoomHoweverManyDoorsItHas()
    {
        QualificationOutcome outcome = Qualifier().Qualify(
            OpenToTheNextSpace(),
            new[]
            {
                Feature(BoundaryFeatureKind.Door, "Doors"),
                Feature(BoundaryFeatureKind.Door, "Doors"),
            });

        Assert.False(outcome.IsQualified);
        Assert.Equal(RejectionReason.NotEnclosed, outcome.Reason);
        Assert.Null(outcome.Room);
    }

    [Fact]
    public void AnUnenclosedRejectionReportsTheOpeningAtItsRealSize()
    {
        QualificationOutcome outcome = Qualifier().Qualify(OpenToTheNextSpace(), Array.Empty<BoundaryFeature>());

        Assert.Contains("open by 3", outcome.Explanation, StringComparison.Ordinal);
    }

    /// <summary>
    /// The refusal that matters most in this model. Twelve lift doors across two
    /// shafts, and five trash chutes, are set into bounding walls and read
    /// exactly like doors. Admitting them would turn two lift shafts and a
    /// refuse chute into rooms.
    /// </summary>
    [Fact]
    public void ALiftShaftRingedWithLiftDoorsIsNotARoom()
    {
        var liftDoors = Enumerable.Range(0, 6)
            .Select(_ => Feature(BoundaryFeatureKind.SpecialtyEquipment, "Specialty Equipment"))
            .ToList();

        QualificationOutcome outcome = Qualifier().Qualify(Enclosed(), liftDoors);

        Assert.False(outcome.IsQualified);
        Assert.Equal(RejectionReason.NoEntrance, outcome.Reason);
        Assert.Contains("6 x SpecialtyEquipment", outcome.Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public void GlazingIsNotAWayIn()
    {
        QualificationOutcome outcome = Qualifier().Qualify(
            Enclosed(),
            new[]
            {
                Feature(BoundaryFeatureKind.CurtainWallPanel, "Curtain Panels"),
                Feature(BoundaryFeatureKind.Window, "Windows"),
                Feature(BoundaryFeatureKind.EmbeddedWall, "Walls"),
            });

        Assert.Equal(RejectionReason.NoEntrance, outcome.Reason);
    }

    /// <summary>
    /// A chase with nothing at all set into its boundary. The rejection has to
    /// say so, because "no entrance" alone reads the same as a space whose
    /// entrance was missed.
    /// </summary>
    [Fact]
    public void ABoundaryWithNothingInItSaysSo()
    {
        QualificationOutcome outcome = Qualifier().Qualify(Enclosed(), Array.Empty<BoundaryFeature>());

        Assert.Equal(RejectionReason.NoEntrance, outcome.Reason);
        Assert.Equal("Nothing at all is set into this boundary.", outcome.Explanation);
    }

    [Fact]
    public void ADoorPanelInACurtainWallIsADoor()
    {
        QualificationOutcome outcome = Qualifier().Qualify(
            Enclosed(),
            new[]
            {
                Feature(BoundaryFeatureKind.CurtainWallPanel, "Curtain Panels"),
                Feature(BoundaryFeatureKind.CurtainWallDoorPanel, "Doors"),
            });

        Assert.True(outcome.IsQualified);
        Assert.Single(outcome.Room!.Entrances);
    }

    [Fact]
    public void AnOpeningCutThroughAWallIsAWayThrough()
    {
        QualificationOutcome outcome = Qualifier().Qualify(
            Enclosed(),
            new[] { Feature(BoundaryFeatureKind.Opening, "Rectangular Straight Wall Opening") });

        Assert.True(outcome.IsQualified);
    }

    /// <summary>
    /// Something the adapter could not classify must never be assumed to be a
    /// way in. It is reported, so an unhandled category surfaces as a question
    /// rather than as a room that should not exist.
    /// </summary>
    [Fact]
    public void SomethingUnrecognisedIsNotTreatedAsAnEntrance()
    {
        QualificationOutcome outcome = Qualifier().Qualify(
            Enclosed(),
            new[] { Feature(BoundaryFeatureKind.Unknown, "(no category)") });

        Assert.Equal(RejectionReason.NoEntrance, outcome.Reason);
        Assert.Contains("1 x Unknown", outcome.Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public void ARuleCannotBeBuiltThatAdmitsTheUnrecognised()
    {
        Assert.Throws<ArgumentException>(
            () => new EntranceRule(new[] { BoundaryFeatureKind.Door, BoundaryFeatureKind.Unknown }));
    }

    /// <summary>
    /// Guards the tolerance decision from being bypassed further down. A region
    /// measured against a tolerance wide enough to swallow a doorway must not be
    /// able to become a room by having a door on it.
    /// </summary>
    [Fact]
    public void ARegionMeasuredAgainstAnIndefensibleToleranceIsRefusedOutright()
    {
        var region = new CandidateRegion(new RegionId(9), new[] { Rect(10, 8) }, 3.0);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => Qualifier().Qualify(region, new[] { Feature(BoundaryFeatureKind.Door, "Doors") }));
    }

    [Fact]
    public void TheRuleReportsWhatItAdmits()
    {
        Assert.Equal(
            new[]
            {
                BoundaryFeatureKind.Door,
                BoundaryFeatureKind.Opening,
                BoundaryFeatureKind.CurtainWallDoorPanel,
            }.OrderBy(k => k),
            EntranceRule.Default.AdmittingKinds);

        Assert.False(EntranceRule.Default.Admits(BoundaryFeatureKind.SpecialtyEquipment));
        Assert.False(EntranceRule.Default.Admits(BoundaryFeatureKind.Window));
        Assert.False(EntranceRule.Default.Admits(BoundaryFeatureKind.EmbeddedWall));
        Assert.False(EntranceRule.Default.Admits(BoundaryFeatureKind.CurtainWallPanel));
    }
}
