using Autodesk.Revit.DB;

namespace SpatialAnalyzer.Revit;

/// <summary>
/// Makes the Revit 2025 API reference load-bearing at compile time.
///
/// This exists for a specific reason. If no code in this assembly used an
/// <c>Autodesk.Revit</c> type, the project would still build with a
/// mis-configured reference, and the "RevitAPI.dll must never be copied to
/// output" check would pass trivially — output cannot contain a dependency the
/// assembly does not have. Exposing a Revit type in a member signature forces a
/// real assembly reference into metadata, so both checks test something.
///
/// This is scaffolding for the foundation phase, not a design. It should be
/// deleted once real adapter code lands.
/// </summary>
internal static class RevitApiReference
{
    /// <summary>
    /// The boundary location this project starts its boundary-extraction work
    /// from. <see cref="SpatialElementBoundaryLocation.Finish"/> corresponds
    /// most naturally to the room boundaries the analysis needs; any move away
    /// from it is a decision that has to be justified and written down, not a
    /// default left unexamined.
    /// </summary>
    internal static SpatialElementBoundaryLocation DefaultBoundaryLocation =>
        SpatialElementBoundaryLocation.Finish;
}
