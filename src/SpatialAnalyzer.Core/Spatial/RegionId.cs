using System.Globalization;

namespace SpatialAnalyzer.Core.Spatial;

/// <summary>
/// Identifies a spatial region within one analysis run.
///
/// A granular region is not a Revit element and has no element id to borrow, so
/// it is numbered in the order it was discovered. That makes the identifier
/// stable and repeatable for a given model, view and phase, which is what
/// reports and exports within a run need.
///
/// It is deliberately not claimed to be stable across edits to the model.
/// Regions are derived from the walls that enclose them, so adding a partition
/// renumbers everything after it. Anything that needs to survive an edit has to
/// be anchored to the model itself, not to this.
///
/// It is a distinct type rather than an int so that a region number can never be
/// passed where an element id is expected; the two are both small integers and
/// would otherwise substitute for each other silently.
/// </summary>
public readonly record struct RegionId : IComparable<RegionId>
{
    public RegionId(int ordinal)
    {
        if (ordinal < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ordinal), ordinal, "A region ordinal cannot be negative.");
        }

        Ordinal = ordinal;
    }

    public int Ordinal { get; }

    public int CompareTo(RegionId other) => Ordinal.CompareTo(other.Ordinal);

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"R{Ordinal}");
}
