using System.Globalization;

namespace SpatialAnalyzer.Core.Spatial;

/// <summary>
/// How far apart two boundary endpoints may be while still describing one
/// physical location.
///
/// The geometry types take a plain number, because measuring is not judging: a
/// caller may legitimately ask a loop whether it closes within three feet, and
/// get an honest answer. This type is the judgement. It exists so the value that
/// decides whether a space is a room cannot be an arbitrary number, and so that
/// decision is written down once rather than repeated at every call site.
///
/// Why an upper bound is safe to state, when the project refuses to state a
/// default: the two questions are different. A default would be this type
/// choosing a tolerance nobody looked at. A bound rules out values that are not
/// tolerances at all. One eighth of an inch is finer than anything drafted in
/// imperial practice, and some three hundred times smaller than the narrowest
/// doorway a building contains. A number above it has stopped being a claim
/// about how a location is recorded and become a claim about the building.
///
/// It is worth being clear about what this tolerance can and cannot do, because
/// the rule it serves is the one this project cannot compromise on. Regions are
/// discovered by Revit's plan topology, not by this number. The tolerance is
/// applied afterwards, to the question of whether an already-discovered region's
/// boundary closes. It joins no geometry, merges no regions and changes which
/// spaces exist not at all. It therefore cannot bridge a physical gap - the most
/// it can do is call one region enclosed when its boundary was reported
/// discontinuously, which is why it is still kept small and why the measured gap
/// is always reported alongside the verdict.
///
/// Values are in Revit's internal unit, decimal feet.
/// </summary>
public readonly record struct ClosureTolerance : IComparable<ClosureTolerance>
{
    /// <summary>
    /// One eighth of an inch, which is exactly one ninety-sixth of a foot.
    ///
    /// Written as the fraction rather than as 0.0104167 so that it is exact and
    /// so that what it means is legible: this is a length in the units Revit
    /// actually works in, not a rounded decimal whose origin has been lost.
    /// </summary>
    public const double MaximumInternalFeet = 1.0 / 96.0;

    public ClosureTolerance(double internalFeet)
    {
        if (double.IsNaN(internalFeet))
        {
            throw new ArgumentException("A closure tolerance must be a number.", nameof(internalFeet));
        }

        if (internalFeet < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(internalFeet),
                internalFeet,
                "A closure tolerance cannot be negative.");
        }

        if (internalFeet > MaximumInternalFeet)
        {
            throw new ArgumentOutOfRangeException(
                nameof(internalFeet),
                internalFeet,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"A closure tolerance of {internalFeet:0.######} ft is larger than one eighth of an inch ({MaximumInternalFeet:0.######} ft). A value that big is no longer a statement about how one location is recorded twice, and treating a gap of that size as closed would describe a building that does not exist."));
        }

        InternalFeet = internalFeet;
    }

    public double InternalFeet { get; }

    /// <summary>
    /// Requires two endpoints to coincide exactly.
    ///
    /// Offered because it is occasionally the right answer and always an honest
    /// one, not as a default. Applied to a real model it rejects boundaries that
    /// Revit itself considers closed.
    /// </summary>
    public static ClosureTolerance Exact => new(0);

    public int CompareTo(ClosureTolerance other) => InternalFeet.CompareTo(other.InternalFeet);

    public static bool operator <(ClosureTolerance left, ClosureTolerance right) => left.CompareTo(right) < 0;

    public static bool operator >(ClosureTolerance left, ClosureTolerance right) => left.CompareTo(right) > 0;

    public static bool operator <=(ClosureTolerance left, ClosureTolerance right) => left.CompareTo(right) <= 0;

    public static bool operator >=(ClosureTolerance left, ClosureTolerance right) => left.CompareTo(right) >= 0;

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{InternalFeet:0.########} ft");
}
