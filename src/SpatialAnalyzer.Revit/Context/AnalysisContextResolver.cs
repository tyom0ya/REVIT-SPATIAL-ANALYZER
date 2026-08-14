using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using SpatialAnalyzer.Core.Domain;

namespace SpatialAnalyzer.Revit.Context;

/// <summary>
/// Outcome of establishing the analysis context. Either a usable context, or a
/// reason the active state cannot support one.
///
/// The failure cases here are ordinary user situations - the wrong view is
/// open, no document is loaded - not defects, so they are returned rather than
/// thrown. That keeps the command free to explain the problem plainly instead
/// of surfacing an exception.
/// </summary>
public sealed class AnalysisContextResolution
{
    private AnalysisContextResolution(AnalysisContext? context, string? failureReason)
    {
        Context = context;
        FailureReason = failureReason;
    }

    public AnalysisContext? Context { get; }

    public string? FailureReason { get; }

    public bool IsSuccess => Context is not null;

    public static AnalysisContextResolution Success(AnalysisContext context) => new(context, null);

    public static AnalysisContextResolution Failure(string reason) => new(null, reason);
}

/// <summary>
/// The live Revit objects describing where an analysis is taking place. Held
/// only inside the Revit layer; Core receives <see cref="AnalysisContextInfo"/>.
/// </summary>
public sealed class AnalysisContext
{
    public AnalysisContext(Document document, ViewPlan view, Level level, Phase phase)
    {
        Document = document;
        View = view;
        Level = level;
        Phase = phase;
    }

    public Document Document { get; }

    public ViewPlan View { get; }

    public Level Level { get; }

    public Phase Phase { get; }

    /// <summary>
    /// Copies the context into plain data for Core.
    /// </summary>
    public AnalysisContextInfo ToInfo() => new(
        ViewId: new RevitElementId(View.Id.Value),
        ViewName: View.Name,
        ViewType: View.ViewType.ToString(),
        LevelId: new RevitElementId(Level.Id.Value),
        LevelName: Level.Name,
        LevelElevationInternalFeet: Level.Elevation,
        PhaseId: new RevitElementId(Phase.Id.Value),
        PhaseName: Phase.Name);
}

/// <summary>
/// Establishes the view, level and phase an analysis will run in.
/// </summary>
public static class AnalysisContextResolver
{
    public static AnalysisContextResolution Resolve(UIDocument? uiDocument)
    {
        if (uiDocument is null)
        {
            return AnalysisContextResolution.Failure("No document is open.");
        }

        Document document = uiDocument.Document;

        // A plan view is required. ViewPlan covers floor plans, ceiling plans,
        // area plans and structural plans, so the class test alone is not
        // enough - the view type has to be checked as well.
        if (document.ActiveView is not ViewPlan view)
        {
            return AnalysisContextResolution.Failure(
                $"The active view '{document.ActiveView.Name}' is a {document.ActiveView.ViewType}. " +
                "Open a floor plan and run the command again.");
        }

        if (view.ViewType != ViewType.FloorPlan)
        {
            return AnalysisContextResolution.Failure(
                $"The active view '{view.Name}' is a {view.ViewType}, not a floor plan.");
        }

        // A plan view normally generates a level, but not always: some plan
        // views are not associated with one, and GenLevel is then null.
        Level? level = view.GenLevel;
        if (level is null)
        {
            return AnalysisContextResolution.Failure(
                $"The floor plan '{view.Name}' is not associated with a level.");
        }

        Phase? phase = ResolvePhase(document, view);
        if (phase is null)
        {
            return AnalysisContextResolution.Failure(
                $"The floor plan '{view.Name}' has no phase set. Phase determines which walls and " +
                "doors exist, so the analysis cannot proceed without one.");
        }

        return AnalysisContextResolution.Success(new AnalysisContext(document, view, level, phase));
    }

    /// <summary>
    /// Reads the phase from the view itself.
    ///
    /// The view's phase is used deliberately rather than the last phase in the
    /// project. Elements are created and demolished across phases, so the set
    /// of walls and doors that bound a space differs between them; assuming the
    /// final phase would silently analyse a different building than the one the
    /// user is looking at.
    /// </summary>
    private static Phase? ResolvePhase(Document document, ViewPlan view)
    {
        Parameter? phaseParameter = view.get_Parameter(BuiltInParameter.VIEW_PHASE);
        if (phaseParameter is null)
        {
            return null;
        }

        ElementId phaseId = phaseParameter.AsElementId();
        if (phaseId == ElementId.InvalidElementId)
        {
            return null;
        }

        return document.GetElement(phaseId) as Phase;
    }
}
