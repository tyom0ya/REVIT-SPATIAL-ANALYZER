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

    [Fact]
    public void LineEndings_DoNotFollowTheHost()
    {
        var report = new DiagnosticReport("R");
        report.Item("a", "b");

        Assert.DoesNotContain("\r", report.ToText());
    }
}
