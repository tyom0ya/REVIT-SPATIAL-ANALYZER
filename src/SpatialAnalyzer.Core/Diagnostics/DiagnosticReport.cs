using System.Globalization;
using System.Text;

namespace SpatialAnalyzer.Core.Diagnostics;

/// <summary>
/// Builds a plain text diagnostic report.
///
/// Deliberately not a logging framework. What this project needs from
/// diagnostics is a readable record of what a run actually saw in the model,
/// written once per run and comparable against the previous one. A dependency
/// offering levels, sinks and structured events would answer questions nobody
/// here is asking, and would ship inside a process shared with every other
/// add-in in the session.
///
/// Output is deterministic so that two runs over an unchanged model differ
/// nowhere, which is what makes diffing them useful.
/// </summary>
public sealed class DiagnosticReport
{
    /// <summary>
    /// Line separator is fixed rather than taken from the environment. Reports
    /// are compared between runs and, eventually, between machines; a value
    /// that changes with the host would show every line as modified.
    /// </summary>
    public const string LineSeparator = "\n";

    private const int LabelWidth = 34;

    private readonly List<string> _lines = new();

    public DiagnosticReport(string title)
    {
        Title = title;
        _lines.Add(title);
        _lines.Add(new string('=', title.Length));
    }

    public string Title { get; }

    public void Section(string name)
    {
        _lines.Add(string.Empty);
        _lines.Add(name);
        _lines.Add(new string('-', name.Length));
    }

    public void Item(string label, string? value)
    {
        _lines.Add($"{label.PadRight(LabelWidth)}{value ?? string.Empty}");
    }

    /// <summary>
    /// Counts are formatted with the invariant culture. A report that renders
    /// 1,234 on one machine and 1.234 on another cannot be diffed.
    /// </summary>
    public void Item(string label, long value)
    {
        Item(label, value.ToString(CultureInfo.InvariantCulture));
    }

    public void Item(string label, double value)
    {
        Item(label, value.ToString("0.####", CultureInfo.InvariantCulture));
    }

    public void Line(string text) => _lines.Add(text);

    public void Blank() => _lines.Add(string.Empty);

    /// <summary>
    /// Writes a label and count pair for each entry, ordered by label so the
    /// report does not reshuffle when the model's internal ordering changes.
    /// </summary>
    public void Census(IEnumerable<KeyValuePair<string, long>> counts)
    {
        foreach (KeyValuePair<string, long> entry in counts.OrderBy(e => e.Key, StringComparer.Ordinal))
        {
            Item(entry.Key, entry.Value);
        }
    }

    public string ToText()
    {
        var builder = new StringBuilder();
        foreach (string line in _lines)
        {
            builder.Append(line);
            builder.Append(LineSeparator);
        }

        return builder.ToString();
    }
}
