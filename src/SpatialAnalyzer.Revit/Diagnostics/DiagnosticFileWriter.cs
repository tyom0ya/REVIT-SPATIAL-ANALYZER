using System.Text;
using SpatialAnalyzer.Core.Diagnostics;

namespace SpatialAnalyzer.Revit.Diagnostics;

/// <summary>
/// Writes diagnostic reports to the user's local application data.
///
/// Deliberately outside the project directory and outside any model folder.
/// These files are a record of one run against one model; they are not source,
/// they are not deliverables, and they must never end up committed.
/// </summary>
public static class DiagnosticFileWriter
{
    public static string DirectoryPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RevitSpatialAnalyzer",
            "diagnostics");

    /// <summary>
    /// Writes the report and returns the full path written.
    /// </summary>
    public static string Write(DiagnosticReport report, string fileNameStem)
    {
        Directory.CreateDirectory(DirectoryPath);

        // Sortable, filename-safe, and unambiguous between runs a second apart.
        string timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        string path = Path.Combine(DirectoryPath, $"{timestamp}-{fileNameStem}.txt");

        // UTF-8 without a BOM, and the report's own line separator rather than
        // the platform's, so two runs can be compared byte for byte.
        File.WriteAllText(path, report.ToText(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        return path;
    }
}
