namespace MarkdownSpike;

/// <summary>Locates the repository root and the default folders the spike reads and writes.</summary>
internal static class RepositoryPaths
{
    private const string Marker = "dotnet-report-service.slnx";

    public static string Root { get; } = FindRoot();

    public static string SpikeFolder => Path.Combine(Root, "spikes", "05-markdown");

    public static string Results => Path.Combine(SpikeFolder, "results");

    /// <summary>The sample package with HTML parts for a dataset id such as "pilot-grouped".</summary>
    public static string HtmlTemplate(string datasetId) => Path.Combine(Root, "samples", "templates", datasetId);

    /// <summary>The spike's own Markdown parts for a dataset id.</summary>
    public static string MarkdownTemplate(string datasetId) => Path.Combine(SpikeFolder, "templates", datasetId + "-md");

    private static string FindRoot()
    {
        foreach (string start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, Marker)))
                {
                    return dir.FullName;
                }
            }
        }

        throw new InvalidOperationException($"The repository root ({Marker}) was not found above the program or the current folder.");
    }
}
