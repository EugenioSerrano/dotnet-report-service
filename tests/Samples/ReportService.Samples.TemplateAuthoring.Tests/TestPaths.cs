namespace ReportService.Samples.TemplateAuthoring.Tests;

/// <summary>
/// The repository and the example folders, found by walking up from the built test
/// assembly for the solution file - the same rule the dataset generator uses,
/// applied test-locally. The goldens are read from the test output because the
/// project copies them there.
/// </summary>
internal static class TestPaths
{
    /// <summary>The repository root, the nearest ancestor holding the solution file.</summary>
    public static string RepositoryRoot { get; } = FindRoot();

    /// <summary>The three examples' folder.</summary>
    public static string TemplatesFolder => Path.Combine(RepositoryRoot, "samples", "templates");

    /// <summary>The folder of one example package.</summary>
    /// <param name="id">The package's identifier.</param>
    public static string Example(string id) => Path.Combine(TemplatesFolder, id);

    /// <summary>The committed goldens, as the build copied them beside the test assembly.</summary>
    public static string Goldens => Path.Combine(AppContext.BaseDirectory, "Goldens");

    /// <summary>A temporary directory unique to one case.</summary>
    public static string Temp() => Directory.CreateTempSubdirectory("report-service-authoring-").FullName;

    private static string FindRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "dotnet-report-service.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("No folder containing dotnet-report-service.slnx was found above the test assembly.");
    }
}
