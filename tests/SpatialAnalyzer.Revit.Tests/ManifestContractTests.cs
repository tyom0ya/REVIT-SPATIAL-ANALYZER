using System.Reflection;
using System.Xml.Linq;

namespace SpatialAnalyzer.Revit.Tests;

/// <summary>
/// Checks that every entry point declared in the .addin manifest template
/// actually exists in the built assembly in a form Revit can load.
///
/// Revit resolves the manifest by reflection at startup. A misspelled
/// namespace, a class made internal, a constructor given a parameter, or a
/// missing [Transaction] attribute all compile cleanly, deploy cleanly, and
/// then fail - or silently do nothing - when Revit next starts. Compilation
/// and manifest loading are separate failure classes, and this covers the
/// second one without needing Revit.
/// </summary>
public class ManifestContractTests
{
    private const string ExternalCommand = "Autodesk.Revit.UI.IExternalCommand";
    private const string ExternalApplication = "Autodesk.Revit.UI.IExternalApplication";

    private static string MetadataValue(string key) =>
        typeof(ManifestContractTests).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .First(a => a.Key == key)
            .Value!;

    private static string ManifestTemplatePath => Path.GetFullPath(MetadataValue("ManifestTemplatePath"));
    private static string RevitAssemblyPath => Path.GetFullPath(MetadataValue("RevitAssemblyPath"));
    private static string RevitApiDir => MetadataValue("RevitApiDir");

    private static MetadataLoadContext CreateLoadContext()
    {
        var paths = new List<string> { RevitAssemblyPath };
        paths.AddRange(Directory.GetFiles(Path.GetDirectoryName(RevitAssemblyPath)!, "*.dll"));
        paths.Add(Path.Combine(RevitApiDir, "RevitAPI.dll"));
        paths.Add(Path.Combine(RevitApiDir, "RevitAPIUI.dll"));
        paths.AddRange(Directory.GetFiles(Path.GetDirectoryName(typeof(object).Assembly.Location)!, "*.dll"));

        return new MetadataLoadContext(new PathAssemblyResolver(paths));
    }

    private static IEnumerable<(string Type, string ClassName, string Name)> ManifestEntries()
    {
        XDocument document = XDocument.Load(ManifestTemplatePath);
        foreach (XElement addIn in document.Root!.Elements("AddIn"))
        {
            yield return (
                addIn.Attribute("Type")!.Value,
                addIn.Element("FullClassName")!.Value,
                addIn.Element("Name")?.Value ?? "(unnamed)");
        }
    }

    [Fact]
    public void ManifestTemplate_DeclaresAtLeastOneEntry()
    {
        Assert.NotEmpty(ManifestEntries());
    }

    [Fact]
    public void EveryManifestEntry_ResolvesToALoadableRevitEntryPoint()
    {
        using MetadataLoadContext context = CreateLoadContext();
        Assembly assembly = context.LoadFromAssemblyPath(RevitAssemblyPath);

        foreach ((string entryType, string className, string name) in ManifestEntries())
        {
            Type? type = assembly.GetType(className);
            Assert.True(type is not null, $"Manifest entry '{name}' names {className}, which does not exist in {Path.GetFileName(RevitAssemblyPath)}.");

            // Revit instantiates the type itself, so it must be publicly
            // visible, concrete, and default-constructible.
            Assert.True(type!.IsPublic, $"{className} must be public for Revit to instantiate it.");
            Assert.False(type.IsAbstract, $"{className} must not be abstract.");
            Assert.True(
                type.GetConstructors().Any(c => c.GetParameters().Length == 0),
                $"{className} must have a public parameterless constructor.");

            string required = entryType switch
            {
                "Command" => ExternalCommand,
                "Application" => ExternalApplication,
                _ => throw new Xunit.Sdk.XunitException($"Manifest entry '{name}' has unsupported Type='{entryType}'."),
            };

            string[] interfaces = type.GetInterfaces().Select(i => i.FullName!).ToArray();
            Assert.True(
                interfaces.Contains(required),
                $"Manifest declares '{name}' as Type='{entryType}', so {className} must implement {required}. It implements: {string.Join(", ", interfaces)}");
        }
    }

    /// <summary>
    /// Revit requires a TransactionAttribute on external commands. Without it
    /// the command is rejected at invocation time rather than at load, which
    /// makes it look like the button simply does nothing.
    /// </summary>
    [Fact]
    public void EveryCommandEntry_CarriesATransactionAttribute()
    {
        using MetadataLoadContext context = CreateLoadContext();
        Assembly assembly = context.LoadFromAssemblyPath(RevitAssemblyPath);

        foreach ((string entryType, string className, _) in ManifestEntries().Where(e => e.Type == "Command"))
        {
            Type type = assembly.GetType(className)!;
            bool hasTransaction = type.GetCustomAttributesData()
                .Any(a => a.AttributeType.FullName == "Autodesk.Revit.Attributes.TransactionAttribute");

            Assert.True(hasTransaction, $"{className} is registered as Type='{entryType}' and must carry [Transaction].");
        }
    }

    /// <summary>
    /// The deployment scripts strip the template's comments before substituting
    /// the assembly path, because the comments mention the placeholder by name.
    /// This pins that ordering: the template must still be valid XML once the
    /// comments are gone.
    /// </summary>
    [Fact]
    public void ManifestTemplate_RemainsValidXmlWithoutItsComments()
    {
        string raw = File.ReadAllText(ManifestTemplatePath);
        string stripped = System.Text.RegularExpressions.Regex.Replace(raw, "(?s)<!--.*?-->", string.Empty);

        XDocument document = XDocument.Parse(stripped);

        Assert.Equal("RevitAddIns", document.Root!.Name.LocalName);
        Assert.DoesNotContain("{ASSEMBLY_PATH}", stripped.Replace("<Assembly>{ASSEMBLY_PATH}</Assembly>", string.Empty));
    }
}
