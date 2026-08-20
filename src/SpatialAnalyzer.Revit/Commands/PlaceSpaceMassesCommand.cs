using System.Globalization;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using SpatialAnalyzer.Core.Spatial;
using SpatialAnalyzer.Revit.Boundaries;
using SpatialAnalyzer.Revit.Context;
using SpatialAnalyzer.Revit.Masses;

namespace SpatialAnalyzer.Revit.Commands;

/// <summary>
/// Puts a solid in every space the plan encloses, whether or not Revit would
/// call it a room.
///
/// A room has to satisfy Revit: bounded by walls it respects, somewhere to
/// stand, one level. Much of what a building contains fails one of those - the
/// shaft with no door, the space divided by walls inside a group, the riser
/// running the height of the block - and is invisible in consequence.
///
/// A mass is bound by none of that, so this reports what the geometry encloses
/// rather than what the model has been persuaded to admit. It writes to the
/// model and asks first; one undo removes the lot.
/// </summary>
[Transaction(TransactionMode.Manual)]
public class PlaceSpaceMassesCommand : IExternalCommand
{
    private const string ProtectedPathFragment = @"\models\pristine\";

    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        UIDocument? uiDocument = commandData.Application.ActiveUIDocument;

        AnalysisContextResolution resolution = AnalysisContextResolver.Resolve(uiDocument);
        if (!resolution.IsSuccess)
        {
            TaskDialog.Show("Spatial Analyzer", resolution.FailureReason!);
            return Result.Succeeded;
        }

        AnalysisContext context = resolution.Context!;

        if (context.Document.PathName.Contains(ProtectedPathFragment, StringComparison.OrdinalIgnoreCase))
        {
            TaskDialog.Show(
                "Spatial Analyzer",
                "This document is the pristine model, which development tooling must not write to."
                + Environment.NewLine + Environment.NewLine
                + "Open the working copy under models\\dev and run this there.");
            return Result.Cancelled;
        }

        PartitionSurvey.Result survey;
        try
        {
            var tolerance = new ClosureTolerance(context.Document.Application.ShortCurveTolerance);
            survey = PartitionSurvey.Of(context, tolerance.InternalFeet);
        }
        catch (Exception exception)
        {
            message = $"The plan could not be read: {exception.GetType().Name}: {exception.Message}";
            return Result.Failed;
        }

        // Every space, not only the ones without a room in them. A mass beside
        // a room is not a duplicate: it is the same space said in a way that
        // carries volume and survives the room being deleted.
        IReadOnlyList<PlanFace> spaces = survey.Subdivision.Faces;

        if (spaces.Count == 0)
        {
            TaskDialog.Show("Spatial Analyzer", "This plan encloses no space, so there is nothing to build.");
            return Result.Succeeded;
        }

        bool topMeasured = SpaceMasses.TopWasMeasured(context.Document, context.Level);

        if (!Confirm(spaces.Count, topMeasured))
        {
            return Result.Cancelled;
        }

        SpaceMasses.Result built;
        using (var transaction = new Transaction(context.Document, "Spatial Analyzer place space masses"))
        {
            transaction.Start();

            try
            {
                built = SpaceMasses.Build(context, spaces);
            }
            catch (Exception exception)
            {
                transaction.RollBack();
                message = $"The masses could not be built: {exception.GetType().Name}: {exception.Message}";
                return Result.Failed;
            }

            transaction.Commit();
        }

        uiDocument!.RefreshActiveView();
        Report(built, topMeasured);
        return Result.Succeeded;
    }

    private static bool Confirm(int spaces, bool topMeasured)
    {
        var ask = new TaskDialog("Spatial Analyzer")
        {
            MainInstruction = string.Create(CultureInfo.InvariantCulture, $"Build {spaces} space mass(es)?"),
            MainContent = "This writes to the model. It adds a solid in each space the plan encloses, "
                        + "from the floor of this level to the level above."
                        + Environment.NewLine + Environment.NewLine
                        + (topMeasured
                            ? "The height comes from the level above."
                            : "There is no level above this one, so the height is a guess of ten feet "
                              + "rather than anything the model states.")
                        + Environment.NewLine + Environment.NewLine
                        + "Running again will not build them twice: a space that already has a mass is "
                        + "left alone. One undo removes everything this adds.",
            CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.Cancel,
            DefaultButton = TaskDialogResult.Cancel,
        };

        return ask.Show() == TaskDialogResult.Yes;
    }

    private static void Report(SpaceMasses.Result built, bool topMeasured)
    {
        var lines = new List<string>
        {
            string.Create(CultureInfo.InvariantCulture, $"Spaces the plan encloses:  {built.SpacesFound}"),
            string.Create(CultureInfo.InvariantCulture, $"   masses built:  {built.MassesMade}"),
            string.Create(CultureInfo.InvariantCulture, $"   already standing:  {built.AlreadyStanding}"),
        };

        if (built.Refused > 0)
        {
            lines.Add(string.Create(CultureInfo.InvariantCulture, $"   could not be built:  {built.Refused}"));
        }

        lines.Add(string.Empty);

        // Said plainly, because a mass whose height nothing in the model
        // supports looks exactly like one measured from a level above.
        lines.Add(topMeasured
            ? "Height taken from the level above."
            : "HEIGHT IS A GUESS. Nothing above this level says how tall these spaces are.");

        lines.Add(string.Empty);
        lines.Add("Press Ctrl+Z once to remove them.");

        var done = new TaskDialog("Spatial Analyzer")
        {
            MainInstruction = built.MassesMade > 0 ? "Space masses built." : "No mass was built.",
            MainContent = string.Join(Environment.NewLine, lines),
            CommonButtons = TaskDialogCommonButtons.Close,
        };

        if (built.Failures.Count > 0)
        {
            done.ExpandedContent = string.Join(
                Environment.NewLine,
                built.Failures.Distinct(StringComparer.Ordinal).Take(20));
        }

        done.Show();
    }
}
