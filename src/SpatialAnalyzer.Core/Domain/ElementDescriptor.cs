namespace SpatialAnalyzer.Core.Domain;

/// <summary>
/// Identity of a single Revit element, reduced to the four facts this project
/// reports: category, family, type and id.
///
/// The exported JSON is organised as Category then Family then Type then id, so
/// every element has to yield all four. Revit does not guarantee that: an
/// element may have no category, and elements that are not family instances
/// have no family name in the usual sense. Rather than let each caller invent
/// its own handling, the normalisation lives here once.
/// </summary>
public sealed record ElementDescriptor
{
    /// <summary>
    /// Stands in wherever Revit gives us nothing. A visible placeholder is used
    /// instead of an empty string so that a gap in the model shows up in output
    /// as a gap, rather than as a blank that reads like a formatting mistake.
    /// </summary>
    public const string Unspecified = "(unspecified)";

    private ElementDescriptor(RevitElementId id, string categoryName, string familyName, string typeName)
    {
        Id = id;
        CategoryName = categoryName;
        FamilyName = familyName;
        TypeName = typeName;
    }

    public RevitElementId Id { get; }

    public string CategoryName { get; }

    public string FamilyName { get; }

    public string TypeName { get; }

    /// <summary>
    /// Builds a descriptor, substituting <see cref="Unspecified"/> for anything
    /// Revit could not supply and trimming surrounding whitespace so that names
    /// differing only by padding do not group separately in the export.
    /// </summary>
    public static ElementDescriptor Create(
        RevitElementId id,
        string? categoryName,
        string? familyName,
        string? typeName) =>
        new(id, Normalise(categoryName), Normalise(familyName), Normalise(typeName));

    private static string Normalise(string? value) =>
        string.IsNullOrWhiteSpace(value) ? Unspecified : value.Trim();

    public override string ToString() => $"{CategoryName} / {FamilyName} / {TypeName} [{Id}]";
}
