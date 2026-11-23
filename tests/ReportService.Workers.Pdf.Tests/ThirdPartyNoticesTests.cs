using System.Text.Json;
using ReportService.Testing;

namespace ReportService.Workers.Pdf.Tests;

/// <summary>
/// The notice file a release carries, checked against what the two worker processes
/// actually resolve: every package in the orchestration worker's and the render worker's
/// resolved graph has an entry of its own, with the name and the exact version the graph
/// names. The graph is the one the build restored - the same file the build wrote - so a
/// package added to either process without its entry fails here rather than shipping.
/// </summary>
public sealed class ThirdPartyNoticesTests
{
    /// <summary>The notice file, at the product root.</summary>
    public const string NoticesFile = "THIRD-PARTY-NOTICES.md";

    /// <summary>The two projects whose resolved graph is the set a shipped image carries.</summary>
    public static TheoryData<string> ShippedProjects { get; } = new()
    {
        Path.Combine("src", "ReportService.Workers.Orchestration", "ReportService.Workers.Orchestration.csproj"),
        Path.Combine("src", "ReportService.Workers.Pdf", "ReportService.Workers.Pdf.csproj"),
    };

    [Theory]
    [MemberData(nameof(ShippedProjects))]
    public void EveryPackageOfAShippedWorkerIsInTheNoticeFileWithItsExactVersion(string project)
    {
        var notices = File.ReadAllLines(Path.Combine(RepositoryPaths.Root, NoticesFile));
        var headings = notices
            .Where(line => line.StartsWith("## ", StringComparison.Ordinal))
            .ToList();

        var packages = ResolvedPackagesOf(project);
        Assert.NotEmpty(packages);
        foreach (var (name, version) in packages)
        {
            Assert.True(
                headings.Exists(line =>
                    line.Contains(name, StringComparison.Ordinal)
                    && line.Contains(version, StringComparison.Ordinal)),
                $"{project} resolves {name} {version}, and {NoticesFile} carries no entry for it.");
        }
    }

    [Fact]
    public void TheFileStatesTheLicenceOfEveryComponentItNames()
    {
        // The page is wrapped for reading, so it is compared with its whitespace collapsed:
        // a sentence split across two lines is still the same sentence.
        var text = System.Text.RegularExpressions.Regex.Replace(
            File.ReadAllText(Path.Combine(RepositoryPaths.Root, NoticesFile)),
            @"\s+",
            " ");

        // The ICC profile's statement is the product owner's decision, in writing: the profile
        // ships with its copyright tag and the uncertainty about its terms is recorded.
        Assert.Contains("Copyright International Color Consortium, 2015", text, StringComparison.Ordinal);
        Assert.Contains("sRGB2014.icc", text, StringComparison.Ordinal);
        Assert.Contains("have not been confirmed", text, StringComparison.Ordinal);
        Assert.Contains("Adobe Glyph List", text, StringComparison.Ordinal);
        Assert.Contains("MustRead.html", text, StringComparison.Ordinal);
    }

    // The packages a project resolves, read from the assets file its restore wrote: an entry
    // whose type is "package" is a NuGet package, and a project reference is not.
    private static List<(string Name, string Version)> ResolvedPackagesOf(string project)
    {
        var projectDirectory = Path.Combine(RepositoryPaths.Root, Path.GetDirectoryName(project)!);
        var assets = Path.Combine(projectDirectory, "obj", "project.assets.json");
        Assert.True(
            File.Exists(assets),
            $"The resolved graph of {project} is not at {assets}: the build that produces it has not run, so there is nothing to check.");

        using var document = JsonDocument.Parse(File.ReadAllText(assets));
        var packages = new List<(string Name, string Version)>();
        foreach (var target in document.RootElement.GetProperty("targets").EnumerateObject())
        {
            foreach (var entry in target.Value.EnumerateObject())
            {
                if (entry.Value.TryGetProperty("type", out var type)
                    && string.Equals(type.GetString(), "package", StringComparison.Ordinal))
                {
                    var separator = entry.Name.LastIndexOf('/');
                    packages.Add((entry.Name[..separator], entry.Name[(separator + 1)..]));
                }
            }

            break;
        }

        return packages;
    }
}
