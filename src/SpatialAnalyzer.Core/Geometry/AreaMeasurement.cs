using System.Globalization;

namespace SpatialAnalyzer.Core.Geometry;

/// <summary>
/// Which way round a closed boundary was drawn.
///
/// Reported rather than assumed. Revit conventionally returns a room's outer
/// boundary counter-clockwise and its interior voids clockwise, but relying on
/// that convention without checking it is how a void ends up counted as a room.
/// Measuring the winding lets the convention be confirmed against real models
/// instead of trusted.
/// </summary>
public enum LoopWinding
{
    /// <summary>The boundary encloses no area at all, so it has no direction.</summary>
    Degenerate,

    CounterClockwise,

    Clockwise,
}

/// <summary>
/// The answer to "how much plan area does this boundary enclose", including the
/// answer "it does not enclose any, because it is not closed".
///
/// Those two outcomes are kept apart deliberately, and an unmeasured result will
/// not hand out a number. A boundary interrupted by a doorway has an *unknown*
/// area; a boundary that doubles back on itself has an area of *zero*. Both
/// would arrive downstream as 0.0 if this type were a plain double, and the
/// first would then read as a real, if surprising, measurement of the building.
///
/// The tolerance under which the question was settled travels with the answer,
/// so a reported area is never separated from the claim about closure that
/// justifies it.
///
/// Areas are in Revit's internal unit, square feet.
/// </summary>
public readonly record struct AreaMeasurement
{
    private readonly double _internalSquareFeet;

    private AreaMeasurement(
        bool isMeasured,
        double internalSquareFeet,
        LoopWinding winding,
        double toleranceInternalFeet,
        double largestGapInternalFeet)
    {
        IsMeasured = isMeasured;
        _internalSquareFeet = internalSquareFeet;
        Winding = winding;
        ToleranceInternalFeet = toleranceInternalFeet;
        LargestGapInternalFeet = largestGapInternalFeet;
    }

    /// <summary>
    /// Whether the boundary was closed enough for an area to mean anything.
    /// </summary>
    public bool IsMeasured { get; }

    /// <summary>
    /// The enclosed area, never negative.
    ///
    /// Throws when nothing was measured. That is deliberate: there is no value
    /// this could return for an open boundary that would not be a false
    /// statement about the building, and failing loudly at the point of the
    /// mistake is better than a plausible zero travelling into an export.
    /// </summary>
    public double InternalSquareFeet => IsMeasured
        ? _internalSquareFeet
        : throw new InvalidOperationException(
            "This boundary is not closed within the stated tolerance, so it encloses no measurable area. " +
            "Check IsMeasured, or call TryGetInternalSquareFeet, before reading the area.");

    public LoopWinding Winding { get; }

    /// <summary>The closure tolerance the caller stated when asking.</summary>
    public double ToleranceInternalFeet { get; }

    /// <summary>
    /// The widest discontinuity found in the boundary, reported either way. When
    /// nothing could be measured this is the evidence for why, at its real size.
    /// </summary>
    public double LargestGapInternalFeet { get; }

    public static AreaMeasurement Measured(
        double internalSquareFeet,
        LoopWinding winding,
        double toleranceInternalFeet,
        double largestGapInternalFeet)
    {
        if (internalSquareFeet < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(internalSquareFeet),
                internalSquareFeet,
                "An enclosed area cannot be negative; pass the magnitude and report direction as winding.");
        }

        return new AreaMeasurement(true, internalSquareFeet, winding, toleranceInternalFeet, largestGapInternalFeet);
    }

    public static AreaMeasurement NotEnclosed(double toleranceInternalFeet, double largestGapInternalFeet) =>
        new(false, 0, LoopWinding.Degenerate, toleranceInternalFeet, largestGapInternalFeet);

    public bool TryGetInternalSquareFeet(out double internalSquareFeet)
    {
        internalSquareFeet = _internalSquareFeet;
        return IsMeasured;
    }

    public override string ToString() => IsMeasured
        ? string.Create(CultureInfo.InvariantCulture, $"{_internalSquareFeet:0.######} sq ft ({Winding})")
        : string.Create(CultureInfo.InvariantCulture, $"not enclosed (largest gap {LargestGapInternalFeet:0.######} ft > tolerance {ToleranceInternalFeet:0.######} ft)");
}
