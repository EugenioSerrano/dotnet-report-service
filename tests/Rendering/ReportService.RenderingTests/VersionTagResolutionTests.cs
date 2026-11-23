using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using ReportService.Testing;

namespace ReportService.RenderingTests;

/// <summary>
/// Hermetic checks that the rendering suite resolves its container image tags from
/// the build version instead of a hard-coded literal. These need no container
/// runtime and never touch <see cref="RenderingSuite"/>'s lifecycle; run them with
/// a targeted filter (FullyQualifiedName~VersionTagResolutionTests). None of these
/// checks pins a fixed version string, so they track a version bump instead of
/// breaking on it.
/// </summary>
public sealed class VersionTagResolutionTests
{
    /// <summary>The repository-root marker, as the composition host resolves it.</summary>
    private const string RepositoryRootMarker = "global.json";

    /// <summary>The host project whose published ContainerImageTag the suite must match.</summary>
    private static readonly string[] HostProjectSegments =
    [
        "tests", "Rendering", "ReportService.RenderingHost", "ReportService.RenderingHost.csproj",
    ];

    // A container image tag: a semantic-version-shaped prefix with an optional suffix.
    private static readonly Regex WellFormedTag =
        new(@"^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static string ResolveMetadataTag() =>
        typeof(RenderingSuite).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(attribute => attribute.Key == "RenderingImageTag")
            .Value
        ?? throw new InvalidOperationException("The RenderingImageTag assembly metadata is missing or empty.");

    [Fact]
    public void VersionTag_is_non_empty_and_well_formed()
    {
        var tag = ResolveMetadataTag();

        Assert.False(string.IsNullOrWhiteSpace(tag));
        Assert.DoesNotContain("report-service-", tag);
        Assert.DoesNotContain(":", tag);
        Assert.Matches(WellFormedTag, tag);
    }

    [Fact]
    public void Image_references_are_composed_from_the_resolved_tag()
    {
        var tag = ResolveMetadataTag();

        Assert.Equal($"report-service-rendering-host:{tag}", RenderingSuite.HostImage);
        Assert.Equal($"report-service-pdf-oracle:{tag}", RenderingSuite.OracleImage);
    }

    [Fact]
    public void VersionTag_equals_the_host_project_evaluated_ContainerImageTag()
    {
        var tag = ResolveMetadataTag();

        var repositoryRoot = TryResolveRepositoryRoot(AppContext.BaseDirectory);
        if (repositoryRoot is null)
        {
            Assert.Skip("The repository root could not be located from the test base directory; "
                + "the MSBuild evaluation of ContainerImageTag is skipped (the integration cycle proves the match).");
        }

        var hostProject = Path.Combine(new[] { repositoryRoot }.Concat(HostProjectSegments).ToArray());
        if (!File.Exists(hostProject))
        {
            Assert.Skip($"The host project {hostProject} was not found; the MSBuild evaluation is skipped.");
        }

        var evaluated = TryEvaluateContainerImageTag(repositoryRoot, hostProject);
        if (evaluated is null)
        {
            Assert.Skip("`dotnet msbuild -getProperty:ContainerImageTag` could not be run in this environment; "
                + "the MSBuild evaluation is skipped (the integration cycle proves the match).");
        }

        Assert.Equal(evaluated, tag);
    }

    private static string? TryResolveRepositoryRoot(string start)
    {
        for (var directory = new DirectoryInfo(Path.GetFullPath(start)); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, RepositoryRootMarker)))
            {
                return directory.FullName;
            }
        }

        return null;
    }

    private static string? TryEvaluateContainerImageTag(string workingDirectory, string hostProject)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo("dotnet")
                {
                    ArgumentList = { "msbuild", hostProject, "-getProperty:ContainerImageTag", "-nologo" },
                    WorkingDirectory = workingDirectory,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                },
            };

            if (!process.Start())
            {
                return null;
            }

            var (stdout, _) = ChildProcessStreams.DrainToExitAsync(process).GetAwaiter().GetResult();
            if (process.ExitCode != 0)
            {
                return null;
            }

            var value = stdout.Trim();

            // A single -getProperty prints the plain value; tolerate a JSON shape too.
            if (value.StartsWith('{'))
            {
                using var json = JsonDocument.Parse(value);
                value = json.RootElement.GetProperty("Properties").GetProperty("ContainerImageTag").GetString()?.Trim() ?? "";
            }

            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
