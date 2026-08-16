using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using SpatialAnalyzer.Core.Export;
using SpatialAnalyzer.Core.Spatial;
using SpatialAnalyzer.Revit.Boundaries;
using SpatialAnalyzer.Revit.Context;
using CoreBoundaryLoop = SpatialAnalyzer.Core.Geometry.BoundaryLoop;

namespace SpatialAnalyzer.Revit.Diagnostics;

/// <summary>
/// Reads every region on the level and everything on its boundary, then leaves.
///
/// Reading and judging are kept apart on purpose. Everything Revit has to say is
/// copied into plain data inside a transaction that is rolled back, so the model
/// is untouched and no temporary room outlives the read. Whatever happens next -
/// applying the rule, asking a person, applying it again with their answer -
/// happens outside, against data that no longer depends on the model being in
/// any particular state.
///
/// That separation is what makes asking a person possible at all: Revit will not
/// let a selection be made while a transaction is open.
/// </summary>
public static class RegionQualification
{
    private const SpatialElementBoundaryLocation BoundaryLocation = SpatialElementBoundaryLocation.Finish;

    /// <summary>A foot is exactly 0.3048 metres, so this factor is exact.</summary>
    private const double MillimetresPerFoot = 304.8;

    public sealed record RegionReading(
        CandidateRegion Region,
        IReadOnlyList<BoundaryFeatureCollector.Found> Features,
        double RevitAreaFeet2,
        string RoomLabel,
        bool WasRoomLocated,
        bool FoundBehindAnIgnoredWall);

    public sealed record Reading(
        IReadOnlyList<RegionReading> Regions,
        double ClosureToleranceFeet,
        int RoomsBefore,
        int RoomsAfter,
        TransactionStatus Status,
        PartitionSurvey.Result Partitions,
        IReadOnlyList<string> Failures);

    /// <summary>
    /// The read's own account of itself, in the shape the export writes.
    ///
    /// Lives here because this is what holds the facts, and is shared by both
    /// commands that export so the file and the dialog cannot disagree.
    /// </summary>
    public static ExportedReading Describe(Reading reading)
    {
        ArgumentNullException.ThrowIfNull(reading);

        // Sixty-six walls failing the same way produce sixty-six near-identical
        // lines, which is a worse diagnosis than one. Distinct first, then
        // capped - and the true total travels alongside, so a truncated list
        // cannot be mistaken for a complete one.
        var sample = reading.Failures.Distinct(StringComparer.Ordinal).Take(25).ToList();

        PartitionArrangement found = reading.Partitions.Arrangement;

        return new ExportedReading(
            reading.Partitions.WallsConsidered,
            found.ClosedLoops
                .Select(loop => new ExportedEnclosure(
                    loop.Area.InternalSquareFeet,
                    loop.Area.InternalSquareFeet * SpatialExport.SquareMetresPerSquareFoot,
                    loop.Walls.Select(w => w.Id.Value).ToList()))
                .ToList(),
            found.OpenChains
                .Select(chain => new ExportedOpenRun(
                    chain.GapBetweenNearestFreeEndsInternalFeet,
                    chain.GapBetweenNearestFreeEndsInternalFeet * MillimetresPerFoot,
                    chain.Walls.Select(w => w.Id.Value).ToList()))
                .ToList(),
            found.Tangled.Count,
            reading.Failures.Count,
            sample);
    }

    public static Reading Read(AnalysisContext context)
    {
        Document document = context.Document;
        var failures = new List<string>();

        // Revit's own threshold for geometry it cannot tell apart. Read from the
        // running application rather than written down, so the number comes from
        // the software that produced the boundary.
        var tolerance = new ClosureTolerance(document.Application.ShortCurveTolerance);

        int roomsBefore = CountRooms(document);
        List<RegionReading> regions;
        TransactionStatus status;

        // Walls Revit is told to ignore hide whole rooms, and Revit's topology
        // cannot be persuaded to divide a region for them - measured, not
        // assumed. What they enclose is worked out from their own geometry
        // instead, which needs nothing written and so happens outside the
        // transaction entirely.
        PartitionSurvey.Result partitions = PartitionSurvey.Of(context, tolerance.InternalFeet);
        failures.AddRange(partitions.Failures);

        using (var transaction = new Transaction(document, "Spatial Analyzer region reading"))
        {
            transaction.Start();
            try
            {
                regions = ReadRegions(context, tolerance, new HashSet<long>(), failures);
            }
            finally
            {
                status = transaction.RollBack();
            }
        }

        return new Reading(
            regions,
            tolerance.InternalFeet,
            roomsBefore,
            CountRooms(document),
            status,
            partitions,
            failures);
    }

    private static int CountRooms(Document document) =>
        new FilteredElementCollector(document)
            .OfCategory(BuiltInCategory.OST_Rooms)
            .WhereElementIsNotElementType()
            .GetElementCount();

    private static List<RegionReading> ReadRegions(
        AnalysisContext context,
        ClosureTolerance tolerance,
        HashSet<long> dividerLineIds,
        List<string> failures)
    {
        Document document = context.Document;
        var readings = new List<RegionReading>();

        PlanTopology topology = document.get_PlanTopology(context.Level, context.Phase);

        List<Room> existingRooms = new FilteredElementCollector(document)
            .OfCategory(BuiltInCategory.OST_Rooms)
            .WhereElementIsNotElementType()
            .OfType<Room>()
            .Where(r => r.LevelId == context.Level.Id && r.Area > 0)
            .ToList();

        var options = new SpatialElementBoundaryOptions
        {
            SpatialElementBoundaryLocation = BoundaryLocation,
        };

        var circuits = new List<PlanCircuit>();
        foreach (PlanCircuit circuit in topology.Circuits)
        {
            circuits.Add(circuit);
        }

        int index = 0;
        foreach (PlanCircuit circuit in circuits.OrderByDescending(c => c.Area))
        {
            int thisIndex = index++;

            try
            {
                Room? room = circuit.IsRoomLocated ? FindExistingRoom(circuit, context, existingRooms) : null;

                if (room is null)
                {
                    room = document.Create.NewRoom(null, circuit);
                    if (room is null)
                    {
                        failures.Add($"region {thisIndex}: Revit would not place a room in this circuit");
                        continue;
                    }

                    document.Regenerate();
                }

                IReadOnlyList<CoreBoundaryLoop> loops = BoundaryExtractor.Extract(room, options);

                // A region bounded by one of the lines we laid exists only
                // because a wall Revit was told to ignore was made to count.
                // That is a different kind of finding from a region the model
                // reports on its own, and the export says so.
                bool behindAnIgnoredWall = dividerLineIds.Count > 0
                    && loops.SelectMany(l => l.Segments)
                        .Any(s => dividerLineIds.Contains(s.Reference.ElementId.Value));

                readings.Add(new RegionReading(
                    new CandidateRegion(new RegionId(thisIndex), loops, tolerance.InternalFeet),
                    BoundaryFeatureCollector.Collect(document, room, options).ToList(),
                    room.Area,
                    $"{room.Number} {room.Name}".Trim(),
                    circuit.IsRoomLocated,
                    behindAnIgnoredWall));
            }
            catch (Exception exception)
            {
                failures.Add($"region {thisIndex}: {exception.GetType().Name}: {exception.Message}");
            }
        }

        return readings;
    }

    private static Room? FindExistingRoom(PlanCircuit circuit, AnalysisContext context, List<Room> rooms)
    {
        UV point = circuit.GetPointInside();
        var probe = new XYZ(point.U, point.V, context.Level.Elevation + 1.0);

        foreach (Room room in rooms)
        {
            if (room.IsPointInRoom(probe))
            {
                return room;
            }
        }

        return null;
    }
}
