using System.Globalization;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using SpatialAnalyzer.Core.Spatial;
using SpatialAnalyzer.Revit.Boundaries;
using SpatialAnalyzer.Revit.Masses;

namespace SpatialAnalyzer.Revit.Commands;

/// <summary>
/// Builds a solid in every space the faces of the model enclose.
///
/// The companion command works from wall centre lines on one level. This works
/// from the flat faces of the walls themselves, on every storey the view
/// reaches, and the two differences are the point of it.
///
/// A centre line runs down the middle of a wall, so a room bounded by centre
/// lines is half a wall too big on every side, and a wall between two rooms has
/// one line to offer both. A face is the surface somebody paints, and a room
/// bounded by faces has the area it actually has.
///
/// And the height is cut before the plan is worked out, at the floor slabs the
/// model contains rather than at the level datums somebody named. A space runs
/// from the top of the slab it stands on to the underside of the slab above,
/// which is where a space in fact stops.
///
/// Writes to the model and asks first. One undo removes the lot.
/// </summary>
[Transaction(TransactionMode.Manual)]
public class PlaceFaceMassesCommand : IExternalCommand
{
    private const string ProtectedPathFragment = @"\models\pristine\";
    private const double SquareFeetToSquareMetres = 0.09290304;
    private const double FeetToMetres = 0.3048;

    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        UIDocument? uiDocument = commandData.Application.ActiveUIDocument;

        if (uiDocument?.Document is not Document document)
        {
            TaskDialog.Show("Spatial Analyzer", "There is no open document to read.");
            return Result.Succeeded;
        }

        // Any view that shows the model will do, and the choice of view is how
        // the operator says how much of the building to answer for. This does
        // not go through the usual context resolver, which insists on a plan
        // and a level: the whole reason for cutting the height into storeys is
        // so that no level has to be trusted to say which walls are whose, and
        // a three dimensional view is what makes every floor available at once.
        View view = document.ActiveView;

        if (view is not View3D && view is not ViewPlan)
        {
            TaskDialog.Show(
                "Spatial Analyzer",
                "This reads the model through a view, and the current view does not show one."
                + Environment.NewLine + Environment.NewLine
                + "Open a 3D view to build masses for every floor at once, or a plan to build "
                + "them for the storey that plan shows.");
            return Result.Succeeded;
        }

        if (document.PathName.Contains(ProtectedPathFragment, StringComparison.OrdinalIgnoreCase))
        {
            TaskDialog.Show(
                "Spatial Analyzer",
                "This document is the pristine model, which development tooling must not write to."
                + Environment.NewLine + Environment.NewLine
                + @"Open the working copy under models\dev and run this there.");
            return Result.Cancelled;
        }

        FaceSurvey survey;
        try
        {
            double tolerance = new ClosureTolerance(
                document.Application.ShortCurveTolerance).InternalFeet;

            survey = FaceMasses.Survey(PlanarFaceReader.Of(document, view, tolerance), tolerance);
        }
        catch (Exception exception)
        {
            message = $"The faces could not be read: {exception.GetType().Name}: {exception.Message}";
            return Result.Failed;
        }

        if (survey.Spaces.Count == 0)
        {
            TaskDialog.Show("Spatial Analyzer", Scope(view) + Environment.NewLine
                + Environment.NewLine + NothingFound(survey));
            return Result.Succeeded;
        }

        if (!Confirm(survey, view))
        {
            return Result.Cancelled;
        }

        FaceMasses.Built built;
        using (var transaction = new Transaction(document, "Spatial Analyzer place face masses"))
        {
            transaction.Start();

            try
            {
                built = FaceMasses.Build(document, survey.Spaces);
            }
            catch (Exception exception)
            {
                transaction.RollBack();
                message = $"The masses could not be built: {exception.GetType().Name}: {exception.Message}";
                return Result.Failed;
            }

            transaction.Commit();
        }

        uiDocument.RefreshActiveView();
        Report(survey, built, view);
        return Result.Succeeded;
    }

    /// <summary>
    /// Says why nothing was found, rather than only that nothing was.
    ///
    /// Each of these has a different cause and a different thing to do about
    /// it, and a bare "no spaces" sends somebody looking in the wrong place.
    /// </summary>
    private static string NothingFound(FaceSurvey survey)
    {
        var why = new List<string>();

        if (survey.ElementsRead == 0)
        {
            why.Add("This view shows no walls or curtain panels to read.");
        }
        else if (survey.UprightFacesRead == 0)
        {
            why.Add(Count(survey.ElementsRead)
                + " element(s) were read and none of them had an upright flat face.");

            if (survey.CurvedFacesPassedOver > 0)
            {
                why.Add(Count(survey.CurvedFacesPassedOver)
                    + " face(s) were curved. A curved face has no one direction and draws no single "
                    + "line on the plan, so it is passed over.");
            }
        }
        else if (survey.Bands.Count == 0)
        {
            why.Add("The walls read are not tall enough to make a storey anyone stands in.");
        }
        else if (survey.LoopsFound == 0)
        {
            why.Add("No run of faces closes. Faces that stop short of each other are left as they "
                  + "were found; nothing here joins what the model has apart.");
        }
        else
        {
            why.Add(Count(survey.LoopsFound) + " closed loop(s) were found and none was a space.");

            if (survey.LoopsThatWereMaterial > 0)
            {
                why.Add("   " + Count(survey.LoopsThatWereMaterial)
                    + " had bounding faces looking out of them rather than in, which is a wall "
                    + "enclosing its own thickness rather than a space in the building.");
            }

            if (survey.LoopsTooSmall > 0)
            {
                why.Add("   " + Count(survey.LoopsTooSmall) + " enclosed less than "
                    + Area(FaceMasses.SmallestSpaceSquareFeet) + " m2.");
            }
        }

        return string.Join(Environment.NewLine + Environment.NewLine, why);
    }

    private static bool Confirm(FaceSurvey survey, View view)
    {
        int corroborated = survey.Spaces.Count(s => s.OpposingPairs >= OppositeFaces.EnoughToCorroborate);

        var lines = new List<string>
        {
            Scope(view),
            string.Empty,
            "Read " + Count(survey.UprightFacesRead) + " upright flat face(s) from "
                + Count(survey.ElementsRead) + " element(s), across "
                + Count(survey.Bands.Count) + " storey(s).",
            string.Empty,
            "Each space runs from the top of the slab it stands on to the underside of the slab "
                + "above, so it is the height of the space rather than the height of the structure.",
            string.Empty,
            Count(corroborated) + " of " + Count(survey.Spaces.Count) + " have bounding faces that "
                + "look at each other in two or more pairs, which is a space behaving like a room. "
                + "The rest are built too and say so in their comments.",
            string.Empty,
            "Running again will not build them twice. One undo removes everything this adds.",
        };

        var ask = new TaskDialog("Spatial Analyzer")
        {
            MainInstruction = "Build " + Count(survey.Spaces.Count)
                + " mass(es) from the faces of the model?",
            MainContent = string.Join(Environment.NewLine, lines),
            CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.Cancel,
            DefaultButton = TaskDialogResult.Cancel,
        };

        return ask.Show() == TaskDialogResult.Yes;
    }

    private static void Report(FaceSurvey survey, FaceMasses.Built built, View view)
    {
        var lines = new List<string>
        {
            Scope(view),
            string.Empty,
            "Elements read:  " + Count(survey.ElementsRead),
            "   upright flat faces:  " + Count(survey.UprightFacesRead),
        };

        if (survey.CurvedFacesPassedOver > 0)
        {
            lines.Add("   curved faces passed over:  " + Count(survey.CurvedFacesPassedOver));
        }

        lines.Add(string.Empty);
        lines.Add("Storeys:  " + Count(survey.Bands.Count));

        foreach (ZBand band in survey.Bands)
        {
            lines.Add("   " + Metres(band.BottomInternalFeet)
                + " m to " + Metres(band.TopInternalFeet) + " m"
                + "   (" + Count(survey.Spaces.Count(s => s.Band == band)) + " space(s))");
        }

        lines.Add(string.Empty);
        lines.Add("Closed loops found:  " + Count(survey.LoopsFound));
        lines.Add("   walls themselves, their faces looking outward:  "
            + Count(survey.LoopsThatWereMaterial));
        lines.Add("   too small to be a space:  " + Count(survey.LoopsTooSmall));
        lines.Add("   spaces:  " + Count(survey.Spaces.Count));

        if (survey.RoomsPlaced > 0)
        {
            lines.Add(string.Empty);
            lines.Add("Rooms Revit already has:  " + Count(survey.RoomsPlaced));
            lines.Add("   standing inside a space found here:  " + Count(survey.RoomsInsideASpace));
            lines.Add("   with no space over them:  "
                + Count(survey.RoomsPlaced - survey.RoomsInsideASpace));
        }

        lines.Add(string.Empty);
        lines.Add("Masses built:  " + Count(built.MassesMade));
        lines.Add("   already standing:  " + Count(built.AlreadyStanding));

        if (built.RefusedAsTooThinToDraw > 0)
        {
            lines.Add("   too thin for Revit to draw:  " + Count(built.RefusedAsTooThinToDraw));
        }

        if (built.RefusedByRevit > 0)
        {
            lines.Add("   refused by Revit (see details):  " + Count(built.RefusedByRevit));
        }

        lines.Add(string.Empty);
        lines.Add(Count(built.Corroborated) + " of the " + Count(built.MassesMade)
            + " built have bounding faces that look at each other in two or more pairs. The rest "
            + "are marked SHAPE NOT CORROBORATED in their comments: they are still closed loops, "
            + "but nothing beyond the loop itself says so.");

        lines.Add(string.Empty);
        lines.Add("Gaps between faces were measured and left alone. Nothing here joins what the "
            + "model has apart, so a space that stays open stays open.");

        lines.Add(string.Empty);
        lines.Add("Press Ctrl+Z once to remove them.");

        var done = new TaskDialog("Spatial Analyzer")
        {
            MainInstruction = built.MassesMade > 0 ? "Face masses built." : "No mass was built.",
            MainContent = string.Join(Environment.NewLine, lines),
            CommonButtons = TaskDialogCommonButtons.Close,
        };

        if (survey.Failures.Count > 0 || built.Failures.Count > 0)
        {
            done.ExpandedContent = string.Join(
                Environment.NewLine,
                survey.Failures.Concat(built.Failures).Distinct(StringComparer.Ordinal).Take(20));
        }

        done.Show();
    }

    /// <summary>
    /// What the run was asked about, said back before any number is quoted.
    ///
    /// A count of storeys means one thing when the whole model was read and
    /// quite another when a single plan was, and the two are impossible to tell
    /// apart from the numbers alone.
    /// </summary>
    private static string Scope(View view) => view switch
    {
        View3D { IsSectionBoxActive: true } =>
            "Read through a 3D view with its section box on, so this covers the part of the "
            + "model inside that box and no more.",
        View3D => "Read through a 3D view, so this covers every floor of the model at once.",
        ViewPlan plan =>
            "Read through the plan of " + plan.GenLevel?.Name + ", so this covers the storey that "
            + "plan shows. Open a 3D view to do the whole building at once.",
        _ => "Read through " + view.Name + ".",
    };

    private static string Count(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Metres(double internalFeet) =>
        (internalFeet * FeetToMetres).ToString("0.00", CultureInfo.InvariantCulture);

    private static string Area(double internalSquareFeet) =>
        (internalSquareFeet * SquareFeetToSquareMetres).ToString("0.0", CultureInfo.InvariantCulture);
}
