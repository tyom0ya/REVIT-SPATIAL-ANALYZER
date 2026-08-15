using System.Globalization;

namespace SpatialAnalyzer.Core.Spatial;

/// <summary>
/// Why a region was not reported as a room.
/// </summary>
public enum RejectionReason
{
    /// <summary>
    /// The boundary does not close. The space continues past the opening, so it
    /// is part of whatever is on the other side rather than a room of its own.
    /// The only way to make it one would be to close the opening.
    /// </summary>
    NotEnclosed,

    /// <summary>
    /// Nothing on the boundary admits a person. Lift shafts, duct chases, the
    /// gaps between partitions and the void behind a trash chute all arrive
    /// here.
    /// </summary>
    NoEntrance,
}

/// <summary>
/// What became of one candidate region.
///
/// A rejection carries its reason and its evidence, because a report that lists
/// only the rooms found gives no way to tell a correct rejection from a missing
/// room. Both outcomes are the answer.
/// </summary>
public sealed class QualificationOutcome
{
    private QualificationOutcome(
        CandidateRegion region,
        GranularRoom? room,
        RejectionReason? reason,
        string explanation)
    {
        Region = region;
        Room = room;
        Reason = reason;
        Explanation = explanation;
    }

    public CandidateRegion Region { get; }

    /// <summary>The room, when the region qualified; otherwise null.</summary>
    public GranularRoom? Room { get; }

    public RejectionReason? Reason { get; }

    /// <summary>
    /// A sentence naming what was found, for the diagnostic report. Written so a
    /// person reading a rejection can tell whether they agree with it.
    /// </summary>
    public string Explanation { get; }

    public bool IsQualified => Room is not null;

    internal static QualificationOutcome Qualified(GranularRoom room, string explanation) =>
        new(room.Region, room, null, explanation);

    internal static QualificationOutcome Rejected(
        CandidateRegion region,
        RejectionReason reason,
        string explanation) =>
        new(region, null, reason, explanation);

    public override string ToString() => IsQualified
        ? string.Create(CultureInfo.InvariantCulture, $"{Region.Id}: room. {Explanation}")
        : string.Create(CultureInfo.InvariantCulture, $"{Region.Id}: rejected, {Reason}. {Explanation}");
}

/// <summary>
/// Applies the project's definition of a room to a candidate region.
///
/// Two questions, in this order. Is the space enclosed, and does anything let
/// you in. The order matters for the diagnostics rather than the verdict: a
/// region that is open to the space next door is not a room whatever is on its
/// boundary, and saying so is more use to a reader than reporting that it also
/// lacks a door.
///
/// Everything this needs is passed in as plain data, so the definition of a room
/// can be exercised against contrived shapes in milliseconds rather than only
/// against whatever the acceptance model happens to contain.
/// </summary>
public sealed class RoomQualifier
{
    public RoomQualifier(EntranceRule entranceRule)
    {
        ArgumentNullException.ThrowIfNull(entranceRule);
        EntranceRule = entranceRule;
    }

    public EntranceRule EntranceRule { get; }

    /// <param name="boundaryFeatures">
    /// Everything found on this region's boundary - not everything contained by
    /// the elements that bound it. The difference is the whole difficulty: a
    /// wall lends its doors to every space it touches, and taking that at face
    /// value once credited a four square metre cupboard with eight of them.
    /// </param>
    public QualificationOutcome Qualify(
        CandidateRegion region,
        IReadOnlyList<BoundaryFeature> boundaryFeatures)
    {
        ArgumentNullException.ThrowIfNull(region);
        ArgumentNullException.ThrowIfNull(boundaryFeatures);

        // Throws if the region was measured against a tolerance wide enough to
        // swallow an opening. That is a mistake in the caller rather than a
        // property of the building, so it is not a rejection reason.
        _ = new ClosureTolerance(region.ClosureToleranceInternalFeet);

        if (!region.IsEnclosed)
        {
            return QualificationOutcome.Rejected(
                region,
                RejectionReason.NotEnclosed,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Boundary open by {region.LargestGapInternalFeet:0.######} ft at a tolerance of {region.ClosureToleranceInternalFeet:0.######} ft, across {region.OpenLoops.Count} of {region.Loops.Count} loop(s)."));
        }

        IReadOnlyList<BoundaryFeature> entrances = EntranceRule.EntrancesAmong(boundaryFeatures);

        if (entrances.Count == 0)
        {
            return QualificationOutcome.Rejected(
                region,
                RejectionReason.NoEntrance,
                DescribeWhatWasFoundInstead(boundaryFeatures));
        }

        var room = new GranularRoom(region, entrances.Select(e => e.Element).ToList());

        return QualificationOutcome.Qualified(
            room,
            string.Create(CultureInfo.InvariantCulture, $"Entered through {DescribeKinds(entrances)}."));
    }

    /// <summary>
    /// Names what was on the boundary of a rejected region.
    ///
    /// A bare "no entrance" is not auditable. Knowing that a space is ringed by
    /// six lift doors, or by nothing at all, is what lets a reader agree with
    /// the rejection or challenge it.
    /// </summary>
    private static string DescribeWhatWasFoundInstead(IReadOnlyList<BoundaryFeature> boundaryFeatures)
    {
        if (boundaryFeatures.Count == 0)
        {
            return "Nothing at all is set into this boundary.";
        }

        return string.Create(
            CultureInfo.InvariantCulture,
            $"Nothing on this boundary admits a person. Found {DescribeKinds(boundaryFeatures)}.");
    }

    private static string DescribeKinds(IReadOnlyList<BoundaryFeature> features)
    {
        var counted = features
            .GroupBy(f => f.Kind)
            .OrderBy(g => g.Key)
            .Select(g => string.Create(CultureInfo.InvariantCulture, $"{g.Count()} x {g.Key}"));

        return string.Join(", ", counted);
    }
}
