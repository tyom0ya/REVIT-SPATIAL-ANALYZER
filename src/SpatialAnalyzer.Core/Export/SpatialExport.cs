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
/// What the read had to do to see the plan at all, and what it could not do.
///
/// Walls the model is told to ignore hide whole rooms, so the read lays a room
/// separation line along each one inside the transaction it rolls back. Whether
/// that worked is not visible in the room list: a read that laid no lines and a
/// read that laid them to no effect both produce exactly the number of regions
/// the model reports on its own.
///
/// Those two have different causes and different fixes, so the counts are
/// written into the file rather than shown once in a dialog and lost. A file
/// that quietly reported the model's own answer, while the tool believed it had
/// found more, is the failure this section exists to make impossible.
/// </summary>
/// <param name="FailureCount">
/// Every problem the read met, including those beyond the sample below. A count
/// larger than the list means the list is truncated, which is worth knowing
/// before concluding from it.
/// </param>
/// <summary>
/// A space enclosed by walls the model ignores for rooms - a room the model
/// contains and does not report.
/// </summary>
public sealed record ExportedEnclosure(
    double AreaInternalSquareFeet,
    double AreaSquareMetres,
    IReadOnlyList<long> WallIds);

/// <summary>
/// A run of ignored walls that does not close, and how far short it falls.
///
/// Exported at whatever size the gap is, and never acted on. A run that misses
/// by two millimetres and a run that misses by a metre are the same kind of
/// finding here: walls that would enclose a space if something joined their
/// ends. Whether that something is a missing wall or a doorway is a question
/// about the building, and the answer is not in the geometry.
/// </summary>
public sealed record ExportedOpenRun(
    double GapInternalFeet,
    double GapMillimetres,
    IReadOnlyList<long> WallIds);

/// <param name="TangledWalls">
/// Walls that take part in an enclosure but meet where more than two walls come
/// together, so which of the several possible rings is a room cannot be read
/// off the lines. Counted, not guessed at.
/// </param>
public sealed record ExportedReading(
    int WallsIgnoredForRooms,
    IReadOnlyList<ExportedEnclosure> Enclosures,
    IReadOnlyList<ExportedOpenRun> OpenRuns,
    int TangledWalls,
    int FailureCount,
    IReadOnlyList<string> Failures);

/// <summary>
/// The analysis of one plan, as plain data ready to be written out.
///
/// Built in Core from things already decided, so what the file says and what the
/// reports say cannot drift apart. Nothing here reaches for Revit; if a fact is
/// not in the analysis it does not appear in the file.
/// </summary>
public sealed record SpatialExport(
    string Schema,
    string Scope,
    string Generated,
    ExportContext Context,
    ExportSummary Summary,
    ExportedReading? Reading,
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
    /// A file describing every region on the plan.
    /// </summary>
    public const string WholePlan = "plan";

    /// <summary>
    /// A file describing one room and nothing else.
    ///
    /// Written into the file because the summary counts what the file contains,
    /// and a one-room export would otherwise report a plan of one region. A
    /// consumer that could not tell the two apart would read a fragment as the
    /// whole building.
    /// </summary>
    public static string OneRoom(RegionId room) => $"room {room}";

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
    /// <param name="scope">
    /// What this file covers - the whole plan, or one room. The summary counts
    /// what is in the file, so without this a fragment could be read as the
    /// whole building.
    /// </param>
    /// <param name="doors">
    /// The doors to describe. Taking a list rather than the whole index is what
    /// lets a single-room file carry only the doors that touch that room.
    /// </param>
    /// <param name="reading">
    /// How the regions were obtained, if the caller knows. Null means not
    /// recorded rather than nothing to report - a file that printed zeroes for
    /// a read it never observed would be inventing evidence.
    /// </param>
    public static SpatialExport Build(
        string scope,
        AnalysisContextInfo context,
        string documentTitle,
        string documentPath,
        double closureToleranceInternalFeet,
        IReadOnlyList<QualificationOutcome> outcomes,
        IReadOnlyList<DoorAdjacency> doors,
        IReadOnlyDictionary<RegionId, IReadOnlyList<ElementDescriptor>> elementsByRoom,
        string generated,
        ExportedReading? reading = null)
    {
        ArgumentNullException.ThrowIfNull(scope);
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

        var exportedDoors = doors
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
            scope,
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
            reading,
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
