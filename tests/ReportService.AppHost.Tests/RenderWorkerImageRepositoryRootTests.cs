using ReportService.AppHost;

namespace ReportService.AppHost.Tests;

/// <summary>
/// The repository-root resolution the render-worker-image prepare step relies on.
/// The AppHost sits three directories below the repository root under the grouped
/// layout (<c>src/ReportService.AppHost</c>), and the resolution must reach
/// the root from there without doubling a path segment, so the worker entry project
/// path it builds is the real file.
/// </summary>
public sealed class RenderWorkerImageRepositoryRootTests
{
    [Fact]
    public void ResolvesTheRootForAnAppHostThreeLevelsDeepWithoutDoublingASegment()
    {
        using var tree = new FixtureTree();

        var resolvedRoot = RenderWorkerImage.ResolveRepositoryRoot(tree.AppHostDirectory);
        var workerProject = RenderWorkerImage.WorkerProjectPath(resolvedRoot);

        Assert.Equal(tree.Root, resolvedRoot);
        Assert.DoesNotContain(
            Path.Combine("src", "src"),
            resolvedRoot,
            StringComparison.Ordinal);
        Assert.True(
            File.Exists(workerProject),
            $"the resolved worker entry project '{workerProject}' has to exist in the tree rooted at '{tree.Root}'");
    }

    /// <summary>
    /// A disposable fixture mirroring the real layout: a root that holds the marker
    /// file, the AppHost directory three levels below it, and the worker entry project
    /// where the prepare step expects it.
    /// </summary>
    private sealed class FixtureTree : IDisposable
    {
        public FixtureTree()
        {
            Root = Path.Combine(
                Path.GetTempPath(),
                "report-service-apphost-root-" + Guid.NewGuid().ToString("N"));

            AppHostDirectory = Path.Combine(Root, "src", "ReportService.AppHost");
            Directory.CreateDirectory(AppHostDirectory);

            File.WriteAllText(Path.Combine(Root, "global.json"), "{}");

            var workerDirectory = Path.Combine(Root, "src", "Pdf", "ReportService.Workers.Pdf.Entry");
            Directory.CreateDirectory(workerDirectory);
            File.WriteAllText(
                Path.Combine(workerDirectory, "ReportService.Workers.Pdf.Entry.csproj"),
                "<Project />");
        }

        public string Root { get; }

        public string AppHostDirectory { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (IOException)
            {
                // A leftover temporary directory is not a test failure.
            }
        }
    }
}
