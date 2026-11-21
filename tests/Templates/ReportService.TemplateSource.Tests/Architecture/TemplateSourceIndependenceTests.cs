using System.Text.RegularExpressions;
using ReportService.Testing;

namespace ReportService.TemplateSource.Tests.Architecture;

/// <summary>
/// The template source project's independence from any HTTP client, held as a test
/// rather than as a habit: the project references no package that carries one, and
/// no source file it builds names one. The source a take reads is the deployment's
/// object store, reached through the storage port; a client to a remote host has no
/// place in this project.
/// </summary>
public sealed partial class TemplateSourceIndependenceTests
{
    private const string ProjectName = "ReportService.TemplateSource";

    private const string ProjectFile = ProjectName + ".csproj";

    private static readonly string[] HttpTypes =
    [
        "HttpClient",
        "IHttpClientFactory",
        "HttpRequestMessage",
        "HttpResponseMessage",
    ];

    [Fact]
    public void TheTemplateSourceProjectCarriesNoHttpClient()
    {
        var project = Path.Combine(RepositoryPaths.Root, "src", "Templates", ProjectName, ProjectFile);
        Assert.True(File.Exists(project), $"{ProjectFile} was not found at {project}.");

        foreach (var package in PackageReferences(File.ReadAllText(project)))
        {
            Assert.False(
                package.Contains("Http", StringComparison.OrdinalIgnoreCase),
                $"{ProjectFile} references '{package}', a package that carries an HTTP client. This project's source is the deployment's object store, reached through the storage port.");
        }

        foreach (var file in SourceFiles())
        {
            var lines = File.ReadAllLines(file);
            for (var index = 0; index < lines.Length; index++)
            {
                var named = Array.Find(HttpTypes, type => lines[index].Contains(type, StringComparison.Ordinal));
                Assert.True(
                    named is null,
                    $"{Path.GetFileName(file)}:{index + 1} names '{named}'. This project's source is the deployment's object store, reached through the storage port, and it names no HTTP client.");
            }
        }
    }

    private static IEnumerable<string> PackageReferences(string project) =>
        PackageReferencePattern()
            .Matches(project)
            .Select(match => match.Groups["package"].Value);

    private static IEnumerable<string> SourceFiles()
    {
        var directory = Path.Combine(RepositoryPaths.Root, "src", "Templates", ProjectName);
        return Directory.GetFiles(directory, "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
    }

    [GeneratedRegex(@"PackageReference\s+Include=""(?<package>[^""]+)""", RegexOptions.CultureInvariant)]
    private static partial Regex PackageReferencePattern();
}
