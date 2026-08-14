using System.Globalization;

namespace SpatialAnalyzer.Core.Domain;

/// <summary>
/// A Revit element identifier, carried through Core as plain data.
///
/// Revit element ids are 64-bit. Storing one in an <see cref="int"/> silently
/// truncates ids above 2,147,483,647, which is not a theoretical concern in a
/// mature model, so this type exists to make the width impossible to get wrong
/// by accident and to keep raw <see cref="long"/> values from being mistaken
/// for counts, indices or areas.
///
/// It is also ordered, because the JSON this project exports has to be stable
/// enough to diff between runs.
/// </summary>
public readonly record struct RevitElementId(long Value) : IComparable<RevitElementId>
{
    /// <summary>
    /// Matches Revit's own invalid element id. Absence is represented with this
    /// rather than with a nullable, so that "no element" survives serialisation
    /// and comparison the same way Revit represents it.
    /// </summary>
    public static readonly RevitElementId Invalid = new(-1);

    public bool IsValid => Value != Invalid.Value;

    public int CompareTo(RevitElementId other) => Value.CompareTo(other.Value);

    public static bool operator <(RevitElementId left, RevitElementId right) => left.CompareTo(right) < 0;
    public static bool operator >(RevitElementId left, RevitElementId right) => left.CompareTo(right) > 0;
    public static bool operator <=(RevitElementId left, RevitElementId right) => left.CompareTo(right) <= 0;
    public static bool operator >=(RevitElementId left, RevitElementId right) => left.CompareTo(right) >= 0;

    /// <summary>
    /// Always formats with the invariant culture. Output that changes with the
    /// operator's regional settings is not reproducible, and this value ends up
    /// in exported JSON.
    /// </summary>
    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);

    public static bool TryParse(string? text, out RevitElementId id)
    {
        if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long value))
        {
            id = new RevitElementId(value);
            return true;
        }

        id = Invalid;
        return false;
    }
}
