namespace ReportService.Samples.DatasetGenerator;

/// <summary>Finds the repository root and the default locations below it.</summary>
internal static class RepositoryPaths
{
    private const string SolutionFileName = "dotnet-report-service.slnx";

    /// <summary>
    /// Returns the nearest ancestor of the current directory (itself included)
    /// that contains the solution file, or <c>null</c> when there is none.
    /// </summary>
    public static string? FindRoot()
    {
        for (var directory = new DirectoryInfo(Directory.GetCurrentDirectory()); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, SolutionFileName)))
            {
                return directory.FullName;
            }
        }

        return null;
    }

    /// <summary>Default output folder of the generated datasets.</summary>
    public static string DatasetsFolder(string root) => Path.Combine(root, "samples", "datasets");

    /// <summary>Default location of the expected hashes.</summary>
    public static string SumsFile(string root) => Path.Combine(DatasetsFolder(root), "SHA256SUMS");

    /// <summary>Default folder of the sample template packages.</summary>
    public static string TemplatesFolder(string root) => Path.Combine(root, "samples", "templates");
}
