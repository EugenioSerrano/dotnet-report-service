using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace ReportService.Eng.TestRunner;

/// <summary>
/// One command per test level:
/// <c>dotnet run --project eng/test/ReportService.Eng.TestRunner -- hermetic|integration|rendering|e2e</c>.
/// Runs the level's test projects with <c>dotnet test</c>, writes the run
/// evidence under <c>artifacts/test-results/&lt;stamp&gt;/</c> and exits
/// non-zero, naming the cause, when any project fails or a coverage floor is
/// not met. The projects of each level are fixed here; the command takes no
/// project path from its caller. The integration and end-to-end levels accept
/// <c>--without-printing</c>, which leaves every test class marked as needing
/// the printing steps out of the run — for a check on a build that cannot print
/// yet, never for a covering run.
/// </summary>
internal static class Program
{
    /// <summary>Unit/component projects: no container runtime, measured for coverage.</summary>
    private static readonly string[] HermeticProjects =
    [
        "tests/ReportService.Api.Tests/ReportService.Api.Tests.csproj",
        "tests/ReportService.Data.Tests/ReportService.Data.Tests.csproj",
        "tests/ReportService.DataContract.Tests/ReportService.DataContract.Tests.csproj",
        "tests/ReportService.Fixtures.Tests/ReportService.Fixtures.Tests.csproj",
        "tests/ReportService.Jobs.Tests/ReportService.Jobs.Tests.csproj",
        "tests/ReportService.Migrations.Tests/ReportService.Migrations.Tests.csproj",
        "tests/ReportService.Pdf.Verification.Tests/ReportService.Pdf.Verification.Tests.csproj",
        "tests/ReportService.PdfOracle.Tests/ReportService.PdfOracle.Tests.csproj",
        "tests/ReportService.Rendering.Tests/ReportService.Rendering.Tests.csproj",
        "tests/ReportService.Storage.Tests/ReportService.Storage.Tests.csproj",
        "tests/ReportService.Templates.Tests/ReportService.Templates.Tests.csproj",
        "tests/ReportService.TemplatePackages.Tests/ReportService.TemplatePackages.Tests.csproj",
        "tests/ReportService.TemplateSource.Tests/ReportService.TemplateSource.Tests.csproj",
        "tests/ReportService.Uploads.Tests/ReportService.Uploads.Tests.csproj",
        "tests/ReportService.Workers.Orchestration.Tests/ReportService.Workers.Orchestration.Tests.csproj",
        "tests/ReportService.Workers.Pdf.Tests/ReportService.Workers.Pdf.Tests.csproj",
    ];

    /// <summary>The rendering regression suite: the printed core tier and the independent judge, in their own images.</summary>
    private const string RenderingProject = "tests/ReportService.RenderingTests/ReportService.RenderingTests.csproj";

    /// <summary>The integration suite: real dependencies in containers.</summary>
    private const string IntegrationProject = "tests/ReportService.IntegrationTests/ReportService.IntegrationTests.csproj";

    /// <summary>The end-to-end suite: the whole local composition.</summary>
    private const string EndToEndProject = "tests/ReportService.EndToEndTests/ReportService.EndToEndTests.csproj";

    /// <summary>The composition host; the level prepares the render worker image through it.</summary>
    private const string AppHostProject = "src/ReportService.AppHost/ReportService.AppHost.csproj";

    /// <summary>The argument that makes the composition host prepare the render worker image and exit.</summary>
    private const string PrepareOnlyArgument = "--prepare-render-worker-image";

    /// <summary>
    /// The argument that leaves the test classes marked as needing the printing steps out of
    /// an integration or end-to-end run. The covering run never passes it.
    /// </summary>
    public const string WithoutPrintingArgument = "--without-printing";

    /// <summary>The trait every test class that needs the printing steps carries.</summary>
    public const string RequiresPrintingTrait = "Requires=printing";

    /// <summary>The variable that tells each composition the run has prepared the render worker image.</summary>
    private const string ImagePreparedVariable = "REPORTSERVICE_RENDER_WORKER_IMAGE_PREPARED";

    /// <summary>Options of every <c>dotnet test</c> call.</summary>
    private static readonly string[] TestOptions = ["-c", "Release", "--no-ansi", "--no-progress"];

    /// <summary>
    /// The same options for a hermetic project, plus the switch that runs the test
    /// application the level's build already produced instead of building it again.
    /// </summary>
    private static readonly string[] HermeticTestOptions = ["-c", "Release", "--no-build", "--no-ansi", "--no-progress"];

    private const int ExitPassed = 0;
    private const int ExitTestsFailed = 1;
    private const int ExitCoverageFloor = 2;
    private const int ExitConfiguration = 3;

    /// <summary>The hermetic level's one build failed: no test started, and the build named the project.</summary>
    private const int ExitBuildFailed = 4;

    public static async Task<int> Main(string[] args)
    {
        if (!HermeticExecution.TryParse(args, HermeticProjects.Length, out var command, out var error))
        {
            await Console.Error.WriteLineAsync(error).ConfigureAwait(false);
            await Console.Error.WriteLineAsync(HermeticExecution.UsageLine).ConfigureAwait(false);
            await Console.Error.WriteLineAsync(HermeticExecution.UsageOptions).ConfigureAwait(false);
            return ExitConfiguration;
        }

        var level = command!.Level;
        var withoutPrinting = command.WithoutPrinting;
        var execution = command.Execution;

        var root = RunEvidence.FindRepositoryRoot();
        var startedAt = DateTimeOffset.Now;
        var projects = level switch
        {
            "hermetic" => HermeticProjects,
            "integration" => [IntegrationProject],
            "rendering" => [RenderingProject],
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

        // Every hermetic project must own the assemblies it measures, so an assembly is
        // never measured by two projects and never measured by a project that merely
        // exercises it.
        if (level == "hermetic")
        {
            var unowned = projects
                .Select(p => Path.GetFileNameWithoutExtension(p))
                .Where(name => CoverageFloors.OwnedAssemblies(name) is null)
                .ToList();
            if (unowned.Count > 0)
            {
                foreach (var project in unowned)
                {
                    await Console.Error.WriteLineAsync($"The hermetic project {project} owns no product assembly. Add its row to eng/test/ReportService.Eng.TestRunner/CoverageFloors.cs.").ConfigureAwait(false);
                }

                return ExitConfiguration;
            }
        }

        var runDirectory = RunEvidence.CreateRunDirectory(root, startedAt);
        Console.WriteLine($"Running the {level} level{(withoutPrinting ? " without the classes that need the printing steps" : string.Empty)}; evidence in {Path.GetRelativePath(root, runDirectory)}");

        var usesContainers = level != "hermetic";
        var containersBefore = level == "integration" ? await ListSuiteContainersAsync(root).ConfigureAwait(false) : null;

        // The hermetic level builds once, before anything else runs; the integration
        // and end-to-end levels keep their own per-invocation builds.
        BuildRecord? build = level == "hermetic"
            ? await HermeticExecution.BuildAsync(root, runDirectory, projects).ConfigureAwait(false)
            : null;
        var buildFailed = build is { ExitCode: not ExitPassed };

        var runs = new List<ProjectRun>();
        if (buildFailed)
        {
            var named = build!.FailedProjects.Count > 0
                ? string.Join(", ", build.FailedProjects)
                : "no project could be attributed from the build log";
            await Console.Error.WriteLineAsync(
                $"The hermetic level's build failed (exit code {build.ExitCode.ToString(CultureInfo.InvariantCulture)}): {named}. No test starts; the build log is {build.Log}.").ConfigureAwait(false);
        }
        else
        {
            if (level is "e2e" or "rendering")
            {
                // The image is published once for the whole level, before any test starts:
                // the classes run concurrently and must never publish it themselves.
                runs.Add(await PrepareRenderWorkerImageAsync(root, runDirectory).ConfigureAwait(false));
            }

            if (runs.All(run => run.Passed))
            {
                if (level == "hermetic")
                {
                    var results = await HermeticExecution
                        .RunAsync(projects, execution!, project => RunProjectAsync(root, runDirectory, project, level, withoutPrinting, execution))
                        .ConfigureAwait(false);
                    runs.AddRange(results);
                    HermeticExecution.PrintSummary(root, results);
                }
                else
                {
                    foreach (var project in projects)
                    {
                        runs.Add(await RunProjectAsync(root, runDirectory, project, level, withoutPrinting, null).ConfigureAwait(false));
                    }
                }
            }
        }

        var exitCode = buildFailed
            ? ExitBuildFailed
            : runs.All(r => r.Passed) ? ExitPassed : ExitTestsFailed;
        IReadOnlyList<CoverageResult>? coverage = null;
        if (level == "hermetic" && exitCode == ExitPassed && execution!.CollectsCoverage)
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
            level == "hermetic" && execution!.CollectsCoverage ? CoverageFloors.Options : null,
            coverage,
            images,
            containersBefore,
            containersAfter,
            level == "hermetic" ? new ExecutionRecord(execution!.Mode, execution.Degree, execution.Reason, execution.CoverageRole, build!) : null));

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

    private static async Task<ProjectRun> PrepareRenderWorkerImageAsync(string root, string runDirectory)
    {
        const string name = "ReportService.AppHost";
        var log = Path.Combine(runDirectory, "render-worker-image.log");
        var arguments = new List<string>
        {
            "run",
            "--project", Path.GetFullPath(Path.Combine(root, AppHostProject)),
            "-c", "Release",
            "--no-launch-profile",
            "--", PrepareOnlyArgument,
        };

        Console.WriteLine($"> dotnet {string.Join(' ', arguments.Select(Quote))}");
        int exitCode;
        await using (var writer = new StreamWriter(log, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)) { NewLine = "\n" })
        {
            await writer.WriteLineAsync($"> dotnet {string.Join(' ', arguments.Select(Quote))}").ConfigureAwait(false);
            exitCode = await RunStreamingAsync(root, "dotnet", arguments, new Dictionary<string, string>(), writer).ConfigureAwait(false);
        }

        return new ProjectRun(
            name,
            AppHostProject,
            arguments,
            exitCode,
            exitCode == 0,
            Path.GetRelativePath(root, log),
            null,
            exitCode == 0 ? null : "the render worker image preparation failed; the end-to-end project was not run");
    }

    private static async Task<ProjectRun> RunProjectAsync(string root, string runDirectory, string project, string level, bool withoutPrinting, HermeticOptions? execution)
    {
        var name = Path.GetFileNameWithoutExtension(project);
        var resultsDirectory = Path.Combine(runDirectory, name);
        Directory.CreateDirectory(resultsDirectory);
        var log = Path.Combine(runDirectory, name + ".log");
        var hermetic = execution is not null;

        var arguments = new List<string> { "test", "--project", Path.GetFullPath(Path.Combine(root, project)) };
        arguments.AddRange(hermetic ? HermeticTestOptions : TestOptions);
        arguments.AddRange(["--results-directory", resultsDirectory]);
        if (hermetic && execution!.CollectsCoverage)
        {
            arguments.AddRange(CoverageFloors.Options);
            arguments.AddRange(CoverageFloors.ProjectIncludes(name));
        }

        // The platform's own switch for leaving a trait's tests out; it goes after the
        // separator, because it is an argument of the test application and not of dotnet test.
        if (withoutPrinting)
        {
            arguments.AddRange(["--", "--filter-not-trait", RequiresPrintingTrait]);
        }

        // The integration classes remove their containers through their own handles.
        var environment = level switch
        {
            "integration" => new Dictionary<string, string> { ["TESTCONTAINERS_RYUK_DISABLED"] = "true" },
            "e2e" => new Dictionary<string, string> { [ImagePreparedVariable] = "true" },
            _ => new Dictionary<string, string>(),
        };

        var line = $"> dotnet {string.Join(' ', arguments.Select(Quote))}";
        Console.WriteLine(line);
        var stopwatch = Stopwatch.StartNew();

        // A project that cannot start, or whose log cannot be written, is one failed
        // entry with its problem: the record stays complete and the other projects run.
        var exitCode = -1;
        string? problem = null;
        try
        {
            await using (var writer = new StreamWriter(log, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)) { NewLine = "\n" })
            {
                await writer.WriteLineAsync(line).ConfigureAwait(false);
                exitCode = await RunStreamingAsync(root, "dotnet", arguments, environment, writer).ConfigureAwait(false);
            }
        }
        catch (Win32Exception exception)
        {
            problem = $"the invocation could not start: {exception.Message}";
        }
        catch (InvalidOperationException exception)
        {
            problem = $"the invocation could not start: {exception.Message}";
        }
        catch (IOException exception)
        {
            problem = $"the invocation's log could not be written: {exception.Message}";
        }

        stopwatch.Stop();

        string? report = null;
        if (hermetic && execution!.CollectsCoverage && exitCode == 0 && problem is null)
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
            problem,
            hermetic ? Math.Round(stopwatch.Elapsed.TotalSeconds, 1) : null);
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
        if (level is "e2e" or "rendering")
        {
            var listed = await RunCaptureAsync(root, "docker", ["image", "ls", "--format", "{{.Repository}}:{{.Tag}}"]).ConfigureAwait(false);
            references.AddRange(listed.Output
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(r => r.Contains("report-service-render-worker:", StringComparison.Ordinal)
                    || r.Contains("report-service-rendering-host:", StringComparison.Ordinal)
                    || r.Contains("report-service-pdf-oracle:", StringComparison.Ordinal)
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

    internal static string Quote(string argument) =>
        argument.Length > 0 && !argument.Any(c => char.IsWhiteSpace(c) || c is '"' or '*' or '[' or ']') ? argument : $"\"{argument}\"";

    internal static async Task<int> RunStreamingAsync(
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

    internal static ProcessStartInfo CreateStartInfo(string workingDirectory, string fileName, IReadOnlyList<string> arguments)
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
