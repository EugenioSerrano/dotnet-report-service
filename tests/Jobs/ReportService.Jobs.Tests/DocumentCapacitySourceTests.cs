namespace ReportService.Jobs.Tests;

/// <summary>
/// The product compiles no document ceiling: the example capacity value appears in the
/// product's sources only where the example profile is declared, and no source names the
/// former compiled ceilings. A refusal for size can then only read a configured value.
/// </summary>
public sealed class DocumentCapacitySourceTests
{
    private static readonly string[] ForbiddenEverywhere =
    [
        "DocumentCeiling",
        "AssemblyLimits.MaximumPages",
        "AssemblyLimits.MaximumPartBytes",
    ];

    private static readonly string[] ExampleValues = ["37_748_736", "37748736"];

    [Fact]
    public void NoProductSourceCompilesADocumentCeiling()
    {
        var source = Path.Combine(RepositoryRoot(), "src");
        var declaration = Path.GetFullPath(Path.Combine(source, "Jobs", "ReportService.Jobs", "DocumentCapacity.cs"));
        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(source, "*.cs", SearchOption.AllDirectories))
        {
            if (IsBuildOutput(file))
            {
                continue;
            }

            var text = File.ReadAllText(file);
            offenders.AddRange(ForbiddenEverywhere
                .Where(token => text.Contains(token, StringComparison.Ordinal))
                .Select(token => Path.GetRelativePath(source, file) + ": " + token));
            if (!string.Equals(Path.GetFullPath(file), declaration, StringComparison.OrdinalIgnoreCase))
            {
                offenders.AddRange(ExampleValues
                    .Where(token => text.Contains(token, StringComparison.Ordinal))
                    .Select(token => Path.GetRelativePath(source, file) + ": " + token));
            }
        }

        Assert.Empty(offenders);
    }

    private static bool IsBuildOutput(string file) =>
        file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
        || file.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal);

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "dotnet-report-service.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("the repository root was not found above the test's output");
    }
}
