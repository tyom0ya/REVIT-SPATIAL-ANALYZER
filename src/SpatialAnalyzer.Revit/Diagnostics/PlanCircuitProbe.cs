using System.Globalization;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using SpatialAnalyzer.Core.Diagnostics;
using SpatialAnalyzer.Core.Spatial;
using SpatialAnalyzer.Revit.Boundaries;
using SpatialAnalyzer.Revit.Context;
using CoreBoundaryLoop = SpatialAnalyzer.Core.Geometry.BoundaryLoop;
using CoreBoundarySource = SpatialAnalyzer.Core.Geometry.BoundarySource;

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

    /// <summary>
    /// A wall embedded in a wall that bounds a space, described in enough detail
    /// to say whether a person could walk through it.
    ///
    /// The entrance rule cannot simply count embedded walls. One space is
    /// entered through a storefront embedded in a chase wall, but three others
    /// have embedded walls that are plainly not ways in: a stair glazing panel,
    /// two runs of exterior rainscreen and something called "_Not Defined". What
    /// separates them has to come from the model.
    /// </summary>
    private sealed record EmbeddedWallInfo(
        long HostWallId,
        long WallId,
        string TypeName,
        string Kind,
        double WidthFeet,
        double LengthFeet,
        int PanelCount,
        List<string> Panels,
        List<string> Inserts);

    private sealed record BoundaryElementInfo(
        long ElementId,
        string CategoryName,
        string TypeName,
        string WallKind,
        List<string> Inserts,
        List<EmbeddedWallInfo> EmbeddedWalls);

    /// <summary>
    /// What the candidate region built from these loops makes of them, at one
    /// stated closure tolerance.
    /// </summary>
    private sealed record RegionInfo(
        double ToleranceFeet,
        bool IsEnclosed,
        double? NetAreaFeet2,
        int InnerLoopCount,
        int? OuterLoopIndex,
        List<string> LoopWindings);

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
        List<BoundaryElementInfo> BoundaryElements,
        RegionInfo? AtProjectTolerance,
        RegionInfo? AtRevitTolerance,
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
        report.Item("Closure tolerance, this project (ft)", CoincidentPointToleranceFeet);

        // Read from Revit rather than quoted from documentation. The project's
        // own tolerance was chosen before these were known, and one circuit sits
        // just the wrong side of it, so what Revit itself treats as
        // indistinguishable is the number that decides whether that is a real
        // discontinuity or two records of one location.
        double revitShortCurveTolerance = document.Application.ShortCurveTolerance;
        double revitVertexTolerance = document.Application.VertexTolerance;
        report.Item("Revit ShortCurveTolerance (ft)", revitShortCurveTolerance);
        report.Item("Revit VertexTolerance (ft)", revitVertexTolerance);
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
                circuits = Collect(context, revitShortCurveTolerance);
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
        WriteAreaCrossCheck(report, circuits);
        WriteEntranceInvestigation(report, circuits);
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

    private static List<CircuitInfo> Collect(AnalysisContext context, double revitShortCurveTolerance)
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
            results.Add(Describe(
                document, context, circuit, index, options, existingRooms, doorsByHost, revitShortCurveTolerance));
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
        Dictionary<long, List<FamilyInstance>> doorsByHost,
        double revitShortCurveTolerance)
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

            // Reported from the extractor's plain data rather than from Revit
            // directly, so that this report measures what the rest of the
            // application will actually see. If the copy lost anything, these
            // numbers would drift from the previous run's.
            IReadOnlyList<CoreBoundaryLoop> loops = BoundaryExtractor.Extract(room, options);

            var loopInfos = new List<LoopInfo>(loops.Count);
            foreach (CoreBoundaryLoop loop in loops)
            {
                loopInfos.Add(DescribeLoop(document, loop));
            }

            // The door test still needs Revit curves, which can project a
            // point. Collected independently on purpose: two separate readings
            // of the same boundary make the comparison above mean something.
            Dictionary<long, List<Curve>> curvesByBoundaryElement =
                CollectCurvesByElement(room, options);

            (List<string> onBoundary, int hostedTotal) =
                FindDoorsOnBoundary(document, curvesByBoundaryElement, doorsByHost);

            List<BoundaryElementInfo> boundaryElements =
                DescribeBoundaryElements(document, curvesByBoundaryElement.Keys);

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
                boundaryElements,
                DescribeRegion(index, loops, CoincidentPointToleranceFeet),
                DescribeRegion(index, loops, revitShortCurveTolerance),
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
            new List<LoopInfo>(), new List<string>(), 0, new List<BoundaryElementInfo>(), null, null, failure);

    /// <summary>
    /// Builds the candidate region these loops describe, and records what it
    /// makes of them.
    ///
    /// This is the point of the exercise. The region computes its own area from
    /// the extracted geometry, with interior voids taken out, while Revit
    /// computed one independently for the same room. Two derivations of one
    /// number from one boundary either agree or reveal which is wrong.
    /// </summary>
    private static RegionInfo DescribeRegion(int index, IReadOnlyList<CoreBoundaryLoop> loops, double tolerance)
    {
        var region = new CandidateRegion(new RegionId(index), loops, tolerance);

        var windings = new List<string>(region.LoopAreas.Count);
        foreach (var area in region.LoopAreas)
        {
            windings.Add(area.IsMeasured ? area.Winding.ToString() : "open");
        }

        // Recorded so the convention that Revit returns the outer loop first can
        // be checked against the model rather than assumed. The region itself
        // identifies the outer loop by measured area and never by position.
        int? outerIndex = null;
        for (int i = 0; i < region.Loops.Count; i++)
        {
            if (ReferenceEquals(region.Loops[i], region.OuterLoop))
            {
                outerIndex = i;
                break;
            }
        }

        return new RegionInfo(
            tolerance,
            region.IsEnclosed,
            region.NetArea.TryGetInternalSquareFeet(out double net) ? net : null,
            region.InnerLoops.Count,
            outerIndex,
            windings);
    }

    /// <summary>
    /// Describes each element bounding a space, and everything inserted into it.
    ///
    /// Counting doors hosted in bounding walls answers only "is there a door".
    /// A space with no door may still be entered - through a cased opening, a
    /// gap, or an embedded curtain wall - and the brief's entrance rule cannot
    /// be judged without knowing which. FindInserts is used rather than a
    /// hand-built host map because it reports rectangular openings and embedded
    /// walls too, neither of which appear as hosted family instances.
    /// </summary>
    private static List<BoundaryElementInfo> DescribeBoundaryElements(
        Document document,
        IEnumerable<long> boundaryElementIds)
    {
        var results = new List<BoundaryElementInfo>();

        foreach (long id in boundaryElementIds.OrderBy(i => i))
        {
            Element? element = document.GetElement(new ElementId(id));
            if (element is null)
            {
                continue;
            }

            string typeName = document.GetElement(element.GetTypeId()) is ElementType type
                ? type.Name
                : "(no type)";

            string wallKind = element is Wall wall
                ? (document.GetElement(wall.GetTypeId()) as WallType)?.Kind.ToString() ?? "(unknown)"
                : "-";

            var inserts = new List<string>();
            var embedded = new List<EmbeddedWallInfo>();
            if (element is HostObject host)
            {
                // The flags ask for rectangular openings and for embedded walls
                // and their inserts, which is precisely what a space entered
                // without a door is likely to be relying on.
                foreach (ElementId insertId in host.FindInserts(true, false, true, true))
                {
                    Element? insert = document.GetElement(insertId);
                    if (insert is null)
                    {
                        continue;
                    }

                    inserts.Add(string.Create(
                        CultureInfo.InvariantCulture,
                        $"{insert.Category?.Name ?? "(no category)"} id {insertId.Value} \"{insert.Name}\""));

                    if (insert is Wall embeddedWall)
                    {
                        embedded.Add(DescribeEmbeddedWall(document, id, embeddedWall));
                    }
                }
            }

            results.Add(new BoundaryElementInfo(
                id,
                element.Category?.Name ?? "(no category)",
                typeName,
                wallKind,
                inserts.OrderBy(i => i, StringComparer.Ordinal).ToList(),
                embedded.OrderBy(e => e.WallId).ToList()));
        }

        return results;
    }

    /// <summary>
    /// Describes a wall embedded in a bounding wall, in enough detail to judge
    /// whether it admits a person.
    ///
    /// A curtain wall is reported through its panels, because that is where the
    /// answer lives: a storefront that someone walks through has a door among
    /// its panels, while a glazing panel is glass all the way across. Panels are
    /// grouped and counted rather than listed one by one, so a wall with sixty
    /// identical panes does not bury the one that matters.
    /// </summary>
    private static EmbeddedWallInfo DescribeEmbeddedWall(Document document, long hostWallId, Wall wall)
    {
        string typeName = document.GetElement(wall.GetTypeId()) is ElementType type ? type.Name : "(no type)";
        string kind = (document.GetElement(wall.GetTypeId()) as WallType)?.Kind.ToString() ?? "(unknown)";
        double length = (wall.Location as LocationCurve)?.Curve.Length ?? 0;

        var panelCensus = new Dictionary<string, long>(StringComparer.Ordinal);
        int panelCount = 0;

        // Null for anything that is not a curtain wall, which is the common case.
        CurtainGrid? curtainGrid = wall.CurtainGrid;
        if (curtainGrid is not null)
        {
            foreach (ElementId panelId in curtainGrid.GetPanelIds())
            {
                panelCount++;

                Element? panel = document.GetElement(panelId);
                if (panel is null)
                {
                    continue;
                }

                string panelType = document.GetElement(panel.GetTypeId()) is ElementType t ? t.Name : "(no type)";
                string key = string.Create(
                    CultureInfo.InvariantCulture,
                    $"{panel.Category?.Name ?? "(no category)"} / {panelType}");

                panelCensus.TryGetValue(key, out long current);
                panelCensus[key] = current + 1;
            }
        }

        var inserts = new List<string>();
        foreach (ElementId insertId in wall.FindInserts(true, false, true, true))
        {
            Element? insert = document.GetElement(insertId);
            if (insert is null)
            {
                continue;
            }

            inserts.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"{insert.Category?.Name ?? "(no category)"} id {insertId.Value} \"{insert.Name}\""));
        }

        var panels = panelCensus
            .OrderBy(e => e.Key, StringComparer.Ordinal)
            .Select(e => string.Create(CultureInfo.InvariantCulture, $"{e.Key} x{e.Value}"))
            .ToList();

        return new EmbeddedWallInfo(
            hostWallId,
            wall.Id.Value,
            typeName,
            kind,
            wall.Width,
            length,
            panelCount,
            panels,
            inserts.OrderBy(i => i, StringComparer.Ordinal).ToList());
    }

    private static LoopInfo DescribeLoop(Document document, CoreBoundaryLoop loop)
    {
        var segments = new List<SegmentInfo>(loop.Segments.Count);

        foreach (var segment in loop.Segments)
        {
            string categoryName = segment.Reference.Source switch
            {
                CoreBoundarySource.Linked => "(linked)",
                CoreBoundarySource.None => "(none)",
                _ => document.GetElement(new ElementId(segment.Reference.ElementId.Value))?.Category?.Name
                     ?? "(no category)",
            };

            segments.Add(new SegmentInfo(
                segment.Reference.ElementId.Value,
                categoryName,
                segment.Reference.Source == CoreBoundarySource.Linked,
                segment.Curve.Kind.ToString(),
                segment.Curve.LengthInternalFeet));
        }

        // Closure is asked of the loop itself, with the tolerance stated here.
        // The loop measures and reports; it offers nothing that would close it.
        return new LoopInfo(
            segments.Count,
            loop.IsClosedWithin(CoincidentPointToleranceFeet),
            loop.LargestGapInternalFeet,
            segments);
    }

    /// <summary>
    /// Groups the room's boundary curves by the element that produced them,
    /// for the door projection test, which needs Revit's own curve arithmetic.
    /// </summary>
    private static Dictionary<long, List<Curve>> CollectCurvesByElement(
        Room room,
        SpatialElementBoundaryOptions options)
    {
        var byElement = new Dictionary<long, List<Curve>>();

        foreach (IList<BoundarySegment> loop in room.GetBoundarySegments(options))
        {
            foreach (BoundarySegment segment in loop)
            {
                if (segment.LinkElementId != ElementId.InvalidElementId ||
                    segment.ElementId == ElementId.InvalidElementId)
                {
                    continue;
                }

                long elementId = segment.ElementId.Value;
                if (!byElement.TryGetValue(elementId, out List<Curve>? curves))
                {
                    curves = new List<Curve>();
                    byElement[elementId] = curves;
                }

                curves.Add(segment.GetCurve());
            }
        }

        return byElement;
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
                        onBoundary.Add(string.Create(
                            CultureInfo.InvariantCulture,
                            $"id {door.Id.Value} host {boundaryElementId} at {projection.Distance:0.###} ft"));
                        break;
                    }
                }
            }
        }

        return (onBoundary.OrderBy(d => d, StringComparer.Ordinal).ToList(), hostedTotal);
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

    /// <summary>
    /// Checks the area this project computes against the area Revit computed.
    ///
    /// Both come from the same boundary, by different routes. Ours is measured
    /// from the extracted geometry, using the tessellation for curves and taking
    /// interior voids out; Revit's comes from its own room. Agreement is
    /// evidence that the copy is faithful, that voids are being subtracted and
    /// not counted, and that the loop identified as the outer one really is the
    /// outer one - none of which the unit tests can establish, because they use
    /// shapes this project wrote itself.
    ///
    /// Both tolerances are reported. The project's own was chosen before Revit's
    /// were known, and one circuit's boundary falls between them, so this is the
    /// evidence for which number should decide whether a discontinuity is a real
    /// opening or one location recorded twice.
    /// </summary>
    private static void WriteAreaCrossCheck(DiagnosticReport report, List<CircuitInfo> circuits)
    {
        report.Section("AREA CROSS-CHECK (this project vs Revit, same boundary)");

        List<CircuitInfo> ok = circuits.Where(c => c.Failure is null && c.AtProjectTolerance is not null).ToList();

        report.Item("Enclosed at this project's tolerance", ok.Count(c => c.AtProjectTolerance!.IsEnclosed));
        report.Item("Enclosed at Revit's ShortCurveTolerance", ok.Count(c => c.AtRevitTolerance!.IsEnclosed));
        report.Blank();

        var deviations = new List<double>();
        var rows = new List<string>();

        foreach (CircuitInfo circuit in ok)
        {
            RegionInfo atRevit = circuit.AtRevitTolerance!;
            double revitM2 = UnitUtils.ConvertFromInternalUnits(circuit.RoomAreaFeet2, UnitTypeId.SquareMeters);

            if (atRevit.NetAreaFeet2 is not double ours)
            {
                rows.Add(string.Create(
                    CultureInfo.InvariantCulture,
                    $"  [{circuit.Index,-3}] revit {revitM2,9:0.###} m2   ours        -         NOT ENCLOSED even at Revit's own tolerance"));
                continue;
            }

            double oursM2 = UnitUtils.ConvertFromInternalUnits(ours, UnitTypeId.SquareMeters);
            double deviation = oursM2 - revitM2;

            // Relative to Revit's figure, which is the one being checked against.
            double relative = revitM2 > 0 ? Math.Abs(deviation) / revitM2 : 0;
            deviations.Add(relative);

            string voids = atRevit.InnerLoopCount > 0
                ? string.Create(CultureInfo.InvariantCulture, $"   voids {atRevit.InnerLoopCount}")
                : string.Empty;

            rows.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"  [{circuit.Index,-3}] revit {revitM2,9:0.###} m2   ours {oursM2,9:0.###} m2   diff {deviation,9:0.000000} m2 ({relative:P4}){voids}"));
        }

        if (deviations.Count > 0)
        {
            report.Item("Circuits compared", deviations.Count);
            report.Item("Largest relative difference", deviations.Max().ToString("P6", CultureInfo.InvariantCulture));
            report.Item("Mean relative difference", deviations.Average().ToString("P6", CultureInfo.InvariantCulture));
        }

        report.Blank();
        foreach (string row in rows)
        {
            report.Line(row);
        }

        report.Blank();
        report.Line("  Loop classification, at Revit's tolerance:");
        report.Line("  Which loop the region picked as the outer one, and which way each was drawn.");
        report.Blank();

        var windingCensus = new Dictionary<string, long>(StringComparer.Ordinal);
        int outerWasFirst = 0;
        int multiLoop = 0;

        foreach (CircuitInfo circuit in ok)
        {
            RegionInfo atRevit = circuit.AtRevitTolerance!;

            if (atRevit.OuterLoopIndex == 0)
            {
                outerWasFirst++;
            }

            if (atRevit.LoopWindings.Count > 1)
            {
                multiLoop++;
                string outer = atRevit.OuterLoopIndex?.ToString(CultureInfo.InvariantCulture) ?? "none";
                report.Line(string.Create(
                    CultureInfo.InvariantCulture,
                    $"  [{circuit.Index,-3}] loops {atRevit.LoopWindings.Count}   outer is loop {outer}   windings: {string.Join(", ", atRevit.LoopWindings)}"));
            }

            for (int i = 0; i < atRevit.LoopWindings.Count; i++)
            {
                string key = i == atRevit.OuterLoopIndex
                    ? $"outer {atRevit.LoopWindings[i]}"
                    : $"inner {atRevit.LoopWindings[i]}";
                windingCensus.TryGetValue(key, out long current);
                windingCensus[key] = current + 1;
            }
        }

        report.Blank();
        report.Item(
            "Regions where the outer loop was also the first",
            string.Create(CultureInfo.InvariantCulture, $"{outerWasFirst} of {ok.Count}"));
        report.Item("Regions with more than one loop", multiLoop);
        report.Blank();
        report.Census(windingCensus);
    }

    /// <summary>
    /// Examines the spaces the entrance rule would reject.
    ///
    /// The brief's initial rule is that a space qualifies only if it has a
    /// door. One space this rejects is a large interior room, so the rule is
    /// either too strict or the entrance is of a kind not yet looked for. This
    /// section lists everything bounding those spaces and everything inserted
    /// into it, so the answer comes from the model rather than from relaxing
    /// the rule until the count looks right.
    /// </summary>
    private static void WriteEntranceInvestigation(DiagnosticReport report, List<CircuitInfo> circuits)
    {
        report.Section("ENTRANCE INVESTIGATION (spaces with no door on their boundary)");

        List<CircuitInfo> doorless = circuits
            .Where(c => c.Failure is null && c.DoorsOnBoundary.Count == 0)
            .ToList();

        report.Item("Spaces with no door", doorless.Count);
        report.Line("  Everything bounding each, and everything inserted into it.");

        foreach (CircuitInfo circuit in doorless)
        {
            double squareMetres = UnitUtils.ConvertFromInternalUnits(circuit.CircuitAreaFeet2, UnitTypeId.SquareMeters);
            report.Blank();
            report.Line(string.Create(
                CultureInfo.InvariantCulture,
                $"[{circuit.Index}]  {squareMetres:0.##} m2   loops {circuit.Loops.Count}   bounding elements {circuit.BoundaryElements.Count}"));

            foreach (BoundaryElementInfo element in circuit.BoundaryElements)
            {
                string kind = element.WallKind == "-" ? string.Empty : $"  kind {element.WallKind}";
                report.Line(string.Create(
                    CultureInfo.InvariantCulture,
                    $"      {element.CategoryName,-22} id {element.ElementId,-10} \"{element.TypeName}\"{kind}"));

                foreach (string insert in element.Inserts)
                {
                    report.Line($"          insert: {insert}");
                }
            }
        }

        WriteEmbeddedWalls(report, doorless);

        report.Blank();
        report.Line("  Insert categories across every space with no door:");
        var census = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (string insert in doorless.SelectMany(c => c.BoundaryElements).SelectMany(e => e.Inserts))
        {
            string category = insert.Split(" id ")[0];
            census.TryGetValue(category, out long current);
            census[category] = current + 1;
        }

        if (census.Count == 0)
        {
            report.Line("    none - nothing at all is inserted into any of their boundaries");
        }
        else
        {
            report.Census(census);
        }
    }

    /// <summary>
    /// Lists every wall embedded in the boundary of a space with no door.
    ///
    /// This is the evidence the entrance rule needs and does not yet have. One
    /// of these spaces is a real room, entered through a storefront embedded in
    /// a chase wall. Three others also have embedded walls and are plainly not
    /// rooms: a stair glazing panel, two runs of exterior rainscreen, and one
    /// wall whose type is called "_Not Defined". A rule that counted embedded
    /// walls would admit all four.
    ///
    /// So the question is what tells them apart, and it is asked of the model
    /// rather than answered from the type names, which are a naming convention
    /// this project has no right to rely on. Kind, width, length, curtain panels
    /// and inserts are all reported; whichever of them actually separates the
    /// one from the other three becomes the rule.
    /// </summary>
    private static void WriteEmbeddedWalls(DiagnosticReport report, List<CircuitInfo> doorless)
    {
        report.Blank();
        report.Line("  Walls embedded in these boundaries, in full:");

        var rows = doorless
            .SelectMany(c => c.BoundaryElements.SelectMany(e => e.EmbeddedWalls.Select(w => (Circuit: c, Wall: w))))
            .ToList();

        if (rows.Count == 0)
        {
            report.Line("    none");
            return;
        }

        foreach ((CircuitInfo circuit, EmbeddedWallInfo wall) in rows)
        {
            double squareMetres = UnitUtils.ConvertFromInternalUnits(circuit.CircuitAreaFeet2, UnitTypeId.SquareMeters);
            double widthMm = UnitUtils.ConvertFromInternalUnits(wall.WidthFeet, UnitTypeId.Millimeters);
            double lengthMm = UnitUtils.ConvertFromInternalUnits(wall.LengthFeet, UnitTypeId.Millimeters);

            report.Blank();
            report.Line(string.Create(
                CultureInfo.InvariantCulture,
                $"    circuit [{circuit.Index}] ({squareMetres:0.##} m2)   embedded wall id {wall.WallId} in host {wall.HostWallId}"));
            report.Line(string.Create(
                CultureInfo.InvariantCulture,
                $"      type \"{wall.TypeName}\"   kind {wall.Kind}   width {widthMm:0.#} mm   length {lengthMm:0.#} mm"));
            report.Line(string.Create(
                CultureInfo.InvariantCulture,
                $"      curtain panels {wall.PanelCount}"));

            foreach (string panel in wall.Panels)
            {
                report.Line($"        panel: {panel}");
            }

            if (wall.Inserts.Count == 0)
            {
                report.Line("        inserts: none");
            }

            foreach (string insert in wall.Inserts)
            {
                report.Line($"        insert: {insert}");
            }
        }
    }

    private static void WriteCircuits(DiagnosticReport report, List<CircuitInfo> circuits)
    {
        report.Section("CIRCUITS");

        foreach (CircuitInfo circuit in circuits)
        {
            double circuitM2 = UnitUtils.ConvertFromInternalUnits(circuit.CircuitAreaFeet2, UnitTypeId.SquareMeters);
            report.Blank();
            string roomed = circuit.WasRoomLocated ? "roomed" : "UNROOMED";
            report.Line(string.Create(
                CultureInfo.InvariantCulture,
                $"[{circuit.Index}]  circuit area {circuitM2:0.##} m2   sides {circuit.SideNum}   {roomed}"));

            if (circuit.Failure is not null)
            {
                report.Line($"      FAILED: {circuit.Failure}");
                continue;
            }

            double roomM2 = UnitUtils.ConvertFromInternalUnits(circuit.RoomAreaFeet2, UnitTypeId.SquareMeters);
            string source = circuit.RoomWasTemporary ? "temporary room" : "existing room";
            report.Line(string.Create(
                CultureInfo.InvariantCulture,
                $"      {source}: {circuit.RoomLabel}   area {roomM2:0.##} m2   perimeter {circuit.PerimeterFeet:0.##} ft"));
            report.Line(string.Create(
                CultureInfo.InvariantCulture,
                $"      loops {circuit.Loops.Count}   doors on boundary {circuit.DoorsOnBoundary.Count}   (hosted by bounding walls: {circuit.DoorsHostedByBoundaryWalls})"));

            for (int i = 0; i < circuit.Loops.Count; i++)
            {
                LoopInfo loop = circuit.Loops[i];
                double gapMillimetres =
                    UnitUtils.ConvertFromInternalUnits(loop.LargestGapFeet, UnitTypeId.Millimeters);
                string closure = loop.IsClosed
                    ? "closed"
                    : string.Create(
                        CultureInfo.InvariantCulture,
                        $"OPEN by {loop.LargestGapFeet:0.00000000} ft ({gapMillimetres:0.0000} mm)");
                report.Line(string.Create(
                    CultureInfo.InvariantCulture,
                    $"        loop {i}: {loop.SegmentCount} segments, {closure}"));

                var categories = loop.Segments
                    .GroupBy(s => s.CategoryName, StringComparer.Ordinal)
                    .OrderBy(g => g.Key, StringComparer.Ordinal)
                    .Select(g => string.Create(CultureInfo.InvariantCulture, $"{g.Key} x{g.Count()}"));
                report.Line($"          bounded by: {string.Join(", ", categories)}");

                var curveTypes = loop.Segments
                    .GroupBy(s => s.CurveType, StringComparer.Ordinal)
                    .OrderBy(g => g.Key, StringComparer.Ordinal)
                    .Select(g => string.Create(CultureInfo.InvariantCulture, $"{g.Key} x{g.Count()}"));
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
                    rows.Add(string.Create(
                        CultureInfo.InvariantCulture,
                        $"  circuit {circuit.Index,-4} loop {i}   {segment.CurveType,-6} length {segment.LengthFeet,8:0.###} ft ({millimetres:0.#} mm)   elementId {segment.ElementId}"));
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
