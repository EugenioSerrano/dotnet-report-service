namespace ReportService.Testing;

/// <summary>
/// Locates the product repository from a running test, so a test that needs a
/// repository file reads it from its real place instead of guessing.
/// </summary>
public static class RepositoryPaths
{
    /// <summary>The file that marks the repository root.</summary>
    public const string MarkerFileName = "global.json";

    /// <summary>The repository root, found by walking up from the test's output directory.</summary>
    public static string Root => FindRoot(AppContext.BaseDirectory);

    /// <summary>Walks up from <paramref name="startDirectory"/> to the directory that holds <see cref="MarkerFileName"/>.</summary>
    public static string FindRoot(string startDirectory) => FindUpwards(startDirectory, MarkerFileName);

    /// <summary>
    /// Walks up from <paramref name="startDirectory"/> to the first directory
    /// that contains <paramref name="markerFileName"/>; throws, naming the
    /// directory the search started from, when no such directory exists.
    /// </summary>
    public static string FindUpwards(string startDirectory, string markerFileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(startDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(markerFileName);

        var start = Path.GetFullPath(startDirectory);
        for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, markerFileName)))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException(
            $"No {markerFileName} was found in {start} or in any of its parent directories.");
    }
}
