using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ReportService.Eng.TestRunner;

/// <summary>The result of one test project within a run.</summary>
internal sealed record ProjectRun(
    string Name,
    string Path,
    IReadOnlyList<string> Arguments,
    int ExitCode,
    bool Passed,
    string Log,
    string? CoverageReport,
    string? Problem);

/// <summary>A container image the run used, with the identity the container runtime reports for it.</summary>
internal sealed record ImageEvidence(string Reference, string? Digest, string? Id);

/// <summary>The run header written to <c>run.json</c>.</summary>
internal sealed record RunRecord(
    string Level,
    string StartedAt,
    string FinishedAt,
    string ProductCommit,
    bool ProductWorktreeClean,
    string ReferenceEnvironment,
    bool Passed,
    int ExitCode,
    IReadOnlyList<ProjectRun> Projects,
    IReadOnlyList<string>? CoverageOptions,
    IReadOnlyList<CoverageResult>? Coverage,
    IReadOnlyList<ImageEvidence>? Images,
    IReadOnlyList<string>? ContainersBefore,
    IReadOnlyList<string>? ContainersAfter);

/// <summary>Where a run writes its evidence, and how the header is written.</summary>
internal static partial class RunEvidence
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>The repository root: the nearest directory above the tool that holds <c>global.json</c>.</summary>
    public static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "global.json")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException($"No global.json was found in {AppContext.BaseDirectory} or in any of its parent directories.");
    }

    /// <summary>
    /// Creates <c>artifacts/test-results/&lt;yyMMdd-HHmm&gt;</c> under the
    /// repository root; when a run of the same minute already used that name,
    /// a counter is appended (<c>-2</c>, <c>-3</c>, …), so no run overwrites another.
    /// </summary>
    public static string CreateRunDirectory(string repositoryRoot, DateTimeOffset startedAt)
    {
        var parent = Path.Combine(repositoryRoot, "artifacts", "test-results");
        var stamp = startedAt.ToString("yyMMdd-HHmm", CultureInfo.InvariantCulture);
        var candidate = Path.Combine(parent, stamp);
        for (var counter = 2; Directory.Exists(candidate); counter++)
        {
            candidate = Path.Combine(parent, $"{stamp}-{counter.ToString(CultureInfo.InvariantCulture)}");
        }

        Directory.CreateDirectory(candidate);
        return candidate;
    }

    /// <summary>The revision line of the reference-environment page, or a note when it cannot be read.</summary>
    public static string ReadReferenceEnvironment(string repositoryRoot)
    {
        var page = Path.Combine(repositoryRoot, "docs", "reference-environment.md");
        if (!File.Exists(page))
        {
            return "unknown (docs/reference-environment.md not found)";
        }

        var revision = File.ReadLines(page)
            .Select(line => PageRevision().Match(line))
            .FirstOrDefault(match => match.Success);
        return revision is null
            ? "unknown (no revision line in docs/reference-environment.md)"
            : $"docs/reference-environment.md revision {revision.Groups["revision"].Value}";
    }

    public static string Timestamp(DateTimeOffset value) => value.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture);

    public static void Write(string runDirectory, RunRecord record) =>
        File.WriteAllText(Path.Combine(runDirectory, "run.json"), JsonSerializer.Serialize(record, JsonOptions) + "\n");

    [GeneratedRegex(@"^\*\*Page revision:\*\*\s*(?<revision>\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex PageRevision();
}
