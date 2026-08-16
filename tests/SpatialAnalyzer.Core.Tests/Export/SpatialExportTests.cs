using System.Globalization;
using System.Text.Json;
using SpatialAnalyzer.Core.Domain;
using SpatialAnalyzer.Core.Export;
using SpatialAnalyzer.Core.Geometry;
using SpatialAnalyzer.Core.Spatial;

namespace SpatialAnalyzer.Core.Tests.Export;

public class SpatialExportTests
{
    private const double Tolerance = 1.0 / 384.0;
    private const string Generated = "2026-08-15T12:00:00Z";

    private static long _nextId = 60000;

    private static readonly AnalysisContextInfo L2 = new(
        new RevitElementId(1350631), "L2", "FloorPlan",
        new RevitElementId(593177), "L2", 8.0833,
        new RevitElementId(118390), "New Construction");

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

    private static ElementDescriptor Element(long id, string category, string family = "Fam", string type = "Type") =>
        ElementDescriptor.Create(new RevitElementId(id), category, family, type);

    private static QualificationOutcome Qualified(int ordinal, EntranceAuthority authority = EntranceAuthority.Rule)
    {
        var region = new CandidateRegion(new RegionId(ordinal), new[] { Rect(0, 0, 10, 8) }, Tolerance);

        var feature = new BoundaryFeature(Element(786853, "Doors"), BoundaryFeatureKind.Door);
        var confirmed = authority == EntranceAuthority.OperatorConfirmed
            ? new HashSet<RevitElementId> { feature.Element.Id }
            : null;

        // A door qualifies by rule; to force operator authority the rule is
        // given something it will not admit, and the confirmation supplies it.
        if (authority == EntranceAuthority.OperatorConfirmed)
        {
            var storefront = new BoundaryFeature(Element(1594372, "Walls"), BoundaryFeatureKind.EmbeddedWall);
            return new RoomQualifier(EntranceRule.Default).Qualify(
                region,
                new[] { storefront },
                new HashSet<RevitElementId> { storefront.Element.Id });
        }

        return new RoomQualifier(EntranceRule.Default).Qualify(region, new[] { feature }, confirmed);
    }

    private static QualificationOutcome RejectedForNoEntrance(int ordinal)
    {
        var region = new CandidateRegion(new RegionId(ordinal), new[] { Rect(0, 0, 4, 4) }, Tolerance);
        return new RoomQualifier(EntranceRule.Default).Qualify(region, Array.Empty<BoundaryFeature>());
    }

    private static DoorAdjacencyIndex NoDoors() =>
        DoorAdjacencyIndex.Build(new Dictionary<RegionId, IReadOnlyList<BoundaryFeature>>(), EntranceRule.Default);

    private static SpatialExport Build(
        IReadOnlyList<QualificationOutcome> outcomes,
        DoorAdjacencyIndex? doors = null,
        IReadOnlyDictionary<RegionId, IReadOnlyList<ElementDescriptor>>? elements = null) =>
        SpatialExport.Build(
            L2,
            "Snowdon Towers Sample Architectural",
            @"C:\models\dev\Snowdon.rvt",
            Tolerance,
            outcomes,
            doors ?? NoDoors(),
            elements ?? new Dictionary<RegionId, IReadOnlyList<ElementDescriptor>>(),
            Generated);

    [Fact]
    public void RoomsAndRejectionsAreBothExported()
    {
        SpatialExport export = Build(new[] { Qualified(0), RejectedForNoEntrance(1) });

        Assert.Single(export.Rooms);
        Assert.Single(export.RegionsRejected);
        Assert.Equal(2, export.Summary.Regions);
        Assert.Equal("NoEntrance", export.RegionsRejected[0].Reason);
    }

    /// <summary>
    /// A room that exists because somebody was asked is a different kind of
    /// claim from one the model supports on its own. A file that hid the
    /// difference would be saying the model contains something it does not.
    /// </summary>
    [Fact]
    public void ARoomRestingOnJudgementSaysSo()
    {
        SpatialExport export = Build(new[]
        {
            Qualified(0),
            Qualified(1, EntranceAuthority.OperatorConfirmed),
        });

        Assert.Equal(1, export.Summary.RoomsOnModelEvidence);
        Assert.Equal(1, export.Summary.RoomsOnOperatorJudgement);

        ExportedRoom judged = export.Rooms.Single(r => r.RestsOnOperatorJudgement);
        Assert.Equal("OperatorConfirmed", judged.Entrances[0].Authority);
    }

    [Fact]
    public void AnElementIsExportedAsCategoryFamilyTypeAndId()
    {
        var elements = new Dictionary<RegionId, IReadOnlyList<ElementDescriptor>>
        {
            [new RegionId(0)] = new[] { Element(1378482, "Furniture", "Table-Dining Round w Chairs", "60\" Diameter") },
        };

        SpatialExport export = Build(new[] { Qualified(0) }, elements: elements);

        ExportedElement element = Assert.Single(export.Rooms[0].Elements);
        Assert.Equal("Furniture", element.Category);
        Assert.Equal("Table-Dining Round w Chairs", element.Family);
        Assert.Equal("60\" Diameter", element.Type);
        Assert.Equal(1378482, element.Id);
    }

    /// <summary>
    /// A door belongs to both rooms either side of it, so it appears in both
    /// element lists. Stating the relationship once, plainly, is what stops that
    /// reading as duplication.
    /// </summary>
    [Fact]
    public void ADoorIsExportedWithBothRoomsItConnects()
    {
        var door = new BoundaryFeature(Element(786853, "Doors"), BoundaryFeatureKind.Door);

        DoorAdjacencyIndex doors = DoorAdjacencyIndex.Build(
            new Dictionary<RegionId, IReadOnlyList<BoundaryFeature>>
            {
                [new RegionId(0)] = new[] { door },
                [new RegionId(1)] = new[] { door },
            },
            EntranceRule.Default);

        SpatialExport export = Build(new[] { Qualified(0), Qualified(1) }, doors);

        ExportedDoor exported = Assert.Single(export.Doors);
        Assert.Equal("BetweenTwoRegions", exported.Connection);
        Assert.Equal(new[] { "R0", "R1" }, exported.Rooms);
    }

    /// <summary>
    /// The open question from the plan topology survey, answered. Fourteen
    /// boundary segments on the acceptance level have no element behind them.
    /// They are real boundary and cannot be given a category, family and type,
    /// so they are exported as what is known: length and shape. Dropping them
    /// would make a room's boundary look complete when part of it is not
    /// accounted for.
    /// </summary>
    [Fact]
    public void BoundaryWithNoElementBehindItIsExportedRatherThanDropped()
    {
        var loop = new BoundaryLoop(new[]
        {
            new BoundarySegment(
                BoundaryCurve.Straight(new Point2D(0, 0), new Point2D(4, 0)),
                BoundaryReference.Host(new RevitElementId(10))),
            new BoundarySegment(
                BoundaryCurve.Straight(new Point2D(4, 0), new Point2D(4, 4)),
                BoundaryReference.Unattributed()),
            new BoundarySegment(
                BoundaryCurve.Straight(new Point2D(4, 4), new Point2D(0, 4)),
                BoundaryReference.Host(new RevitElementId(11))),
            new BoundarySegment(
                BoundaryCurve.Straight(new Point2D(0, 4), new Point2D(0, 0)),
                BoundaryReference.Host(new RevitElementId(12))),
        });

        var region = new CandidateRegion(new RegionId(0), new[] { loop }, Tolerance);
        QualificationOutcome outcome = new RoomQualifier(EntranceRule.Default).Qualify(
            region,
            new[] { new BoundaryFeature(Element(1, "Doors"), BoundaryFeatureKind.Door) });

        SpatialExport export = Build(new[] { outcome });

        ExportedUnattributedBoundary segment = Assert.Single(export.Rooms[0].UnattributedBoundary);
        Assert.Equal("Line", segment.CurveKind);
        Assert.Equal(4.0, segment.LengthInternalFeet, precision: 9);
        Assert.Equal(1, export.Summary.UnattributedBoundarySegments);
    }

    /// <summary>
    /// A region rejected for not being enclosed has no area, and nothing is
    /// invented to fill the field.
    /// </summary>
    [Fact]
    public void ARegionWithNoMeasurableAreaExportsNoArea()
    {
        var open = new BoundaryLoop(new[]
        {
            new BoundarySegment(BoundaryCurve.Straight(new Point2D(0, 0), new Point2D(10, 0)), BoundaryReference.Host(new RevitElementId(1))),
            new BoundarySegment(BoundaryCurve.Straight(new Point2D(10, 0), new Point2D(10, 8)), BoundaryReference.Host(new RevitElementId(2))),
            new BoundarySegment(BoundaryCurve.Straight(new Point2D(10, 8), new Point2D(6.5, 8)), BoundaryReference.Host(new RevitElementId(3))),
            new BoundarySegment(BoundaryCurve.Straight(new Point2D(3.5, 8), new Point2D(0, 8)), BoundaryReference.Host(new RevitElementId(4))),
            new BoundarySegment(BoundaryCurve.Straight(new Point2D(0, 8), new Point2D(0, 0)), BoundaryReference.Host(new RevitElementId(5))),
        });

        var region = new CandidateRegion(new RegionId(9), new[] { open }, Tolerance);
        QualificationOutcome outcome = new RoomQualifier(EntranceRule.Default).Qualify(region, Array.Empty<BoundaryFeature>());

        SpatialExport export = Build(new[] { outcome });

        ExportedRejection rejection = Assert.Single(export.RegionsRejected);
        Assert.Equal("NotEnclosed", rejection.Reason);
        Assert.Null(rejection.AreaInternalSquareFeet);
        Assert.Null(rejection.AreaSquareMetres);
    }

    [Fact]
    public void AreaIsGivenInBothInternalUnitsAndSquareMetres()
    {
        SpatialExport export = Build(new[] { Qualified(0) });

        ExportedRoom room = export.Rooms[0];
        Assert.Equal(80.0, room.AreaInternalSquareFeet, precision: 9);
        Assert.Equal(80.0 * 0.09290304, room.AreaSquareMetres, precision: 9);
    }

    [Fact]
    public void TheFileNamesItsOwnShape()
    {
        Assert.Equal("revit-spatial-analyzer/rooms/1", Build(new[] { Qualified(0) }).Schema);
    }

    /// <summary>
    /// Two runs over an unchanged model must differ nowhere but the timestamp,
    /// which is what makes two exports worth comparing at all.
    /// </summary>
    [Fact]
    public void TwoExportsOfTheSameAnalysisAreIdentical()
    {
        string first = SpatialExportWriter.ToJson(Build(new[] { Qualified(0), RejectedForNoEntrance(1) }));
        string second = SpatialExportWriter.ToJson(Build(new[] { RejectedForNoEntrance(1), Qualified(0) }));

        Assert.Equal(first, second);
    }

    /// <summary>
    /// Numbers must not follow the operator's regional settings. Swedish writes
    /// a negative with U+2212 rather than a hyphen, which is the case that slips
    /// past a decimal separator check, and this project has been caught by it.
    /// </summary>
    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    [InlineData("sv-SE")]
    public void TheFileDoesNotFollowTheOperatorsRegionalSettings(string culture)
    {
        CultureInfo original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo(culture);

            string json = SpatialExportWriter.ToJson(Build(new[] { Qualified(0) }));

            Assert.Contains("\"levelElevationInternalFeet\": 8.0833", json, StringComparison.Ordinal);
            Assert.DoesNotContain("8,0833", json, StringComparison.Ordinal);
            Assert.DoesNotContain("\u2212", json, StringComparison.Ordinal);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void TheFileIsValidJsonAndReadsBack()
    {
        string json = SpatialExportWriter.ToJson(Build(new[] { Qualified(0), RejectedForNoEntrance(1) }));

        using JsonDocument parsed = JsonDocument.Parse(json);

        Assert.Equal("revit-spatial-analyzer/rooms/1", parsed.RootElement.GetProperty("schema").GetString());
        Assert.Equal(1, parsed.RootElement.GetProperty("rooms").GetArrayLength());
        Assert.Equal("L2", parsed.RootElement.GetProperty("context").GetProperty("level").GetString());
    }

    /// <summary>
    /// Revit type names contain inch marks and ampersands. Escaping those to
    /// &#x22; and &amp; is valid JSON that nobody can read, so the file keeps
    /// them legible.
    /// </summary>
    [Fact]
    public void TypeNamesWithQuotesAndAmpersandsStayReadable()
    {
        var elements = new Dictionary<RegionId, IReadOnlyList<ElementDescriptor>>
        {
            [new RegionId(0)] = new[] { Element(1, "Walls", "Basic Wall", "Chase - GWB & Metal Stud 6 5/8\"") },
        };

        string json = SpatialExportWriter.ToJson(Build(new[] { Qualified(0) }, elements: elements));

        Assert.Contains("Chase - GWB & Metal Stud 6 5/8", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\\u0026", json, StringComparison.Ordinal);

        // Still valid JSON: the inch mark is escaped as a quote must be.
        using JsonDocument parsed = JsonDocument.Parse(json);
        Assert.Equal(
            "Chase - GWB & Metal Stud 6 5/8\"",
            parsed.RootElement.GetProperty("rooms")[0].GetProperty("elements")[0].GetProperty("type").GetString());
    }

    [Fact]
    public void LineEndingsDoNotFollowTheHost()
    {
        Assert.DoesNotContain("\r", SpatialExportWriter.ToJson(Build(new[] { Qualified(0) })), StringComparison.Ordinal);
    }
}
