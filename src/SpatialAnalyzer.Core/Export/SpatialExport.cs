using SpatialAnalyzer.Core.Domain;
using SpatialAnalyzer.Core.Geometry;
using SpatialAnalyzer.Core.Spatial;

namespace SpatialAnalyzer.Core.Export;

/// <summary>One element, as the brief asks for it: category, family, type, id.</summary>
public sealed record ExportedElement(string Category, string Family, string Type, long Id);

/// <summary>
/// A way into a room, and on whose authority it counts as one.
///
/// Authority is exported because a room that exists because somebody was asked
/// is a different kind of claim from one the model supports on its own. A file
/// that hid the difference would be saying the model contains something it does
/// not.
/// </summary>
public sealed record ExportedEntrance(string Kind, string Authority, ExportedElement Element);

/// <summary>
/// Boundary the model draws but attributes to nothing.
///
/// The acceptance level has fourteen such segments, between three and eighteen
/// centimetres long, appearing where walls meet. They are real boundary and
/// cannot be described as category, family, type and id, because there is no
/// element to describe. Rather than drop them - which would make a room's
/// boundary look complete when part of it is unaccounted for - they are exported
/// as what is known about them: how long they are and what shape.
/// </summary>
public sealed record ExportedUnattributedBoundary(string CurveKind, double LengthInternalFeet);

public sealed record ExportedRoom(
    string Id,
    double AreaInternalSquareFeet,
    double AreaSquareMetres,
    bool RestsOnOperatorJudgement,
    int BoundaryLoops,
    string Qualification,
    IReadOnlyList<ExportedEntrance> Entrances,
    IReadOnlyList<ExportedElement> Elements,
    IReadOnlyList<ExportedUnattributedBoundary> UnattributedBoundary);

/// <summary>
/// A region that was not reported as a room, and why.
///
/// Exported alongside the rooms because a file listing only what qualified gives
/// no way to tell a correct rejection from a room that was missed.
/// </summary>
public sealed record ExportedRejection(
    string Id,
    double? AreaInternalSquareFeet,
    double? AreaSquareMetres,
    string Reason,
    string Explanation);

/// <summary>
/// A door and what it connects.
///
/// A door belongs to both rooms either side of it, so it appears in both of
/// their element lists. That is deliberate rather than duplication, and this
/// section is where the relationship is stated once and plainly.
/// </summary>
public sealed record ExportedDoor(ExportedElement Element, string Connection, IReadOnlyList<string> Rooms);

public sealed record ExportContext(
    string Document,
    string DocumentPath,
    string View,
    string ViewType,
    long ViewId,
    string Level,
    long LevelId,
    double LevelElevationInternalFeet,
    string Phase,
    long PhaseId,
    double ClosureToleranceInternalFeet);

public sealed record ExportSummary(
    int Regions,
    int Rooms,
    int RoomsOnModelEvidence,
    int RoomsOnOperatorJudgement,
    int RegionsRejected,
    int Doors,
    int UnattributedBoundarySegments);

/// <summary>
/// The analysis of one plan, as plain data ready to be written out.
///
/// Built in Core from things already decided, so what the file says and what the
/// reports say cannot drift apart. Nothing here reaches for Revit; if a fact is
/// not in the analysis it does not appear in the file.
/// </summary>
public sealed record SpatialExport(
    string Schema,
    string Generated,
    ExportContext Context,
    ExportSummary Summary,
    IReadOnlyList<ExportedRoom> Rooms,
    IReadOnlyList<ExportedRejection> RegionsRejected,
    IReadOnlyList<ExportedDoor> Doors)
{
    /// <summary>
    /// Names the shape of this file so a consumer can tell whether it
    /// understands it, and so a later change to that shape is a visible event
    /// rather than a silent one.
    /// </summary>
    public const string SchemaName = "revit-spatial-analyzer/rooms/1";

    /// <summary>
    /// Square feet to square metres. A foot is exactly 0.3048 metres by
    /// definition, so this factor is exact rather than a rounded conversion.
    ///
    /// Areas are given in both: internal units because that is what Revit
    /// measured and what round-trips without loss, and square metres because a
    /// consumer should not have to know that Revit works in feet.
    /// </summary>
    public const double SquareMetresPerSquareFoot = 0.09290304;

    /// <param name="generated">
    /// When this was produced. Supplied rather than read from the clock so that
    /// a test can pin it, and so the only part of the file that changes between
    /// two runs over an unchanged model is the part that should.
    /// </param>
    public static SpatialExport Build(
        AnalysisContextInfo context,
        string documentTitle,
        string documentPath,
        double closureToleranceInternalFeet,
        IReadOnlyList<QualificationOutcome> outcomes,
        DoorAdjacencyIndex doors,
        IReadOnlyDictionary<RegionId, IReadOnlyList<ElementDescriptor>> elementsByRoom,
        string generated)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(outcomes);
        ArgumentNullException.ThrowIfNull(doors);
        ArgumentNullException.ThrowIfNull(elementsByRoom);

        var rooms = new List<ExportedRoom>();
        var rejected = new List<ExportedRejection>();

        // Region order throughout, so two runs over an unchanged model produce
        // the same file and a difference between them means something.
        foreach (QualificationOutcome outcome in outcomes.OrderBy(o => o.Region.Id))
        {
            if (outcome.Room is null)
            {
                rejected.Add(Describe(outcome));
                continue;
            }

            rooms.Add(Describe(outcome.Room, outcome.Explanation, elementsByRoom));
        }

        var exportedDoors = doors.Adjacencies
            .OrderBy(a => a.Door.Id.Value)
            .Select(a => new ExportedDoor(
                Describe(a.Door),
                a.Connection.ToString(),
                a.Regions.Select(r => r.ToString()).ToList()))
            .ToList();

        var summary = new ExportSummary(
            outcomes.Count,
            rooms.Count,
            rooms.Count(r => !r.RestsOnOperatorJudgement),
            rooms.Count(r => r.RestsOnOperatorJudgement),
            rejected.Count,
            exportedDoors.Count,
            rooms.Sum(r => r.UnattributedBoundary.Count));

        return new SpatialExport(
            SchemaName,
            generated,
            new ExportContext(
                documentTitle,
                documentPath,
                context.ViewName,
                context.ViewType,
                context.ViewId.Value,
                context.LevelName,
                context.LevelId.Value,
                context.LevelElevationInternalFeet,
                context.PhaseName,
                context.PhaseId.Value,
                closureToleranceInternalFeet),
            summary,
            rooms,
            rejected,
            exportedDoors);
    }

    private static ExportedRoom Describe(
        GranularRoom room,
        string qualification,
        IReadOnlyDictionary<RegionId, IReadOnlyList<ElementDescriptor>> elementsByRoom)
    {
        double squareFeet = room.Area.InternalSquareFeet;

        IReadOnlyList<ElementDescriptor> elements = elementsByRoom.TryGetValue(room.Id, out var found)
            ? found
            : Array.Empty<ElementDescriptor>();

        var unattributed = room.Loops
            .SelectMany(loop => loop.UnattributedSegments)
            .Select(segment => new ExportedUnattributedBoundary(
                segment.Curve.Kind.ToString(),
                segment.Curve.LengthInternalFeet))
            .ToList();

        return new ExportedRoom(
            room.Id.ToString(),
            squareFeet,
            squareFeet * SquareMetresPerSquareFoot,
            room.RestsOnOperatorJudgement,
            room.Loops.Count,
            qualification,
            room.Entrances
                .Select(e => new ExportedEntrance(e.Kind.ToString(), e.Authority.ToString(), Describe(e.Element)))
                .ToList(),
            elements
                .OrderBy(e => e.CategoryName, StringComparer.Ordinal)
                .ThenBy(e => e.FamilyName, StringComparer.Ordinal)
                .ThenBy(e => e.TypeName, StringComparer.Ordinal)
                .ThenBy(e => e.Id.Value)
                .Select(Describe)
                .ToList(),
            unattributed);
    }

    private static ExportedRejection Describe(QualificationOutcome outcome)
    {
        // A region rejected for not being enclosed has no area, and saying so is
        // the point: nothing is invented to fill the field.
        bool measured = outcome.Region.NetArea.TryGetInternalSquareFeet(out double squareFeet);

        return new ExportedRejection(
            outcome.Region.Id.ToString(),
            measured ? squareFeet : null,
            measured ? squareFeet * SquareMetresPerSquareFoot : null,
            outcome.Reason?.ToString() ?? "Unknown",
            outcome.Explanation);
    }

    private static ExportedElement Describe(ElementDescriptor element) =>
        new(element.CategoryName, element.FamilyName, element.TypeName, element.Id.Value);
}
