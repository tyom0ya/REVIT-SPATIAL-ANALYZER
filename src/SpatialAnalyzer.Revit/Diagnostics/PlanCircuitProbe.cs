using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using SpatialAnalyzer.Core.Diagnostics;
using SpatialAnalyzer.Revit.Context;

namespace SpatialAnalyzer.Revit.Diagnostics;

/// <summary>
/// Answers whether Revit's plan circuits can serve as candidate regions, by
/// giving every circuit a room and reading the boundary that results.
///
/// The audit established that plan topology finds far more circuits than the
/// model has rooms. What it could not say is whether the unroomed ones are real
/// spaces or artefacts, because a circuit on its own exposes only an area, a
/// side count and a point inside it. A room placed in the circuit exposes the
/// boundary, and the boundary is what the rest of the project needs.
///
/// The rooms created here are temporary. Everything read is copied into plain
/// values before the transaction is rolled back, so the model is left exactly
/// as it was found. Nothing is committed under any circumstance.
/// </summary>
public static class PlanCircuitProbe
{
    /// <summary>
    /// Room boundaries are requested at the finish face because that is the
    /// surface that encloses the space a person occupies, which is what the
    /// analysis is about. The alternatives measure to wall centres or to the
    /// structural core, both of which describe the construction rather than the
    /// room.
    /// </summary>
    private const SpatialElementBoundaryLocation BoundaryLocation = SpatialElementBoundaryLocation.Finish;

    /// <summary>
    /// Endpoints closer together than this are treated as the same physical
    /// location, and anything larger is reported as a real discontinuity rather
    /// than closed up. This is a numerical tolerance for floating point
    /// representation of one point, and is emphatically not licence to bridge a
    /// gap in the model: roughly a third of a millimetre, far below any opening
    /// a building could contain.
    /// </summary>
    private const double CoincidentPointToleranceFeet = 0.001;

    private sealed record SegmentInfo(
        long ElementId,
        string CategoryName,
        bool FromLink,
        string CurveType,
        double LengthFeet);

    private sealed record LoopInfo(
        int SegmentCount,
        bool IsClosed,
        double LargestGapFeet,
        List<SegmentInfo> Segments);

    private sealed record CircuitInfo(
        int Index,
        double CircuitAreaFeet2,
        int SideNum,
        bool WasRoomLocated,
        bool RoomWasTemporary,
        string RoomLabel,
        double RoomAreaFeet2,
        double PerimeterFeet,
        List<LoopInfo> Loops,
        int DoorCount,
        List<string> DoorDescriptions,
        string? Failure);

    public static DiagnosticReport Probe(AnalysisContext context)
    {
        Document document = context.Document;
        var report = new DiagnosticReport("REVIT SPATIAL ANALYZER - PLAN CIRCUIT PROBE");

        report.Section("SESSION");
        report.Item("Document", document.Title);
        report.Item("Path", string.IsNullOrEmpty(document.PathName) ? "(unsaved)" : document.PathName);
        report.Item("View", context.View.Name);
        report.Item("Level", context.Level.Name);
        report.Item("Phase", context.Phase.Name);
        report.Item("Boundary location", BoundaryLocation.ToString());
        report.Item("Generated", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));

        int roomsBefore = CountRooms(document);

        List<CircuitInfo> circuits;
        TransactionStatus status;

        // Everything that touches the model happens inside this block. The
        // using statement matters: should anything throw, disposing an open
        // transaction rolls it back, so there is no path that leaves temporary
        // rooms behind.
        using (var transaction = new Transaction(document, "Spatial Analyzer plan circuit probe"))
        {
            transaction.Start();
            try
            {
                circuits = Collect(context);
            }
            finally
            {
                status = transaction.RollBack();
            }
        }

        int roomsAfter = CountRooms(document);

        report.Section("ROLLBACK VERIFICATION");
        report.Item("Transaction status", status.ToString());
        report.Item("Rooms before probe", roomsBefore);
        report.Item("Rooms after probe", roomsAfter);
        bool clean = status == TransactionStatus.RolledBack && roomsBefore == roomsAfter;
        report.Item("Model unchanged", clean ? "yes" : "NO - INVESTIGATE");

        WriteSummary(report, circuits);
        WriteCircuits(report, circuits);
        WriteBoundaryCategoryCensus(report, circuits);

        return report;
    }

    private static int CountRooms(Document document) =>
        new FilteredElementCollector(document)
            .OfCategory(BuiltInCategory.OST_Rooms)
            .WhereElementIsNotElementType()
            .GetElementCount();

    private static List<CircuitInfo> Collect(AnalysisContext context)
    {
        Document document = context.Document;
        var results = new List<CircuitInfo>();

        PlanTopology topology = document.get_PlanTopology(context.Level, context.Phase);

        // Doors are hosted in walls, so they do not appear as boundary segments
        // themselves. Associating a door with a space therefore means asking
        // which wall hosts it and whether that wall bounds the space. This map
        // is built once rather than per circuit.
        var doorsByHost = new Dictionary<long, List<FamilyInstance>>();
        foreach (FamilyInstance door in new FilteredElementCollector(document)
                     .OfCategory(BuiltInCategory.OST_Doors)
                     .WhereElementIsNotElementType()
                     .OfType<FamilyInstance>())
        {
            Element? host = door.Host;
            if (host is null)
            {
                continue;
            }

            if (!doorsByHost.TryGetValue(host.Id.Value, out List<FamilyInstance>? list))
            {
                list = new List<FamilyInstance>();
                doorsByHost[host.Id.Value] = list;
            }

            list.Add(door);
        }

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
            results.Add(Describe(document, circuit, index, options, doorsByHost));
            index++;
        }

        return results;
    }

    private static CircuitInfo Describe(
        Document document,
        PlanCircuit circuit,
        int index,
        SpatialElementBoundaryOptions options,
        Dictionary<long, List<FamilyInstance>> doorsByHost)
    {
        bool wasRoomLocated = circuit.IsRoomLocated;
        Room? room = null;
        bool temporary = false;

        try
        {
            // Passing null asks Revit to create a room for this circuit. A
            // circuit that already holds one is given a room the same way; the
            // rollback removes whatever was created either way.
            room = document.Create.NewRoom(null, circuit);
            temporary = true;

            if (room is null)
            {
                return Failed(index, circuit, wasRoomLocated, "NewRoom returned null for this circuit.");
            }

            // Boundaries are not available until the model has caught up with
            // the newly created room.
            document.Regenerate();

            IList<IList<BoundarySegment>> loops = room.GetBoundarySegments(options);

            var loopInfos = new List<LoopInfo>();
            var boundaryElementIds = new HashSet<long>();

            foreach (IList<BoundarySegment> loop in loops)
            {
                loopInfos.Add(DescribeLoop(document, loop, boundaryElementIds));
            }

            var doors = new List<FamilyInstance>();
            foreach (long boundaryId in boundaryElementIds)
            {
                if (doorsByHost.TryGetValue(boundaryId, out List<FamilyInstance>? hosted))
                {
                    doors.AddRange(hosted);
                }
            }

            List<FamilyInstance> distinctDoors = doors
                .GroupBy(d => d.Id.Value)
                .Select(g => g.First())
                .OrderBy(d => d.Id.Value)
                .ToList();

            return new CircuitInfo(
                index,
                circuit.Area,
                circuit.SideNum,
                wasRoomLocated,
                temporary,
                $"{room.Number} {room.Name}".Trim(),
                room.Area,
                room.Perimeter,
                loopInfos,
                distinctDoors.Count,
                distinctDoors.Select(d => $"id {d.Id.Value} host {d.Host?.Id.Value}").ToList(),
                null);
        }
        catch (Exception exception)
        {
            // One awkward circuit must not cost the whole probe. Recording the
            // failure keeps it visible instead of leaving a silent hole in the
            // results.
            return Failed(index, circuit, wasRoomLocated, $"{exception.GetType().Name}: {exception.Message}");
        }
    }

    private static CircuitInfo Failed(int index, PlanCircuit circuit, bool wasRoomLocated, string failure) =>
        new(index, circuit.Area, circuit.SideNum, wasRoomLocated, false, "(none)", 0, 0,
            new List<LoopInfo>(), 0, new List<string>(), failure);

    private static LoopInfo DescribeLoop(
        Document document,
        IList<BoundarySegment> loop,
        HashSet<long> boundaryElementIds)
    {
        var segments = new List<SegmentInfo>();
        var curves = new List<Curve>();

        foreach (BoundarySegment segment in loop)
        {
            Curve curve = segment.GetCurve();
            curves.Add(curve);

            // A boundary produced by a linked model reports the link instance
            // in ElementId and the element inside the link in LinkElementId.
            // Recording which of the two is present keeps a link instance from
            // being mistaken for the wall that actually bounds the space.
            bool fromLink = segment.LinkElementId != ElementId.InvalidElementId;
            long elementId = segment.ElementId.Value;

            string categoryName = "(none)";
            if (!fromLink && elementId != ElementId.InvalidElementId.Value)
            {
                Element? element = document.GetElement(segment.ElementId);
                categoryName = element?.Category?.Name ?? "(no category)";
                boundaryElementIds.Add(elementId);
            }
            else if (fromLink)
            {
                categoryName = "(linked)";
            }

            segments.Add(new SegmentInfo(
                elementId,
                categoryName,
                fromLink,
                curve.GetType().Name,
                curve.Length));
        }

        (bool closed, double largestGap) = MeasureClosure(curves);

        return new LoopInfo(segments.Count, closed, largestGap, segments);
    }

    /// <summary>
    /// Measures whether a loop actually closes, and by how much it misses.
    ///
    /// The gap is reported rather than corrected. A discontinuity here is
    /// evidence about the model and has to be understood; quietly joining the
    /// ends would manufacture an enclosure that the building does not have.
    /// </summary>
    private static (bool Closed, double LargestGap) MeasureClosure(List<Curve> curves)
    {
        if (curves.Count == 0)
        {
            return (false, 0);
        }

        double largest = 0;
        for (int i = 0; i < curves.Count; i++)
        {
            XYZ end = curves[i].GetEndPoint(1);
            XYZ nextStart = curves[(i + 1) % curves.Count].GetEndPoint(0);
            largest = Math.Max(largest, end.DistanceTo(nextStart));
        }

        return (largest <= CoincidentPointToleranceFeet, largest);
    }

    private static void WriteSummary(DiagnosticReport report, List<CircuitInfo> circuits)
    {
        report.Section("SUMMARY");
        report.Item("Circuits probed", circuits.Count);
        report.Item("  succeeded", circuits.Count(c => c.Failure is null));
        report.Item("  failed", circuits.Count(c => c.Failure is not null));
        report.Blank();
        report.Item("Originally room located", circuits.Count(c => c.WasRoomLocated));
        report.Item("Originally unroomed", circuits.Count(c => !c.WasRoomLocated));
        report.Blank();

        List<CircuitInfo> ok = circuits.Where(c => c.Failure is null).ToList();
        report.Item("With at least one door", ok.Count(c => c.DoorCount > 0));
        report.Item("With no door", ok.Count(c => c.DoorCount == 0));
        report.Blank();
        report.Item("Single boundary loop", ok.Count(c => c.Loops.Count == 1));
        report.Item("Multiple boundary loops", ok.Count(c => c.Loops.Count > 1));
        report.Item("No boundary loop", ok.Count(c => c.Loops.Count == 0));
        report.Blank();
        report.Item("All loops closed", ok.Count(c => c.Loops.Count > 0 && c.Loops.All(l => l.IsClosed)));
        report.Item("Some loop open", ok.Count(c => c.Loops.Any(l => !l.IsClosed)));
        report.Blank();
        report.Line("  A circuit with no door cannot be an enclosed space a person enters,");
        report.Line("  which is the entrance rule the brief states. Whether that alone");
        report.Line("  separates real spaces from artefacts is what these numbers show.");
    }

    private static void WriteCircuits(DiagnosticReport report, List<CircuitInfo> circuits)
    {
        report.Section("CIRCUITS");

        foreach (CircuitInfo circuit in circuits)
        {
            double circuitM2 = UnitUtils.ConvertFromInternalUnits(circuit.CircuitAreaFeet2, UnitTypeId.SquareMeters);
            report.Blank();
            report.Line($"[{circuit.Index}]  circuit area {circuitM2:0.##} m2   sides {circuit.SideNum}   " +
                        $"originally {(circuit.WasRoomLocated ? "roomed" : "UNROOMED")}");

            if (circuit.Failure is not null)
            {
                report.Line($"      FAILED: {circuit.Failure}");
                continue;
            }

            double roomM2 = UnitUtils.ConvertFromInternalUnits(circuit.RoomAreaFeet2, UnitTypeId.SquareMeters);
            report.Line($"      room {circuit.RoomLabel}   area {roomM2:0.##} m2   perimeter {circuit.PerimeterFeet:0.##} ft");
            report.Line($"      loops {circuit.Loops.Count}   doors {circuit.DoorCount}");

            for (int i = 0; i < circuit.Loops.Count; i++)
            {
                LoopInfo loop = circuit.Loops[i];
                string closure = loop.IsClosed ? "closed" : $"OPEN by {loop.LargestGapFeet:0.####} ft";
                report.Line($"        loop {i}: {loop.SegmentCount} segments, {closure}");

                var categories = loop.Segments
                    .GroupBy(s => s.CategoryName, StringComparer.Ordinal)
                    .OrderBy(g => g.Key, StringComparer.Ordinal)
                    .Select(g => $"{g.Key} x{g.Count()}");
                report.Line($"          bounded by: {string.Join(", ", categories)}");

                var curveTypes = loop.Segments
                    .GroupBy(s => s.CurveType, StringComparer.Ordinal)
                    .OrderBy(g => g.Key, StringComparer.Ordinal)
                    .Select(g => $"{g.Key} x{g.Count()}");
                report.Line($"          curve types: {string.Join(", ", curveTypes)}");
            }

            foreach (string door in circuit.DoorDescriptions)
            {
                report.Line($"        door {door}");
            }
        }
    }

    private static void WriteBoundaryCategoryCensus(DiagnosticReport report, List<CircuitInfo> circuits)
    {
        report.Section("WHAT ACTUALLY BOUNDS THESE SPACES");

        var census = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (SegmentInfo segment in circuits.SelectMany(c => c.Loops).SelectMany(l => l.Segments))
        {
            census.TryGetValue(segment.CategoryName, out long current);
            census[segment.CategoryName] = current + 1;
        }

        report.Line("  Boundary segments by the category of the element that produced them.");
        report.Line("  This is the evidence for which categories qualify as boundaries,");
        report.Line("  rather than a list decided in advance.");
        report.Blank();
        report.Census(census);
    }
}
