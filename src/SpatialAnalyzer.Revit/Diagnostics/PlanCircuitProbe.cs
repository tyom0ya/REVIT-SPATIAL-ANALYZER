using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using SpatialAnalyzer.Core.Diagnostics;
using SpatialAnalyzer.Revit.Context;

namespace SpatialAnalyzer.Revit.Diagnostics;

/// <summary>
/// Answers whether Revit's plan circuits can serve as candidate regions, by
/// reading the boundary of the room in each circuit.
///
/// A circuit on its own exposes only an area, a side count and a point inside
/// it. The boundary is what the rest of the project needs, and a room is what
/// exposes it. Circuits that already hold a room are read through that room;
/// the rest are given a temporary one.
///
/// Temporary rooms are removed by rolling the transaction back. Everything is
/// copied into plain values first, and nothing is committed under any
/// circumstance.
/// </summary>
public static class PlanCircuitProbe
{
    /// <summary>
    /// Room boundaries are requested at the finish face because that is the
    /// surface enclosing the space a person occupies, which is what the
    /// analysis is about. The alternatives measure to wall centres or to the
    /// structural core, both of which describe construction rather than room.
    /// </summary>
    private const SpatialElementBoundaryLocation BoundaryLocation = SpatialElementBoundaryLocation.Finish;

    /// <summary>
    /// Endpoints closer together than this are treated as one physical
    /// location. About a third of a millimetre: floating point representation
    /// noise, far below any opening a building can contain.
    ///
    /// This is not licence to bridge a gap in the model. A discontinuity larger
    /// than this is reported with its measured size and left open, because
    /// joining it would manufacture an enclosure the building does not have.
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
        List<string> DoorsOnBoundary,
        int DoorsHostedByBoundaryWalls,
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
        report.Item("Closure tolerance (ft)", CoincidentPointToleranceFeet);
        report.Item("Generated", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));

        int roomsBefore = CountRooms(document);

        List<CircuitInfo> circuits;
        TransactionStatus status;

        // Everything touching the model happens in here. The using statement
        // matters: if anything throws, disposing an open transaction rolls it
        // back, so no path leaves temporary rooms behind.
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
        WriteUnattributedSegments(report, circuits);

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

        // Rooms already present on this level, so a circuit that holds one is
        // read through it rather than given a second. Revit makes a duplicate
        // room redundant with zero area, which was the flaw in the first
        // version of this probe.
        List<Room> existingRooms = new FilteredElementCollector(document)
            .OfCategory(BuiltInCategory.OST_Rooms)
            .WhereElementIsNotElementType()
            .OfType<Room>()
            .Where(r => r.LevelId == context.Level.Id && r.Area > 0)
            .ToList();

        Dictionary<long, List<FamilyInstance>> doorsByHost = BuildDoorsByHost(document);

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
            results.Add(Describe(document, context, circuit, index, options, existingRooms, doorsByHost));
            index++;
        }

        return results;
    }

    private static Dictionary<long, List<FamilyInstance>> BuildDoorsByHost(Document document)
    {
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

        return doorsByHost;
    }

    private static CircuitInfo Describe(
        Document document,
        AnalysisContext context,
        PlanCircuit circuit,
        int index,
        SpatialElementBoundaryOptions options,
        List<Room> existingRooms,
        Dictionary<long, List<FamilyInstance>> doorsByHost)
    {
        bool wasRoomLocated = circuit.IsRoomLocated;
        bool temporary = false;

        try
        {
            Room? room = wasRoomLocated ? FindExistingRoom(circuit, context, existingRooms) : null;

            if (room is null)
            {
                room = document.Create.NewRoom(null, circuit);
                temporary = true;

                if (room is null)
                {
                    return Failed(index, circuit, wasRoomLocated, "NewRoom returned null for this circuit.");
                }

                // Boundaries are unavailable until the model catches up with a
                // newly created room.
                document.Regenerate();
            }

            IList<IList<BoundarySegment>> loops = room.GetBoundarySegments(options);

            var loopInfos = new List<LoopInfo>();
            var curvesByBoundaryElement = new Dictionary<long, List<Curve>>();

            foreach (IList<BoundarySegment> loop in loops)
            {
                loopInfos.Add(DescribeLoop(document, loop, curvesByBoundaryElement));
            }

            (List<string> onBoundary, int hostedTotal) =
                FindDoorsOnBoundary(document, curvesByBoundaryElement, doorsByHost);

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
                onBoundary,
                hostedTotal,
                null);
        }
        catch (Exception exception)
        {
            // One awkward circuit must not cost the whole probe. Recording the
            // failure keeps it visible rather than leaving a silent hole.
            return Failed(index, circuit, wasRoomLocated, $"{exception.GetType().Name}: {exception.Message}");
        }
    }

    /// <summary>
    /// Finds the room already occupying a circuit.
    ///
    /// The circuit's own interior point is used as the probe. It arrives as a
    /// UV, a location on the plan rather than in space, so it is lifted to the
    /// level's elevation and raised slightly: a point exactly on the floor sits
    /// on the room's lower boundary, where containment is ambiguous.
    /// </summary>
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

    private static CircuitInfo Failed(int index, PlanCircuit circuit, bool wasRoomLocated, string failure) =>
        new(index, circuit.Area, circuit.SideNum, wasRoomLocated, false, "(none)", 0, 0,
            new List<LoopInfo>(), new List<string>(), 0, failure);

    private static LoopInfo DescribeLoop(
        Document document,
        IList<BoundarySegment> loop,
        Dictionary<long, List<Curve>> curvesByBoundaryElement)
    {
        var segments = new List<SegmentInfo>();
        var curves = new List<Curve>();

        foreach (BoundarySegment segment in loop)
        {
            Curve curve = segment.GetCurve();
            curves.Add(curve);

            // A boundary produced by a linked model reports the link instance
            // in ElementId and the element inside the link in LinkElementId.
            // Recording which is present keeps a link instance from being
            // mistaken for the wall that actually bounds the space.
            bool fromLink = segment.LinkElementId != ElementId.InvalidElementId;
            long elementId = segment.ElementId.Value;

            string categoryName = "(none)";
            if (fromLink)
            {
                categoryName = "(linked)";
            }
            else if (elementId != ElementId.InvalidElementId.Value)
            {
                Element? element = document.GetElement(segment.ElementId);
                categoryName = element?.Category?.Name ?? "(no category)";

                if (!curvesByBoundaryElement.TryGetValue(elementId, out List<Curve>? list))
                {
                    list = new List<Curve>();
                    curvesByBoundaryElement[elementId] = list;
                }

                list.Add(curve);
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
    /// Decides which doors actually open onto this space.
    ///
    /// Asking only whether a bounding wall hosts a door is far too generous: a
    /// long wall hosting six doors lends all six to every space it touches, so
    /// a four square metre cupboard came back with eight doors. The test here
    /// is geometric instead. A door is counted when its own location projects
    /// onto a boundary curve produced by its own host wall, within an allowance
    /// taken from that wall's measured thickness rather than a chosen number:
    /// the boundary runs along the finish face while the door sits on the wall's
    /// centre line, so the two are apart by about half the wall's width.
    /// </summary>
    private static (List<string> OnBoundary, int HostedTotal) FindDoorsOnBoundary(
        Document document,
        Dictionary<long, List<Curve>> curvesByBoundaryElement,
        Dictionary<long, List<FamilyInstance>> doorsByHost)
    {
        var onBoundary = new List<string>();
        int hostedTotal = 0;

        foreach ((long boundaryElementId, List<Curve> curves) in curvesByBoundaryElement)
        {
            if (!doorsByHost.TryGetValue(boundaryElementId, out List<FamilyInstance>? hosted))
            {
                continue;
            }

            hostedTotal += hosted.Count;

            double allowance = document.GetElement(new ElementId(boundaryElementId)) is Wall wall
                ? wall.Width
                : 1.0;

            foreach (FamilyInstance door in hosted)
            {
                if (door.Location is not LocationPoint location)
                {
                    continue;
                }

                foreach (Curve curve in curves)
                {
                    // Compare in plan. The boundary curve lies at the room's
                    // computation height and the door's location at its own,
                    // and that vertical difference is not evidence of anything.
                    XYZ curveStart = curve.GetEndPoint(0);
                    var flattened = new XYZ(location.Point.X, location.Point.Y, curveStart.Z);

                    IntersectionResult? projection = curve.Project(flattened);
                    if (projection is not null && projection.Distance <= allowance)
                    {
                        onBoundary.Add($"id {door.Id.Value} host {boundaryElementId} at {projection.Distance:0.###} ft");
                        break;
                    }
                }
            }
        }

        return (onBoundary.OrderBy(d => d, StringComparer.Ordinal).ToList(), hostedTotal);
    }

    /// <summary>
    /// Measures whether a loop closes, and by how much it misses.
    ///
    /// The gap is reported, never corrected. A discontinuity is evidence about
    /// the model and has to be understood on its own terms.
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
        report.Item("Read through an existing room", circuits.Count(c => c.Failure is null && !c.RoomWasTemporary));
        report.Item("Read through a temporary room", circuits.Count(c => c.RoomWasTemporary));
        report.Blank();

        List<CircuitInfo> ok = circuits.Where(c => c.Failure is null).ToList();
        report.Item("With at least one door on boundary", ok.Count(c => c.DoorsOnBoundary.Count > 0));
        report.Item("With no door on boundary", ok.Count(c => c.DoorsOnBoundary.Count == 0));
        report.Blank();
        report.Item("Single boundary loop", ok.Count(c => c.Loops.Count == 1));
        report.Item("Multiple boundary loops", ok.Count(c => c.Loops.Count > 1));
        report.Item("No boundary loop", ok.Count(c => c.Loops.Count == 0));
        report.Blank();
        report.Item("All loops closed", ok.Count(c => c.Loops.Count > 0 && c.Loops.All(l => l.IsClosed)));
        report.Item("Some loop open", ok.Count(c => c.Loops.Any(l => !l.IsClosed)));
    }

    private static void WriteCircuits(DiagnosticReport report, List<CircuitInfo> circuits)
    {
        report.Section("CIRCUITS");

        foreach (CircuitInfo circuit in circuits)
        {
            double circuitM2 = UnitUtils.ConvertFromInternalUnits(circuit.CircuitAreaFeet2, UnitTypeId.SquareMeters);
            report.Blank();
            report.Line($"[{circuit.Index}]  circuit area {circuitM2:0.##} m2   sides {circuit.SideNum}   " +
                        $"{(circuit.WasRoomLocated ? "roomed" : "UNROOMED")}");

            if (circuit.Failure is not null)
            {
                report.Line($"      FAILED: {circuit.Failure}");
                continue;
            }

            double roomM2 = UnitUtils.ConvertFromInternalUnits(circuit.RoomAreaFeet2, UnitTypeId.SquareMeters);
            string source = circuit.RoomWasTemporary ? "temporary room" : "existing room";
            report.Line($"      {source}: {circuit.RoomLabel}   area {roomM2:0.##} m2   perimeter {circuit.PerimeterFeet:0.##} ft");
            report.Line($"      loops {circuit.Loops.Count}   doors on boundary {circuit.DoorsOnBoundary.Count}" +
                        $"   (hosted by bounding walls: {circuit.DoorsHostedByBoundaryWalls})");

            for (int i = 0; i < circuit.Loops.Count; i++)
            {
                LoopInfo loop = circuit.Loops[i];
                string closure = loop.IsClosed
                    ? "closed"
                    : $"OPEN by {loop.LargestGapFeet:0.00000000} ft " +
                      $"({UnitUtils.ConvertFromInternalUnits(loop.LargestGapFeet, UnitTypeId.Millimeters):0.0000} mm)";
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

            foreach (string door in circuit.DoorsOnBoundary)
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

    /// <summary>
    /// Lists the boundary segments no element accounts for.
    ///
    /// These are recorded rather than quietly dropped. A boundary with no
    /// generating element cannot be reported in the export, and until it is
    /// understood it is not safe to assume it is harmless.
    /// </summary>
    private static void WriteUnattributedSegments(DiagnosticReport report, List<CircuitInfo> circuits)
    {
        report.Section("BOUNDARY SEGMENTS WITH NO GENERATING ELEMENT");

        var rows = new List<string>();
        foreach (CircuitInfo circuit in circuits)
        {
            for (int i = 0; i < circuit.Loops.Count; i++)
            {
                foreach (SegmentInfo segment in circuit.Loops[i].Segments.Where(s => s.CategoryName == "(none)"))
                {
                    double millimetres = UnitUtils.ConvertFromInternalUnits(segment.LengthFeet, UnitTypeId.Millimeters);
                    rows.Add($"  circuit {circuit.Index,-4} loop {i}   {segment.CurveType,-6} " +
                             $"length {segment.LengthFeet,8:0.###} ft ({millimetres:0.#} mm)   elementId {segment.ElementId}");
                }
            }
        }

        report.Item("Count", rows.Count);
        report.Blank();
        foreach (string row in rows)
        {
            report.Line(row);
        }
    }
}
