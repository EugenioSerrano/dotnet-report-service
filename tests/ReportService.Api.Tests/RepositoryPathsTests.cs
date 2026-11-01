using System.Globalization;
using ReportService.Testing;

namespace ReportService.Api.Tests;

/// <summary>Locating the repository root from a running test.</summary>
public sealed class RepositoryPathsTests : IDisposable
{
    private readonly TemporaryDirectory _directory = new();

    public void Dispose() => _directory.Dispose();

    [Fact]
    public void RootIsTheDirectoryThatHoldsTheSolution()
    {
        var root = RepositoryPaths.Root;

        Assert.True(File.Exists(Path.Combine(root, "global.json")));
        Assert.True(File.Exists(Path.Combine(root, "dotnet-report-service.slnx")));
    }

    [Fact]
    public void SearchWalksUpToTheNearestDirectoryWithTheMarker()
    {
        _directory.Write("marker.json", "{}");
        var nested = Path.Combine(_directory.Path, "a", "b", "c");
        Directory.CreateDirectory(nested);

        var found = RepositoryPaths.FindUpwards(nested, "marker.json");

        Assert.Equal(Path.GetFullPath(_directory.Path), found);
    }

    [Fact]
    public void MissingMarkerFailsNamingTheSearchedDirectory()
    {
        var nested = Path.Combine(_directory.Path, "a");
        Directory.CreateDirectory(nested);
        var marker = $"absent-{Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)}.json";

        var failure = Assert.Throws<DirectoryNotFoundException>(() => RepositoryPaths.FindUpwards(nested, marker));

        Assert.Contains(Path.GetFullPath(nested), failure.Message, StringComparison.Ordinal);
        Assert.Contains(marker, failure.Message, StringComparison.Ordinal);
    }
}
