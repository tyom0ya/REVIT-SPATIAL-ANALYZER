namespace SpatialAnalyzer.Core.Domain;

/// <summary>
/// The view, level and phase an analysis was performed in, as plain data.
///
/// Every spatial result this project produces is only meaningful relative to
/// these three things, so they travel with the result rather than being implied
/// by whatever happened to be on screen. Phase in particular is part of the
/// spatial question, not an incidental detail: walls and doors come and go
/// between phases, so "which rooms exist here" has a different answer in each.
///
/// This is the Core-side copy. The live Revit objects stay in the Revit
/// project; what crosses into Core is identifiers and names.
/// </summary>
/// <param name="ViewId">Identifier of the plan view analysed.</param>
/// <param name="ViewName">Name of that view, as shown in the project browser.</param>
/// <param name="ViewType">Revit's view type, recorded so a result can never be
/// mistaken for one produced from a different kind of view.</param>
/// <param name="LevelId">Identifier of the level the view generates.</param>
/// <param name="LevelName">Name of that level.</param>
/// <param name="LevelElevationInternalFeet">
/// Level elevation in Revit's internal length unit, which is decimal feet
/// regardless of the units displayed in the user interface. Kept in internal
/// units so no precision is lost to a display conversion; convert at the point
/// of presentation.
/// </param>
/// <param name="PhaseId">Identifier of the phase the view is set to.</param>
/// <param name="PhaseName">Name of that phase.</param>
public sealed record AnalysisContextInfo(
    RevitElementId ViewId,
    string ViewName,
    string ViewType,
    RevitElementId LevelId,
    string LevelName,
    double LevelElevationInternalFeet,
    RevitElementId PhaseId,
    string PhaseName);
