using ReportService.Testing;

namespace ReportService.TemplateSource.Tests.Architecture;

/// <summary>
/// The manifest format's isolation rule, held as a test rather than as a habit:
/// one product project carries the YAML library, one product type uses it, it
/// reaches only the parser's own namespaces, and no other product code names the
/// manifest's file — every later reader of a manifest reads the JSON the take
/// stored.
/// </summary>
public sealed class YamlIsolationTests
{
    private const string Reader = "TemplateManifestReader.cs";

    private const string SourceProject = "ReportService.TemplateSource.csproj";

    [Fact]
    public void OneProductProjectReferencesTheYamlLibrary()
    {
        var projects = ProductProjects()
            .Where(file => File.ReadAllText(file).Contains("YamlDotNet", StringComparison.Ordinal))
            .Select(file => Path.GetFileName(file))
            .ToList();

        Assert.Equal([SourceProject], projects);
    }

    [Fact]
    public void OneProductTypeUsesTheYamlLibrary()
    {
        var users = ProductSources()
            .Where(file => File.ReadAllText(file).Contains("YamlDotNet", StringComparison.Ordinal))
            .Select(file => Path.GetFileName(file))
            .ToList();

        Assert.Equal([Reader], users);
    }

    [Fact]
    public void TheReaderReachesOnlyTheParserAndItsEvents()
    {
        var namespaces = File.ReadAllLines(ProductSources().Single(file => Path.GetFileName(file) == Reader))
            .Where(line => line.StartsWith("using YamlDotNet", StringComparison.Ordinal))
            .Select(line => line["using ".Length..].TrimEnd(';'))
            .ToList();

        Assert.NotEmpty(namespaces);
        Assert.All(
            namespaces,
            name => Assert.True(
                name is "YamlDotNet.Core" or "YamlDotNet.Core.Events",
                $"the reader uses '{name}', outside the parser and its events"));
    }

    [Fact]
    public void NoOtherProductCodeNamesTheManifestFile()
    {
        var namers = ProductSources()
            .Where(file => File.ReadAllText(file).Contains("template.yaml", StringComparison.Ordinal))
            .Select(file => Path.GetFileName(file))
            .ToList();

        // The package path rule names it once, and the reader and the take name it
        // in the reason of a refusal; nothing else opens it.
        Assert.Equal(
            ["ManifestReadResult.cs", "PackagePath.cs", Reader, "TemplateTake.cs"],
            [.. namers.Order(StringComparer.Ordinal)]);
    }

    [Fact]
    public void TheApiAndTheRenderPathDoNotCarryTheSourceOrItsLibrary()
    {
        var forbidden = new[] { "ReportService.Rendering", "ReportService.Templates", "ReportService.Api", "ReportService.Data", "ReportService.Uploads" };

        foreach (var project in forbidden)
        {
            var directory = Path.Combine(RepositoryPaths.Root, "src", project);
            if (!Directory.Exists(directory))
            {
                continue;
            }

            var csproj = Directory.GetFiles(directory, "*.csproj", SearchOption.TopDirectoryOnly).Single();
            var references = File.ReadAllText(csproj);
            Assert.DoesNotContain("ReportService.TemplateSource", references, StringComparison.Ordinal);
            Assert.DoesNotContain("YamlDotNet", references, StringComparison.Ordinal);
        }
    }

    private static IEnumerable<string> ProductProjects() =>
        Directory.GetFiles(Path.Combine(RepositoryPaths.Root, "src"), "*.csproj", SearchOption.AllDirectories)
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    private static IEnumerable<string> ProductSources() =>
        Directory.GetFiles(Path.Combine(RepositoryPaths.Root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
}
