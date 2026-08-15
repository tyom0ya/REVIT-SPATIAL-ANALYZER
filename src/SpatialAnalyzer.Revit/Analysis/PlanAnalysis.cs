using SpatialAnalyzer.Core.Spatial;
using SpatialAnalyzer.Revit.Context;
using SpatialAnalyzer.Revit.Diagnostics;

namespace SpatialAnalyzer.Revit.Analysis;

/// <summary>
/// Turns a reading of the model into the finished analysis.
///
/// The same assembly whichever command asked for it, so that what one command
/// shows and another reports cannot differ. Confirmations a person gave when
/// qualifying regions are carried through here, which is how they survive into
/// later questions about the same plan.
/// </summary>
public static class PlanAnalysis
{
    public sealed record Result(
        SpatialIndex Index,
        RegionQualification.Reading Reading,
        IReadOnlyList<(RegionQualification.RegionReading Reading, QualificationOutcome Outcome)> Outcomes);

    /// <param name="confirmedEntrances">
    /// Elements a person said are ways in, having been shown what is on the
    /// boundary. Empty where nobody has been asked.
    /// </param>
    public static Result From(
        AnalysisContext context,
        RegionQualification.Reading reading,
        IReadOnlySet<Core.Domain.RevitElementId>? confirmedEntrances = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(reading);

        var qualifier = new RoomQualifier(EntranceRule.Default);

        var outcomes = reading.Regions
            .Select(r => (
                Reading: r,
                Outcome: qualifier.Qualify(
                    r.Region,
                    r.Features.Select(f => f.Feature).ToList(),
                    confirmedEntrances)))
            .ToList();

        var adjacency = DoorAdjacencyIndex.Build(
            reading.Regions.ToDictionary(
                r => r.Region.Id,
                r => (IReadOnlyList<BoundaryFeature>)r.Features.Select(f => f.Feature).ToList()),
            EntranceRule.Default);

        SpatialIndex index = SpatialIndex.Build(
            context.ToInfo(),
            outcomes.Where(o => o.Outcome.IsQualified).Select(o => o.Outcome.Room!).ToList(),
            adjacency,
            reading.ClosureToleranceFeet);

        return new Result(index, reading, outcomes);
    }
}
