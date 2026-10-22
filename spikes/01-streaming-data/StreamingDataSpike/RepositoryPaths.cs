namespace StreamingDataSpike;

/// <summary>Locates the repository root: the nearest folder above the current directory holding the solution file.</summary>
internal static class RepositoryPaths
{
    private static readonly Lazy<string> RootPath = new(FindRoot);

    /// <summary>The repository root.</summary>
    public static string Root => RootPath.Value;

    private static string FindRoot()
    {
        for (var directory = new DirectoryInfo(Directory.GetCurrentDirectory()); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "dotnet-report-service.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Run this command from inside the repository (no dotnet-report-service.slnx found above the current folder).");
    }
}
