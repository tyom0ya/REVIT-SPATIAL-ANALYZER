using System.Globalization;
using SpatialAnalyzer.Core.Diagnostics;

namespace SpatialAnalyzer.Core.Tests.Diagnostics;

public class DiagnosticReportTests
{
    [Fact]
    public void Title_IsUnderlined()
    {
        var report = new DiagnosticReport("AUDIT");

        string[] lines = report.ToText().Split(DiagnosticReport.LineSeparator);

        Assert.Equal("AUDIT", lines[0]);
        Assert.Equal("=====", lines[1]);
    }

    [Fact]
    public void ItemsAppearInTheOrderTheyWereAdded()
    {
        var report = new DiagnosticReport("R");
        report.Item("second", "b");
        report.Item("first", "a");

        string text = report.ToText();

        Assert.True(text.IndexOf("second", StringComparison.Ordinal) < text.IndexOf("first", StringComparison.Ordinal));
    }

    /// <summary>
    /// A census is sorted so that two runs over an unchanged model produce
    /// byte-identical reports. Revit's own collection order is not guaranteed
    /// stable, and an unsorted census would show spurious differences.
    /// </summary>
    [Fact]
    public void Census_IsSortedByLabel()
    {
        var report = new DiagnosticReport("R");
        report.Census(new Dictionary<string, long>
        {
            ["Walls"] = 3,
            ["Doors"] = 1,
            ["Furniture"] = 2,
        });

        string text = report.ToText();
        int doors = text.IndexOf("Doors", StringComparison.Ordinal);
        int furniture = text.IndexOf("Furniture", StringComparison.Ordinal);
        int walls = text.IndexOf("Walls", StringComparison.Ordinal);

        Assert.True(doors < furniture);
        Assert.True(furniture < walls);
    }

    [Fact]
    public void Census_ProducesIdenticalTextRegardlessOfInputOrder()
    {
        var first = new DiagnosticReport("R");
        first.Census(new[]
        {
            new KeyValuePair<string, long>("Walls", 3),
            new KeyValuePair<string, long>("Doors", 1),
        });

        var second = new DiagnosticReport("R");
        second.Census(new[]
        {
            new KeyValuePair<string, long>("Doors", 1),
            new KeyValuePair<string, long>("Walls", 3),
        });

        Assert.Equal(first.ToText(), second.ToText());
    }

    /// <summary>
    /// Reports are compared between runs and between machines, so neither the
    /// operator's regional settings nor the host's line ending convention may
    /// influence the bytes produced.
    /// </summary>
    [Theory]
    [InlineData("de-DE")]
    [InlineData("sv-SE")]
    public void Output_IsCultureInvariant(string culture)
    {
        CultureInfo original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo(culture);

            var report = new DiagnosticReport("R");
            report.Item("count", 1234567L);
            report.Item("area", 12.5);
            report.Item("negative", -1L);

            string text = report.ToText();

            Assert.Contains("1234567", text);
            Assert.Contains("12.5", text);
            Assert.Contains("-1", text);
            Assert.DoesNotContain("1.234.567", text);
            Assert.DoesNotContain("12,5", text);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    /// <summary>
    /// A label as wide as the alignment column used to run straight into its
    /// value, so "Enclosed at Revit's ShortCurveTolerance" followed by 30 read
    /// as "...Tolerance30". Padding silently does nothing once a label is
    /// already wide enough, which is exactly when a separator matters most.
    /// </summary>
    [Fact]
    public void ALabelTooWideToPadIsStillSeparatedFromItsValue()
    {
        var report = new DiagnosticReport("R");
        string wide = new('x', 40);
        report.Item(wide, 30L);

        string[] lines = report.ToText().Split(DiagnosticReport.LineSeparator);

        Assert.Contains(lines, l => l == wide + " 30");
        Assert.DoesNotContain(lines, l => l == wide + "30");
    }

    [Fact]
    public void ALabelExactlyAtTheColumnIsAlsoSeparated()
    {
        var report = new DiagnosticReport("R");
        string atWidth = new('x', 34);
        report.Item(atWidth, 7L);

        Assert.Contains(atWidth + " 7", report.ToText());
    }

    [Fact]
    public void ShorterLabelsStillLineUpInAColumn()
    {
        var report = new DiagnosticReport("R");
        report.Item("a", 1L);
        report.Item("bb", 2L);

        string[] lines = report.ToText().Split(DiagnosticReport.LineSeparator);
        int first = Array.FindIndex(lines, l => l.EndsWith("1", StringComparison.Ordinal));
        int second = Array.FindIndex(lines, l => l.EndsWith("2", StringComparison.Ordinal));

        Assert.Equal(lines[first].IndexOf('1'), lines[second].IndexOf('2'));
    }

    [Fact]
    public void LineEndings_DoNotFollowTheHost()
    {
        var report = new DiagnosticReport("R");
        report.Item("a", "b");

        Assert.DoesNotContain("\r", report.ToText());
    }
}
