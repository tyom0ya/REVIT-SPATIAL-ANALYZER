using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using SpatialAnalyzer.Revit.Context;

namespace SpatialAnalyzer.Revit.Analysis;

/// <summary>
/// Holds the finished analysis between commands, and throws it away the moment
/// the model moves.
///
/// Reading every region out of Revit takes long enough to be worth not
/// repeating, but a kept answer is a dangerous thing: a wall moved after the
/// analysis was built would leave every later question answered confidently and
/// wrongly, with nothing in the reply to hint at it. So this holds an analysis
/// only for as long as it can show that nothing has changed.
///
/// Revit reports every committed change, and any change at all discards what is
/// held. That bluntness is deliberate. Working out whether a particular edit
/// could have affected the rooms is exactly the sort of cleverness that is wrong
/// once, silently, and rebuilding costs seconds.
///
/// The whole analysis is kept rather than only the rooms, because the regions
/// that were rejected and the reasons they were rejected are half of what makes
/// an answer useful, and rebuilding just to explain a rejection would defeat the
/// purpose.
///
/// One analysis is kept, for one view and phase of one document. Commands run on
/// Revit's own thread, one at a time, so nothing here guards against being used
/// from two places at once.
/// </summary>
public static class PlanAnalysisCache
{
    private static PlanAnalysis.Result? _analysis;
    private static Document? _document;
    private static string _documentKey = string.Empty;
    private static long _viewId;
    private static long _phaseId;

    /// <summary>
    /// Names a document in a way that survives being asked twice.
    ///
    /// Revit hands out a fresh managed wrapper around the same document each
    /// time one is asked for, so comparing references reports every document as
    /// a different one - which is what an earlier version of this did, and why
    /// it rebuilt the analysis ten times without ever reusing it. Revit
    /// overrides Equals on Document for exactly this reason, but rather than
    /// rest on semantics that cannot be checked here, two facts are compared
    /// that between them settle it.
    ///
    /// Neither would do alone. A copied file keeps the GUID it was created with,
    /// so the pristine and working copies of the same model share one; and a
    /// document that has never been saved has no path, so several would share
    /// that. Together they identify a document.
    /// </summary>
    private static string KeyOf(Document document) =>
        FormattableString.Invariant($"{document.CreationGUID:D}|{document.PathName}");

    /// <summary>How often an analysis has been built, and how often one was reused.</summary>
    public static int Builds { get; private set; }

    public static int Reuses { get; private set; }

    /// <summary>Why the last request could not be answered from what was held.</summary>
    public static string LastMissReason { get; private set; } = "nothing held yet";

    public static void Attach(ControlledApplication application)
    {
        ArgumentNullException.ThrowIfNull(application);

        application.DocumentChanged += OnDocumentChanged;
        application.DocumentClosed += OnDocumentClosed;
    }

    public static void Detach(ControlledApplication application)
    {
        ArgumentNullException.ThrowIfNull(application);

        application.DocumentChanged -= OnDocumentChanged;
        application.DocumentClosed -= OnDocumentClosed;
        Discard("the add-in is shutting down");
    }

    /// <summary>
    /// The analysis for this view and phase, if one is held and still known to
    /// describe the model as it stands.
    /// </summary>
    public static PlanAnalysis.Result? TryGet(AnalysisContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (_analysis is null)
        {
            LastMissReason = "nothing held";
            return null;
        }

        // A closed document leaves its objects unusable, and touching one throws
        // rather than returning anything.
        if (_document is null || !_document.IsValidObject)
        {
            Discard("the document it was built from is gone");
            return null;
        }

        string key = KeyOf(context.Document);
        if (!string.Equals(_documentKey, key, StringComparison.Ordinal))
        {
            LastMissReason = "held for a different document";
            return null;
        }

        if (_viewId != context.View.Id.Value || _phaseId != context.Phase.Id.Value)
        {
            LastMissReason = "held for a different view or phase";
            return null;
        }

        Reuses++;
        return _analysis;
    }

    public static void Store(AnalysisContext context, PlanAnalysis.Result analysis)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(analysis);

        _analysis = analysis;
        _document = context.Document;
        _documentKey = KeyOf(context.Document);
        _viewId = context.View.Id.Value;
        _phaseId = context.Phase.Id.Value;
        Builds++;
    }

    private static void Discard(string reason)
    {
        _analysis = null;
        _document = null;
        _documentKey = string.Empty;
        LastMissReason = reason;
    }

    /// <summary>
    /// Any committed change discards what is held.
    ///
    /// Reading the regions rolls its transaction back, so if Revit reports a
    /// rollback as a change the analysis will be discarded immediately after
    /// being built. That would be wasteful and never wrong, and the build and
    /// reuse counts are reported so it can be seen rather than assumed.
    /// </summary>
    private static void OnDocumentChanged(object? sender, DocumentChangedEventArgs e)
    {
        if (_analysis is null)
        {
            return;
        }

        // Compared by the same key as everywhere else. The document handed to an
        // event is a different wrapper again, so a reference test here would
        // never match and changes would go unnoticed - the failure that matters,
        // rather than the merely wasteful one.
        Document changed = e.GetDocument();
        if (string.Equals(_documentKey, KeyOf(changed), StringComparison.Ordinal))
        {
            Discard("the model changed");
        }
    }

    private static void OnDocumentClosed(object? sender, DocumentClosedEventArgs e)
    {
        // The document is gone by the time this fires, so there is nothing left
        // to compare against and what is held is dropped regardless.
        Discard("a document was closed");
    }
}
