using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SpatialAnalyzer.Core.Export;

/// <summary>
/// Writes the analysis out as JSON.
///
/// System.Text.Json comes with .NET itself rather than from a package, so
/// nothing extra ships inside a process shared with every other add-in in the
/// session. It also writes numbers invariantly whatever the operator's regional
/// settings are, which hand-assembled JSON has to be told to do and this project
/// has twice had to be corrected for forgetting.
///
/// The output is deterministic: two runs over an unchanged model differ only in
/// the timestamp, which is supplied by the caller rather than read from a clock
/// here. That is what makes two exports worth diffing.
/// </summary>
public static class SpatialExportWriter
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,

        // The convention consumers of JSON expect. Without it the file carries
        // the exact spelling of the C# properties, which makes an internal
        // naming choice part of a published format.
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,

        // Nulls are meaning here, not absence. A rejected region with no area
        // has none because its boundary does not close, and omitting the field
        // would look like the exporter forgot rather than like the building has
        // no answer.
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,

        // Revit type names contain quotes and inches marks - 36" x 84" - and
        // family names contain ampersands. The stricter default encoder escapes
        // those to " and &, which is valid JSON that no one can read.
        // This one escapes what HTML would need and leaves the rest legible.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string ToJson(SpatialExport export)
    {
        ArgumentNullException.ThrowIfNull(export);

        // Line endings are normalised because a report compared between machines
        // must not differ by the host's convention. The serializer emits the
        // platform's newline when indenting.
        return JsonSerializer.Serialize(export, Options).Replace("\r\n", "\n", StringComparison.Ordinal);
    }
}
