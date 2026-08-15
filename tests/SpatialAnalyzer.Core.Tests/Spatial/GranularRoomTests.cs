using System.Reflection;
using SpatialAnalyzer.Core.Domain;
using SpatialAnalyzer.Core.Geometry;
using SpatialAnalyzer.Core.Spatial;

namespace SpatialAnalyzer.Core.Tests.Spatial;

public class GranularRoomTests
{
    private const double Tolerance = 0.001;

    private static long _nextId = 1000;

    private static BoundaryLoop Rect(double x0, double y0, double width, double height)
    {
        var corners = new[]
        {
            new Point2D(x0, y0),
            new Point2D(x0 + width, y0),
            new Point2D(x0 + width, y0 + height),
            new Point2D(x0, y0 + height),
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

    private static ElementDescriptor ADoor() =>
        ElementDescriptor.Create(new RevitElementId(555), "Doors", "Single-Flush", "915 x 2134mm");

    private static RoomEntrance ADoorEntrance() =>
        new(ADoor(), BoundaryFeatureKind.Door, EntranceAuthority.Rule);

    private static CandidateRegion EnclosedRegion(int ordinal = 0) =>
        new(new RegionId(ordinal), new[] { Rect(0, 0, 10, 8) }, Tolerance);

    private static CandidateRegion OpenRegion()
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

        return new CandidateRegion(new RegionId(1), new[] { loop }, Tolerance);
    }

    /// <summary>
    /// The gap rule, arrived at from the other direction.
    ///
    /// A space open to the one next door is not a smaller room; it is part of a
    /// larger one. The only way to make it a room of its own would be to close
    /// the opening, so instead it cannot become a room at all. Without this the
    /// rule would depend on every future caller remembering to check.
    /// </summary>
    [Fact]
    public void ARegionOpenToTheNextSpaceCannotBecomeARoom()
    {
        ArgumentException error = Assert.Throws<ArgumentException>(
            () => new GranularRoom(OpenRegion(), new[] { ADoorEntrance() }));

        Assert.Contains("part of the space beyond that opening", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheOpeningThatDisqualifiedARegionIsNamedAtItsRealSize()
    {
        ArgumentException error = Assert.Throws<ArgumentException>(
            () => new GranularRoom(OpenRegion(), new[] { ADoorEntrance() }));

        Assert.Contains("open by 3", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A room has to say what let you into it, so the evidence travels with the
    /// conclusion instead of being left behind in whatever decided it.
    /// </summary>
    [Fact]
    public void ARoomMustNameWhatQualifiedIt()
    {
        Assert.Throws<ArgumentException>(
            () => new GranularRoom(EnclosedRegion(), Array.Empty<RoomEntrance>()));
    }

    [Fact]
    public void AnEnclosedRegionWithAnEntranceIsARoom()
    {
        var room = new GranularRoom(EnclosedRegion(), new[] { ADoorEntrance() });

        Assert.Equal(80.0, room.Area.InternalSquareFeet, precision: 9);
        Assert.Single(room.Entrances);
    }

    [Fact]
    public void ARoomsAreaHasItsInteriorVoidsAlreadyRemoved()
    {
        var region = new CandidateRegion(new RegionId(4), new[] { Rect(0, 0, 10, 8), Rect(3, 3, 2, 2) }, Tolerance);

        var room = new GranularRoom(region, new[] { ADoorEntrance() });

        Assert.Equal(76.0, room.Area.InternalSquareFeet, precision: 9);
    }

    /// <summary>
    /// Rooms and candidates share one numbering so that a report listing the
    /// rooms found and the candidates rejected can be read as a single sequence.
    /// </summary>
    [Fact]
    public void ARoomKeepsTheNumberOfTheCandidateItCameFrom()
    {
        CandidateRegion region = EnclosedRegion(ordinal: 17);

        var room = new GranularRoom(region, new[] { ADoorEntrance() });

        Assert.Equal(new RegionId(17), room.Id);
        Assert.Same(region, room.Region);
    }

    [Fact]
    public void ARoomExposesTheElementsThatEncloseIt()
    {
        var room = new GranularRoom(EnclosedRegion(), new[] { ADoorEntrance() });

        Assert.Equal(4, room.BoundingReferences.Count);
        Assert.All(room.BoundingReferences, r => Assert.True(r.IsAttributed));
    }

    /// <summary>
    /// A room's area is always a real measurement, because an unenclosed region
    /// never gets this far. This records that guarantee where it can be relied
    /// on rather than leaving callers to test for it.
    /// </summary>
    [Fact]
    public void ARoomsAreaIsAlwaysMeasured()
    {
        var room = new GranularRoom(EnclosedRegion(), new[] { ADoorEntrance() });

        Assert.True(room.Area.IsMeasured);
    }

    /// <summary>
    /// A room that exists because someone was asked is a different kind of
    /// claim from one the model supports on its own, and every report and
    /// export has to be able to tell them apart.
    /// </summary>
    [Fact]
    public void ARoomSaysWhetherItRestsOnSomebodysJudgement()
    {
        var byRule = new GranularRoom(EnclosedRegion(), new[] { ADoorEntrance() });

        var byOperator = new GranularRoom(EnclosedRegion(), new[]
        {
            new RoomEntrance(
                ElementDescriptor.Create(new RevitElementId(900), "Walls", "Curtain Wall", "Block 41 Storefront"),
                BoundaryFeatureKind.EmbeddedWall,
                EntranceAuthority.OperatorConfirmed),
        });

        Assert.False(byRule.RestsOnOperatorJudgement);
        Assert.True(byOperator.RestsOnOperatorJudgement);
    }

    [Fact]
    public void ARoomExposesNoSettableState()
    {
        var settable = typeof(GranularRoom)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanWrite)
            .Select(p => p.Name)
            .ToList();

        Assert.True(settable.Count == 0, $"GranularRoom must be immutable, but these are settable: {string.Join(", ", settable)}");
    }
}
