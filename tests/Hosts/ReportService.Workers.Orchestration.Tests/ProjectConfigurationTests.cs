using System.Text.Json;
using ReportService.Testing;

namespace ReportService.Workers.Orchestration.Tests;

/// <summary>
/// The configuration the project runs with is its own: the file the project ships is
/// the file its output receives, byte for byte, and it carries exactly the settings the
/// host this project starts requires and the cases do not supply themselves. The cases
/// are green only while that holds; another referenced project's file in the output
/// makes the verdict the build's, not the tree's.
/// </summary>
public sealed class ProjectConfigurationTests
{
    /// <summary>The settings the host requires and the cases leave to the configuration file.</summary>
    private static readonly string[] RequiredSettings =
    [
        "Partitioning:SingleBlockRecordLimit",
        "Uploads:Expiry:BatchSize",
        "Uploads:Expiry:SweepInterval",
        "Uploads:VerificationDeadline",
    ];

    [Fact]
    public void TheProjectOwnsTheConfigurationItRunsWith()
    {
        var source = Path.Combine(
            RepositoryPaths.Root,
            "tests", "Hosts", "ReportService.Workers.Orchestration.Tests",
            "appsettings.json");
        var output = Path.Combine(AppContext.BaseDirectory, "appsettings.json");

        // A project that starts a host with settings from a configuration file ships that file.
        Assert.True(File.Exists(source), $"The project ships no configuration of its own: {source} does not exist.");
        Assert.True(File.Exists(output), $"The project's output received no configuration: {output} does not exist.");

        // What the output carries is the project's own file, not another project's copy of one.
        var sourceHash = RunEnvironment.Sha256Of(File.ReadAllBytes(source));
        var outputHash = RunEnvironment.Sha256Of(File.ReadAllBytes(output));
        Assert.True(
            string.Equals(sourceHash, outputHash, StringComparison.Ordinal),
            $"The project's output carries a configuration the project does not own: the source's SHA-256 is {sourceHash}, the output's is {outputHash}.");

        // Exactly these settings, by their full names, at the values the host accepts.
        var settings = Settings(source);
        Assert.Equal(RequiredSettings, settings.Keys.Order(StringComparer.Ordinal));
        Assert.Equal("1000", settings["Partitioning:SingleBlockRecordLimit"]);
        Assert.Equal("00:01:00", settings["Uploads:Expiry:SweepInterval"]);
        Assert.Equal("500", settings["Uploads:Expiry:BatchSize"]);
        Assert.Equal("06:00:00", settings["Uploads:VerificationDeadline"]);
    }

    /// <summary>Every setting of the file, named by its full path and written as the text it carries.</summary>
    private static Dictionary<string, string> Settings(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        var settings = new Dictionary<string, string>(StringComparer.Ordinal);
        Collect(document.RootElement, path: null, settings);
        return settings;
    }

    /// <summary>Walks the file's sections and records each value under its full path.</summary>
    private static void Collect(JsonElement element, string? path, Dictionary<string, string> settings)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                Collect(property.Value, path is null ? property.Name : $"{path}:{property.Name}", settings);
            }

            return;
        }

        settings[path!] = element.ValueKind == JsonValueKind.String
            ? element.GetString()!
            : element.GetRawText();
    }
}
