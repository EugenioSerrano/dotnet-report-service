using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace ReportService.Eng.TestRunner;

/// <summary>
/// One command per test level:
/// <c>dotnet run --project eng/test/ReportService.Eng.TestRunner -- hermetic|integration|e2e</c>.
/// Runs the level's test projects with <c>dotnet test</c>, writes the run
/// evidence under <c>artifacts/test-results/&lt;stamp&gt;/</c> and exits
/// non-zero, naming the cause, when any project fails or a coverage floor is
/// not met. The projects of each level are fixed here; the command takes no
/// project path from its caller.
/// </summary>
internal static class Program
{
    /// <summary>Unit/component projects: no container runtime, measured for coverage.</summary>
    private static readonly string[] HermeticProjects =
    [
        "tests/ReportService.Api.Tests/ReportService.Api.Tests.csproj",
        "tests/ReportService.Migrations.Tests/ReportService.Migrations.Tests.csproj",
        "tests/ReportService.Storage.Tests/ReportService.Storage.Tests.csproj",
    ];

    /// <summary>The integration suite: real dependencies in containers.</summary>
    private const string IntegrationProject = "tests/ReportService.IntegrationTests/ReportService.IntegrationTests.csproj";

    /// <summary>The end-to-end suite: the whole local composition.</summary>
    private const string EndToEndProject = "tests/ReportService.EndToEndTests/ReportService.EndToEndTests.csproj";

    /// <summary>Options of every <c>dotnet test</c> call.</summary>
    private static readonly string[] TestOptions = ["-c", "Release", "--no-ansi", "--no-progress"];

    private const int ExitPassed = 0;
    private const int ExitTestsFailed = 1;
    private const int ExitCoverageFloor = 2;
    private const int ExitConfiguration = 3;

    public static async Task<int> Main(string[] args)
    {
        if (args.Length != 1 || args[0] is not ("hermetic" or "integration" or "e2e"))
        {
            await Console.Error.WriteLineAsync("Usage: dotnet run --project eng/test/ReportService.Eng.TestRunner -- hermetic|integration|e2e").ConfigureAwait(false);
            return ExitConfiguration;
        }

        var level = args[0];
        var root = RunEvidence.FindRepositoryRoot();
        var startedAt = DateTimeOffset.Now;
        var projects = level switch
        {
            "hermetic" => HermeticProjects,
            "integration" => [IntegrationProject],
            _ => new[] { EndToEndProject },
        };

        var missing = projects.Where(p => !File.Exists(Path.Combine(root, p))).ToList();
        if (missing.Count > 0)
        {
            foreach (var project in missing)
            {
                await Console.Error.WriteLineAsync($"The {level} level lists {project}, which does not exist. Update the level list in eng/test/ReportService.Eng.TestRunner/Program.cs.").ConfigureAwait(false);
            }

            return ExitConfiguration;
        }

        var runDirectory = RunEvidence.CreateRunDirectory(root, startedAt);
        Console.WriteLine($"Running the {level} level; evidence in {Path.GetRelativePath(root, runDirectory)}");

        var usesContainers = level != "hermetic";
        var containersBefore = level == "integration" ? await ListSuiteContainersAsync(root).ConfigureAwait(false) : null;

        var runs = new List<ProjectRun>();
        foreach (var project in projects)
        {
            runs.Add(await RunProjectAsync(root, runDirectory, project, level).ConfigureAwait(false));
        }

        var exitCode = runs.All(r => r.Passed) ? ExitPassed : ExitTestsFailed;
        IReadOnlyList<CoverageResult>? coverage = null;
        if (level == "hermetic" && exitCode == ExitPassed)
        {
            coverage = EvaluateCoverage(root, runs);
            await WriteCoverageSummaryAsync(root, runDirectory, coverage).ConfigureAwait(false);
            if (coverage.Any(c => c.Failed))
            {
                exitCode = ExitCoverageFloor;
            }
        }

        var images = usesContainers ? await InspectImagesAsync(root, level).ConfigureAwait(false) : null;
        var containersAfter = level == "integration" ? await ListSuiteContainersAsync(root).ConfigureAwait(false) : null;

        var finishedAt = DateTimeOffset.Now;
        var commit = (await RunCaptureAsync(root, "git", ["rev-parse", "HEAD"]).ConfigureAwait(false)).Output.Trim();
        var status = await RunCaptureAsync(root, "git", ["status", "--porcelain", "--untracked-files=no"]).ConfigureAwait(false);
        RunEvidence.Write(runDirectory, new RunRecord(
            level,
            RunEvidence.Timestamp(startedAt),
            RunEvidence.Timestamp(finishedAt),
            commit.Length > 0 ? commit : "unknown",
            status.ExitCode == 0 && status.Output.Trim().Length == 0,
            RunEvidence.ReadReferenceEnvironment(root),
            exitCode == ExitPassed,
            exitCode,
            runs,
            level == "hermetic" ? CoverageFloors.Options : null,
            coverage,
            images,
            containersBefore,
            containersAfter));

        foreach (var failed in runs.Where(r => !r.Passed))
        {
            Console.WriteLine($"FAILED: {failed.Name} (exit code {failed.ExitCode}){(failed.Problem is null ? string.Empty : ": " + failed.Problem)} — log: {failed.Log}");
        }

        if (containersAfter is { Count: > 0 })
        {
            Console.WriteLine($"Containers of the suite still present after the run: {string.Join(", ", containersAfter)}");
        }

        Console.WriteLine($"The {level} level {(exitCode == ExitPassed ? "passed" : "failed")} (exit code {exitCode.ToString(CultureInfo.InvariantCulture)}); {Path.GetRelativePath(root, Path.Combine(runDirectory, "run.json"))}");
        return exitCode;
    }

    private static async Task<ProjectRun> RunProjectAsync(string root, string runDirectory, string project, string level)
    {
        var name = Path.GetFileNameWithoutExtension(project);
        var resultsDirectory = Path.Combine(runDirectory, name);
        Directory.CreateDirectory(resultsDirectory);
        var log = Path.Combine(runDirectory, name + ".log");

        var arguments = new List<string> { "test", "--project", Path.GetFullPath(Path.Combine(root, project)) };
        arguments.AddRange(TestOptions);
        arguments.AddRange(["--results-directory", resultsDirectory]);
        if (level == "hermetic")
        {
            arguments.AddRange(CoverageFloors.Options);
        }

        // The integration classes remove their containers through their own handles.
        var environment = level == "integration"
            ? new Dictionary<string, string> { ["TESTCONTAINERS_RYUK_DISABLED"] = "true" }
            : new Dictionary<string, string>();

        Console.WriteLine($"> dotnet {string.Join(' ', arguments.Select(Quote))}");
        int exitCode;
        await using (var writer = new StreamWriter(log, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)) { NewLine = "\n" })
        {
            await writer.WriteLineAsync($"> dotnet {string.Join(' ', arguments.Select(Quote))}").ConfigureAwait(false);
            exitCode = await RunStreamingAsync(root, "dotnet", arguments, environment, writer).ConfigureAwait(false);
        }

        string? report = null;
        string? problem = null;
        if (level == "hermetic" && exitCode == 0)
        {
            var reports = Directory.GetFiles(resultsDirectory, CoverageFloors.ReportPattern, SearchOption.TopDirectoryOnly);
            if (reports.Length == 1)
            {
                report = reports[0];
            }
            else
            {
                problem = reports.Length == 0
                    ? $"no coverage report ({CoverageFloors.ReportPattern}) in {Path.GetRelativePath(root, resultsDirectory)}"
                    : $"{reports.Length.ToString(CultureInfo.InvariantCulture)} coverage reports in {Path.GetRelativePath(root, resultsDirectory)}: {string.Join(", ", reports.Select(Path.GetFileName))}";
            }
        }

        return new ProjectRun(
            name,
            project,
            arguments,
            exitCode,
            exitCode == 0 && problem is null,
            Path.GetRelativePath(root, log),
            report is null ? null : Path.GetRelativePath(root, report),
            problem);
    }

    private static List<CoverageResult> EvaluateCoverage(string root, IEnumerable<ProjectRun> runs)
    {
        var figures = runs
            .Where(r => r.CoverageReport is not null)
            .SelectMany(r => CoverageFloors.Read(Path.Combine(root, r.CoverageReport!), r.CoverageReport!, r.Name))
            .ToList();
        return [.. CoverageFloors.Evaluate(figures, CoverageFloors.Floors)];
    }

    private static async Task WriteCoverageSummaryAsync(string root, string runDirectory, IReadOnlyList<CoverageResult> coverage)
    {
        var lines = new List<string>
        {
            "Coverage floors (hermetic level; rates truncated to two decimals, compared as exact ratios)",
            "Each assembly is measured only from its own test project's report; a figure of it in another project's report is excluded, never combined.",
            string.Empty,
        };
        foreach (var result in coverage)
        {
            var measured = result.LinePercentFloor is not null;
            var floors = result.LinePercentFloor is null
                ? "no floor"
                : $"floors {result.LinePercentFloor}% line / {result.BranchPercentFloor}% branch";
            lines.Add(
                $"{result.Assembly} — {(measured ? "measured by" : "reported by")} {result.Project}: {result.Result} — {result.Reason}; " +
                $"lines {result.LinesCovered}/{result.Lines} ({result.LineRate}), " +
                $"branches {result.BranchesCovered}/{result.BranchPoints} ({result.BranchRate}); report rates line-rate={result.LineRateAttribute ?? "—"} " +
                $"branch-rate={result.BranchRateAttribute ?? "—"}; {floors}");
            if (result.Failed)
            {
                var source = result.Report ?? "no report";
                Console.WriteLine($"coverage floor not met: {result.Assembly} measured by {result.Project}: {result.Reason} (from {source})");
            }
        }

        lines.Add(string.Empty);
        lines.Add("Coverage options: " + string.Join(' ', CoverageFloors.Options.Select(Quote)));
        foreach (var line in lines)
        {
            Console.WriteLine(line);
        }

        await File.WriteAllTextAsync(Path.Combine(runDirectory, "coverage-summary.txt"), string.Join('\n', lines) + "\n").ConfigureAwait(false);
    }

    private static async Task<List<string>?> ListSuiteContainersAsync(string root)
    {
        var result = await RunCaptureAsync(root, "docker", ["ps", "--all", "--filter", "name=report-service-it-", "--format", "{{.Names}} {{.Image}} {{.Status}}"]).ConfigureAwait(false);
        return result.ExitCode == 0
            ? [.. result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)]
            : [$"docker ps failed: {result.Output.Trim()}"];
    }

    // Reads the identities of the images the level starts; it only inspects, it never removes or prunes anything.
    private static async Task<List<ImageEvidence>> InspectImagesAsync(string root, string level)
    {
        var references = ReadPinnedReferences(root);
        if (level == "e2e")
        {
            var listed = await RunCaptureAsync(root, "docker", ["image", "ls", "--format", "{{.Repository}}:{{.Tag}}"]).ConfigureAwait(false);
            references.AddRange(listed.Output
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(r => r.Contains("report-service-render-worker:", StringComparison.Ordinal)
                    || r.Contains("report-service-pdf-base:", StringComparison.Ordinal)));
        }

        var images = new List<ImageEvidence>();
        foreach (var reference in references)
        {
            var inspected = await RunCaptureAsync(root, "docker", ["image", "inspect", "--format", "{{json .RepoDigests}}|{{.Id}}", reference]).ConfigureAwait(false);
            if (inspected.ExitCode != 0)
            {
                images.Add(new ImageEvidence(reference, null, null));
                continue;
            }

            var parts = inspected.Output.Trim().Split('|');
            var digests = JsonSerializer.Deserialize<string[]>(parts[0]) ?? [];
            images.Add(new ImageEvidence(reference, digests.FirstOrDefault(), parts.Length > 1 ? parts[1] : null));
        }

        return images;
    }

    private static List<string> ReadPinnedReferences(string root)
    {
        var path = Path.Combine(root, "src", "ReportService.AppHost", "composition-pins.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return [.. document.RootElement.EnumerateObject()
            .Select(entry => $"{entry.Value.GetProperty("image").GetString()}:{entry.Value.GetProperty("tag").GetString()}")];
    }

    private static string Quote(string argument) =>
        argument.Length > 0 && !argument.Any(c => char.IsWhiteSpace(c) || c is '"' or '*' or '[' or ']') ? argument : $"\"{argument}\"";

    private static async Task<int> RunStreamingAsync(
        string workingDirectory,
        string fileName,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string> environment,
        StreamWriter log)
    {
        var startInfo = CreateStartInfo(workingDirectory, fileName, arguments);
        foreach (var (key, value) in environment)
        {
            startInfo.Environment[key] = value;
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Could not start {fileName}.");
        var gate = new object();
        void Write(string? line)
        {
            if (line is null)
            {
                return;
            }

            lock (gate)
            {
                Console.WriteLine(line);
                log.WriteLine(line);
            }
        }

        process.OutputDataReceived += (_, e) => Write(e.Data);
        process.ErrorDataReceived += (_, e) => Write(e.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync().ConfigureAwait(false);
        lock (gate)
        {
            log.WriteLine($"exit code {process.ExitCode.ToString(CultureInfo.InvariantCulture)}");
        }

        return process.ExitCode;
    }

    private static async Task<(int ExitCode, string Output)> RunCaptureAsync(string workingDirectory, string fileName, IReadOnlyList<string> arguments)
    {
        try
        {
            using var process = Process.Start(CreateStartInfo(workingDirectory, fileName, arguments))
                ?? throw new InvalidOperationException($"Could not start {fileName}.");
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().ConfigureAwait(false);
            return (process.ExitCode, await output.ConfigureAwait(false) + await error.ConfigureAwait(false));
        }
        catch (Win32Exception exception)
        {
            return (-1, $"{fileName} is not available: {exception.Message}");
        }
    }

    private static ProcessStartInfo CreateStartInfo(string workingDirectory, string fileName, IReadOnlyList<string> arguments)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }
}
