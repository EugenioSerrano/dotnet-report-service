using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
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
internal static partial class Program
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
        "tests/ReportService.TemplateChecks.Tests/ReportService.TemplateChecks.Tests.csproj",
        "tests/ReportService.Templates.Tests/ReportService.Templates.Tests.csproj",
        "tests/ReportService.TemplatePackages.Tests/ReportService.TemplatePackages.Tests.csproj",
        "tests/ReportService.TemplateSource.Tests/ReportService.TemplateSource.Tests.csproj",
        "tests/ReportService.Uploads.Tests/ReportService.Uploads.Tests.csproj",
        "tests/ReportService.Workers.Orchestration.Tests/ReportService.Workers.Orchestration.Tests.csproj",
        "tests/ReportService.Workers.Pdf.Tests/ReportService.Workers.Pdf.Tests.csproj",
        // The hermetic PDF structure verification: reads the committed golden PDFs
        // in-process (PdfPig), no container. A no-coverage hermetic project
        // (CoverageFloors.NoCoverageProjects): it owns no product assembly.
        "tests/Pdf/ReportService.PdfStructure.Tests/ReportService.PdfStructure.Tests.csproj",
    ];

    /// <summary>The rendering regression suite: the printed core tier and the independent judge, in their own images.</summary>
    private const string RenderingProject = "tests/ReportService.RenderingTests/ReportService.RenderingTests.csproj";

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

    /// <summary>
    /// The variable that names the run's evidence folder to every test process, so a class
    /// that produces an artefact a person must inspect writes it into the run's own folder
    /// instead of guessing one. It is the same name <c>RunEnvironment</c> in the shared test
    /// library reads.
    /// </summary>
    private const string RunDirectoryVariable = "REPORTSERVICE_RUN_DIRECTORY";

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
            await Console.Error.WriteLineAsync(HermeticExecution.UsagePerformance).ConfigureAwait(false);
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
            "rendering" => [RenderingProject],
            // The integration, end-to-end and performance suites were removed from this
            // repository; their level commands stay valid but run nothing.
            _ => Array.Empty<string>(),
        };

        // A level whose project set is now empty is a safe no-op: it prints and exits 0
        // rather than preparing images, building or reaping for a suite that no longer exists.
        if (projects.Length == 0)
        {
            Console.WriteLine($"The {level} level has no projects at this level; nothing to run.");
            return ExitPassed;
        }

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
            // A no-coverage hermetic project owns no product assembly by design and is
            // a valid member; every other hermetic project must own what it measures.
            var unowned = projects
                .Select(p => Path.GetFileNameWithoutExtension(p))
                .Where(name => CoverageFloors.OwnedAssemblies(name) is null && !CoverageFloors.IsNoCoverage(name))
                .ToList();
            if (unowned.Count > 0)
            {
                foreach (var project in unowned)
                {
                    await Console.Error.WriteLineAsync($"The hermetic project {project} owns no product assembly. Add its row to eng/test/ReportService.Eng.TestRunner/CoverageFloors.cs (or list it as a no-coverage hermetic project).").ConfigureAwait(false);
                }

                return ExitConfiguration;
            }
        }

        // The runner joins a job before it starts any child, so every child inherits it
        // from creation and a hard kill of the runner leaves nothing behind.
        if (!ProcessOwnership.TryAcquire(out var ownershipError))
        {
            await Console.Error.WriteLineAsync(ownershipError).ConfigureAwait(false);
            return ExitConfiguration;
        }

        var deadline = DeadlineFor(level, command.TimeoutSeconds);
        var runDirectory = RunEvidence.CreateRunDirectory(root, startedAt);
        Console.WriteLine($"Running the {level} level{(withoutPrinting ? " without the classes that need the printing steps" : string.Empty)}; evidence in {Path.GetRelativePath(root, runDirectory)}");

        var usesContainers = level != "hermetic";

        var runs = new List<ProjectRun>();
        var recordGate = new object();
        string? openPhase = null;
        string? recordProblem = null;
        var recordState = "running";
        var recordExit = -1;
        var recordPassed = false;
        string? recordFinished = null;
        IReadOnlyList<string>? coverageOptions = null;
        IReadOnlyList<CoverageResult>? coverage = null;
        IReadOnlyList<ImageEvidence>? images = null;
        IReadOnlyList<string>? containersAfter = null;
        ExecutionRecord? executionRecord = null;
        var recordWriteFailed = false;
        IReadOnlyList<string>? containersBefore = null;

        // Read before the record is written and rewritten once the captures below know
        // the truth: a kill in the first instant records `unknown` rather than nothing.
        var productCommit = "unknown";
        var productWorktreeClean = false;
        var referenceEnvironment = RunEvidence.ReadReferenceEnvironment(root);

        void Persist()
        {
            lock (recordGate)
            {
                try
                {
                    RunEvidence.Write(runDirectory, new RunRecord(
                        level,
                        RunEvidence.Timestamp(startedAt),
                        recordFinished,
                        productCommit,
                        productWorktreeClean,
                        referenceEnvironment,
                        recordPassed,
                        recordExit,
                        [.. runs],
                        coverageOptions,
                        coverage,
                        images,
                        containersBefore,
                        containersAfter,
                        executionRecord,
                        recordState,
                        openPhase,
                        deadline,
                        ProcessOwnership.Mechanism,
                        recordProblem));
                }
                catch (IOException exception)
                {
                    recordWriteFailed = true;
                    Console.Error.WriteLine($"The run's record could not be written: {exception.Message}");
                }
                catch (UnauthorizedAccessException exception)
                {
                    recordWriteFailed = true;
                    Console.Error.WriteLine($"The run's record could not be written: {exception.Message}");
                }
            }
        }

        void Begin(ProjectRun started)
        {
            lock (recordGate)
            {
                runs.Add(started);
                openPhase = "project:" + started.Name;
            }

            Persist();
        }

        void Settle(ProjectRun finished)
        {
            lock (recordGate)
            {
                if (string.Equals(finished.State, "timed-out", StringComparison.Ordinal))
                {
                    // The record the reap is preceded by is the finished, failing one.
                    recordState = "finished";
                    recordFinished = RunEvidence.Timestamp(DateTimeOffset.Now);
                    recordExit = -1;
                }

                var index = runs.FindIndex(run => string.Equals(run.Name, finished.Name, StringComparison.Ordinal));
                if (index >= 0)
                {
                    runs[index] = finished;
                }
                else
                {
                    runs.Add(finished);
                }

                // The expired phase keeps its name: it is the only field that can say
                // where a killed or timed-out attempt was. A project that ended by itself
                // hands the phase to the next project still started, or to nothing.
                if (finished.State != "timed-out" && openPhase == "project:" + finished.Name)
                {
                    openPhase = runs.FirstOrDefault(run => string.Equals(run.State, "started", StringComparison.Ordinal)) is { } next
                        ? "project:" + next.Name
                        : null;
                }
            }

            Persist();
        }

        // The record exists before the first child: the captures below are children too.
        Persist();

        // One deadline for the whole attempt — the start captures, the prepare step, the
        // build and every project — because the spans it is justified against are
        // whole-run spans.
        using var deadlineSource = new CancellationTokenSource(TimeSpan.FromSeconds(deadline.Seconds));
        var deadlineText = $"{deadline.Seconds / 60:00}:{deadline.Seconds % 60:00}";

        var startCapture = await RunCaptureAsync(root, "git", ["rev-parse", "HEAD"], deadlineSource.Token).ConfigureAwait(false);
        var statusCapture = await RunCaptureAsync(root, "git", ["status", "--porcelain", "--untracked-files=no"], deadlineSource.Token).ConfigureAwait(false);
        productCommit = startCapture.ExitCode == 0 && startCapture.Output.Trim() is { Length: > 0 } captured ? captured : "unknown";
        productWorktreeClean = statusCapture.ExitCode == 0 && statusCapture.Output.Trim().Length == 0;
        Persist();

        containersBefore = level == "integration" ? await ListSuiteContainersAsync(root, deadlineSource.Token).ConfigureAwait(false) : null;
        Persist();

        BuildRecord? build = null;
        if (level == "hermetic")
        {
            openPhase = "build";
            Persist();
            try
            {
                build = await HermeticExecution.BuildAsync(root, runDirectory, projects, deadlineSource.Token, () =>
                {
                    recordProblem ??= $"the level exceeded its deadline of {deadlineText} at {RunEvidence.Timestamp(DateTimeOffset.Now)}, during the build";
                    recordState = "finished";
                    recordFinished = RunEvidence.Timestamp(DateTimeOffset.Now);
                    recordExit = -1;
                    Persist();
                }).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                build = null;
            }
            // A build that was cancelled is null; one that finished keeps its record, even
            // if it finished as the deadline elapsed.
            if (build is null)
            {
                recordProblem ??= $"the level exceeded its deadline of {deadlineText} at {RunEvidence.Timestamp(DateTimeOffset.Now)}, during the build";
            }
            else
            {
                openPhase = null;
            }

            Persist();
        }

        var buildFailed = build is { ExitCode: not ExitPassed };

        if (buildFailed)
        {
            var named = build!.FailedProjects.Count > 0
                ? string.Join(", ", build.FailedProjects)
                : "no project could be attributed from the build log";
            await Console.Error.WriteLineAsync(
                $"The hermetic level's build failed (exit code {build.ExitCode.ToString(CultureInfo.InvariantCulture)}): {named}. No test starts; the build log is {build.Log}.").ConfigureAwait(false);
        }
        else if (!deadlineSource.IsCancellationRequested)
        {
            if (level is "e2e" or "rendering")
            {
                // The image is published once for the whole level, before any test starts:
                // the classes run concurrently and must never publish it themselves.
                openPhase = "prepare";
                Persist();
                var prepared = await PrepareRenderWorkerImageAsync(root, runDirectory, deadlineSource.Token, deadlineText, text =>
                {
                    recordProblem = text;
                    recordState = "finished";
                    recordFinished = RunEvidence.Timestamp(DateTimeOffset.Now);
                    recordExit = -1;
                    Persist();
                }).ConfigureAwait(false);
                if (prepared.State == "timed-out")
                {
                    recordProblem = prepared.Problem;
                }
                else
                {
                    openPhase = null;
                }

                runs.Add(prepared);
                Persist();
            }

            if (runs.All(run => run.Passed) && !deadlineSource.IsCancellationRequested)
            {
                if (level == "hermetic")
                {
                    var results = await HermeticExecution
                        .RunAsync(projects, execution!, project => RunProjectAsync(root, runDirectory, project, level, withoutPrinting, execution, Begin, Settle, deadlineSource.Token, deadlineText))
                        .ConfigureAwait(false);
                    foreach (var result in results)
                    {
                        Settle(result);
                    }

                    HermeticExecution.PrintSummary(root, results);
                }
                else
                {
                    foreach (var project in projects)
                    {
                        if (deadlineSource.IsCancellationRequested)
                        {
                            break;
                        }

                        Settle(await RunProjectAsync(root, runDirectory, project, level, withoutPrinting, null, Begin, Settle, deadlineSource.Token, deadlineText).ConfigureAwait(false));
                    }
                }
            }
        }

        // The attempt expired when something was recorded as expired — not merely because
        // the token fired: a child that ended as the deadline elapsed keeps its own verdict.
        // A deadline that fired before any phase recorded anything — during the start
        // captures, typically — is an expiry too: without this, an attempt that ran nothing
        // would be recorded passed, because All over an empty set is true.
        if (deadlineSource.IsCancellationRequested && recordProblem is null && !runs.Any(run => string.Equals(run.State, "timed-out", StringComparison.Ordinal)))
        {
            recordProblem = $"the level exceeded its deadline of {deadlineText} at {RunEvidence.Timestamp(DateTimeOffset.Now)}, while no phase was running under this attempt's control";
        }

        var expired = recordProblem is not null || runs.Any(run => string.Equals(run.State, "timed-out", StringComparison.Ordinal));
        var exitCode = expired
            ? recordProblem is not null ? ExitBuildFailed : ExitTestsFailed
            : buildFailed
                ? ExitBuildFailed
                : runs.All(r => r.Passed) ? ExitPassed : ExitTestsFailed;

        if (level == "hermetic" && exitCode == ExitPassed && execution!.CollectsCoverage)
        {
            coverageOptions = CoverageFloors.Options;
            coverage = EvaluateCoverage(root, runs);
            await WriteCoverageSummaryAsync(root, runDirectory, coverage).ConfigureAwait(false);
            if (coverage.Any(c => c.Failed))
            {
                exitCode = ExitCoverageFloor;
            }
        }

        images = usesContainers ? await InspectImagesAsync(root, level, deadlineSource.Token).ConfigureAwait(false) : null;
        containersAfter = level == "integration" ? await ListSuiteContainersAsync(root, deadlineSource.Token).ConfigureAwait(false) : null;

        // The closing captures take the deadline too, and a cancelled capture returns rather
        // than throwing. A token that fired while they ran is still an attempt that did not
        // finish within its bound, and must not be recorded a pass with a partial record.
        if (deadlineSource.IsCancellationRequested && recordProblem is null && !runs.Any(run => string.Equals(run.State, "timed-out", StringComparison.Ordinal)))
        {
            recordProblem = $"the level exceeded its deadline of {deadlineText} at {RunEvidence.Timestamp(DateTimeOffset.Now)}, while the attempt's closing captures ran";
            exitCode = ExitBuildFailed;
        }
        executionRecord = level == "hermetic" ? new ExecutionRecord(execution!.Mode, execution.Degree, execution.Reason, execution.CoverageRole, build!) : null;

        recordState = "finished";
        recordFinished = RunEvidence.Timestamp(DateTimeOffset.Now);
        recordPassed = exitCode == ExitPassed;

        // The record's own code is the sentinel: the attempt reached no verdict. The
        // process code says which kind of failure it was. An expiry is either a run-level
        // problem — a prepare step, a build, a closing capture — or a project entry marked
        // timed-out, so both count.
        recordExit = recordProblem is not null || runs.Any(run => string.Equals(run.State, "timed-out", StringComparison.Ordinal))
            ? -1
            : exitCode;
        if (!expired)
        {
            openPhase = null;
        }

        Persist();
        if (recordWriteFailed)
        {
            exitCode = ExitConfiguration;
        }

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

    private static async Task<ProjectRun> PrepareRenderWorkerImageAsync(string root, string runDirectory, CancellationToken cancellationToken, string deadlineText, Action<string>? onExpired = null)
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
        try
        {
            // Built when the deadline fires, never before: an eager sentence would name the
            // prepare's start as the expiry instant in the record the reap is preceded by.
            Func<string> problem = () => $"the level exceeded its deadline of {deadlineText} at {RunEvidence.Timestamp(DateTimeOffset.Now)}, during the prepare step";
            await using (var writer = new StreamWriter(log, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)) { NewLine = "\n", AutoFlush = true })
            {
                await writer.WriteLineAsync($"> dotnet {string.Join(' ', arguments.Select(Quote))}").ConfigureAwait(false);
                exitCode = await RunStreamingAsync(root, "dotnet", arguments, new Dictionary<string, string>(), writer, cancellationToken, () => onExpired?.Invoke(problem())).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            return new ProjectRun(
                name,
                AppHostProject,
                arguments,
                -1,
                false,
                Path.GetRelativePath(root, log),
                null,
                $"the level exceeded its deadline of {deadlineText} at {RunEvidence.Timestamp(DateTimeOffset.Now)}, during the prepare step",
                null,
                "timed-out");
        }

        return new ProjectRun(
            name,
            AppHostProject,
            arguments,
            exitCode,
            exitCode == 0,
            Path.GetRelativePath(root, log),
            null,
            exitCode == 0 ? null : "the render worker image preparation failed; the end-to-end project was not run",
            null,
            exitCode == 0 ? "succeeded" : "failed");
    }

    private static async Task<ProjectRun> RunProjectAsync(string root, string runDirectory, string project, string level, bool withoutPrinting, HermeticOptions? execution, Action<ProjectRun> onStart, Action<ProjectRun> onExpired, CancellationToken cancellationToken, string deadlineText)
    {
        var name = Path.GetFileNameWithoutExtension(project);
        var resultsDirectory = Path.Combine(runDirectory, name);
        Directory.CreateDirectory(resultsDirectory);
        var log = Path.Combine(runDirectory, name + ".log");
        var hermetic = execution is not null;

        var arguments = new List<string> { "test", "--project", Path.GetFullPath(Path.Combine(root, project)) };
        arguments.AddRange(hermetic ? HermeticTestOptions : TestOptions);
        arguments.AddRange(["--results-directory", resultsDirectory]);

        // A no-coverage hermetic project is never instrumented: without this, coverlet would
        // instrument every non-excluded assembly it loads (there is no owned include to scope
        // it), moving product figures and breaking "measured once".
        var collectsCoverage = hermetic && execution!.CollectsCoverage && !CoverageFloors.IsNoCoverage(name);
        if (collectsCoverage)
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

        // The run's own folder is named to every process, so a class that must leave an
        // artefact for a person to inspect writes it beside the run's record.
        environment[RunDirectoryVariable] = runDirectory;

        var line = $"> dotnet {string.Join(' ', arguments.Select(Quote))}";
        Console.WriteLine(line);

        // The entry exists before the child does, so an attempt killed in the first
        // instant still names the project that had begun.
        onStart(new ProjectRun(
            name, project, arguments, -1, false, Path.GetRelativePath(root, log), null, null, null, "started"));

        var stopwatch = Stopwatch.StartNew();

        // The expiry's entry is written by the caller before the tree is reaped.
        Action beforeReap = () => onExpired(new ProjectRun(
            name,
            project,
            arguments,
            -1,
            false,
            Path.GetRelativePath(root, log),
            null,
            $"the level exceeded its deadline of {deadlineText} at {RunEvidence.Timestamp(DateTimeOffset.Now)}, during this project",
            null,
            "timed-out"));

        // A project that cannot start, or whose log cannot be written, is one failed
        // entry with its problem: the record stays complete and the other projects run.
        var exitCode = -1;
        string? problem = null;
        try
        {
            await using (var writer = new StreamWriter(log, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)) { NewLine = "\n", AutoFlush = true })
            {
                await writer.WriteLineAsync(line).ConfigureAwait(false);
                exitCode = await RunStreamingAsync(root, "dotnet", arguments, environment, writer, cancellationToken, beforeReap).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            problem = $"the level exceeded its deadline of {deadlineText} at {RunEvidence.Timestamp(DateTimeOffset.Now)}, during this project";
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

        // A no-coverage hermetic project writes no report, so it is never held to the
        // one-report rule (zero reports would otherwise be read as a failure).
        string? report = null;
        if (collectsCoverage && exitCode == 0 && problem is null)
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
            hermetic ? Math.Round(stopwatch.Elapsed.TotalSeconds, 1) : null,
            problem is not null && problem.Contains("deadline", StringComparison.Ordinal)
                ? "timed-out"
                : exitCode == 0 && problem is null ? "succeeded" : "failed");
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

    private static async Task<IReadOnlyList<string>?> ListSuiteContainersAsync(string root, CancellationToken cancellationToken = default)
    {
        var result = await RunCaptureAsync(root, "docker", ["ps", "--all", "--filter", "name=report-service-it-", "--format", "{{.Names}} {{.Image}} {{.Status}}"], cancellationToken).ConfigureAwait(false);
        return result.ExitCode == 0
            ? [.. result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)]
            : [$"docker ps failed: {result.Output.Trim()}"];
    }

    // Reads the identities of the images the level starts; it only inspects, it never removes or prunes anything.
    private static async Task<IReadOnlyList<ImageEvidence>?> InspectImagesAsync(string root, string level, CancellationToken cancellationToken = default)
    {
        var references = ReadPinnedReferences(root);
        if (level is "e2e" or "rendering")
        {
            var listed = await RunCaptureAsync(root, "docker", ["image", "ls", "--format", "{{.Repository}}:{{.Tag}}"], cancellationToken).ConfigureAwait(false);
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
            var inspected = await RunCaptureAsync(root, "docker", ["image", "inspect", "--format", "{{json .RepoDigests}}|{{.Id}}", reference], cancellationToken).ConfigureAwait(false);
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
        StreamWriter log,
        CancellationToken cancellationToken = default,
        Action? beforeReap = null)
    {
        var startInfo = CreateStartInfo(workingDirectory, fileName, arguments);
        foreach (var (key, value) in environment)
        {
            startInfo.Environment[key] = value;
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Could not start {fileName}.");
        if (OperatingSystem.IsWindows() && ProcessOwnership.Mechanism == "per-child-job")
        {
            // The runner could not join the job itself, so each child is assigned as it
            // starts; a child created before the assignment can escape, and a survivor is
            // reported rather than reaped.
            ProcessOwnership.Assign(process);
        }

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
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // A child that ended between the deadline and this check is not expired: it is
            // recorded with its own exit code and the deadline did not catch it.
            if (process.HasExited)
            {
                lock (gate)
                {
                    log.WriteLine($"exit code {process.ExitCode.ToString(CultureInfo.InvariantCulture)} (it ended as the deadline elapsed)");
                }

                return process.ExitCode;
            }

            // The record states the failure before the tree is reaped, so a reap that raced
            // it could not lose the only evidence the attempt existed.
            lock (gate)
            {
                log.WriteLine($"the deadline expired at {RunEvidence.Timestamp(DateTimeOffset.Now)}; the caller records the failure, then this tree is reaped");
            }

            beforeReap?.Invoke();
            Reap(process);
            throw;
        }

        lock (gate)
        {
            log.WriteLine($"exit code {process.ExitCode.ToString(CultureInfo.InvariantCulture)}");
        }

        return process.ExitCode;
    }

    /// <summary>
    /// Reaps a child and everything it started. A child that ended between the check and
    /// the call throws, and that is not a failure: the tree is reaped either way.
    /// </summary>
    private static void Reap(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
        catch (Win32Exception)
        {
        }
    }

    /// <summary>
    /// The attempt's deadline: at least twice the level's longest recorded whole-run span,
    /// or the caller's own value. The performance level has no recorded run, so the parser
    /// refuses to start it without a value and no branch here invents one.
    /// </summary>
    private static DeadlineRecord DeadlineFor(string level, int? timeoutSeconds)
    {
        if (timeoutSeconds is { } given)
        {
            return new DeadlineRecord(given, "argument");
        }

        return level switch
        {
            "rendering" => new DeadlineRecord(15 * 60, "level"),
            "e2e" => new DeadlineRecord(25 * 60, "level"),
            "integration" => new DeadlineRecord(25 * 60, "level"),
            "hermetic" => new DeadlineRecord(10 * 60, "level"),
            _ => throw new InvalidOperationException($"The {level} level has no recorded run to justify a deadline; --timeout is required for it."),
        };
    }

    private static async Task<(int ExitCode, string Output)> RunCaptureAsync(string workingDirectory, string fileName, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
    {
        try
        {
            using var process = Process.Start(CreateStartInfo(workingDirectory, fileName, arguments))
                ?? throw new InvalidOperationException($"Could not start {fileName}.");
            if (OperatingSystem.IsWindows() && ProcessOwnership.Mechanism == "per-child-job")
            {
                // On the fallback no child inherits the job, so the capture children — a
                // class the survivor scan names — are assigned like the level's own.
                ProcessOwnership.Assign(process);
            }

            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return (process.ExitCode, await output.ConfigureAwait(false) + await error.ConfigureAwait(false));
        }
        catch (Win32Exception exception)
        {
            return (-1, $"{fileName} is not available: {exception.Message}");
        }
        catch (OperationCanceledException)
        {
            return (-1, $"{fileName} was cancelled: the attempt's deadline elapsed while it ran.");
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

    /// <summary>
    /// The job the runner joins so that every process it starts inherits it. A job with
    /// <c>KILL_ON_JOB_CLOSE</c> terminates whatever is left in it when its last handle
    /// closes, and the last handle is closed by the OS when this process ends, however it
    /// ends. The runner therefore never closes the handle itself: it is a member of the
    /// job, and closing it would terminate the runner.
    /// </summary>
    private static partial class ProcessOwnership
    {
        private const int ExtendedLimitInformationClass = 9;
        private const uint LimitKillOnJobClose = 0x2000;

        private static IntPtr job;

        /// <summary>How this run owns its processes, as the record states it.</summary>
        public static string Mechanism { get; private set; } = "kill-tree";

        /// <summary>
        /// Creates the job, gives it the kill-on-close limit and joins the runner to it.
        /// A host that cannot do all three is refused before a run directory exists: a
        /// runner that cannot own its children would leave them holding the build output,
        /// which is the defect this Bolt exists to remove.
        /// </summary>
        public static bool TryAcquire(out string? error)
        {
            error = null;
            if (!OperatingSystem.IsWindows())
            {
                error = "The runner owns the processes it starts through a Windows job object, and this host is not Windows; a level would leave its children behind. Run it on the reference environment.";
                return false;
            }

            job = CreateJobObjectW(IntPtr.Zero, null);
            if (job == IntPtr.Zero)
            {
                error = $"The runner could not create the job that owns the processes it starts (error {Marshal.GetLastWin32Error().ToString(CultureInfo.InvariantCulture)}).";
                return false;
            }

            var limits = default(ExtendedLimitInformation);
            limits.BasicLimitInformation.LimitFlags = LimitKillOnJobClose;
            if (!SetInformationJobObject(job, ExtendedLimitInformationClass, ref limits, (uint)Marshal.SizeOf<ExtendedLimitInformation>()))
            {
                error = $"The runner could not give its job the kill-on-close limit (error {Marshal.GetLastWin32Error().ToString(CultureInfo.InvariantCulture)}).";
                return false;
            }

            if (!AssignProcessToJobObject(job, GetCurrentProcess()))
            {
                // A host that forbids nested jobs: each child is assigned as it starts, and
                // a child created before its assignment can escape the job — the scan
                // reports any survivor instead of claiming there is none.
                Mechanism = "per-child-job";
                Console.WriteLine("The runner could not join its own job; each child is assigned to it as it starts, and a survivor is reported rather than reaped.");
                return true;
            }

            Mechanism = "job-runner";
            return true;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BasicLimitInformation
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IoCounters
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ExtendedLimitInformation
        {
            public BasicLimitInformation BasicLimitInformation;
            public IoCounters IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }

        [SupportedOSPlatform("windows")]
        [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
        private static partial IntPtr CreateJobObjectW(IntPtr attributes, string? name);

        [SupportedOSPlatform("windows")]
        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool SetInformationJobObject(IntPtr jobHandle, int informationClass, ref ExtendedLimitInformation information, uint length);

        [SupportedOSPlatform("windows")]
        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool AssignProcessToJobObject(IntPtr jobHandle, IntPtr processHandle);

        /// <summary>Assigns one child to the job, on the fallback where the runner could not join it.</summary>
        [SupportedOSPlatform("windows")]
        public static void Assign(Process process)
        {
            if (job == IntPtr.Zero)
            {
                return;
            }

            try
            {
                // A child that has already exited has no handle to assign, and that is not a
                // failure: it leaves nothing behind. Reading the handle throws in that case.
                if (!AssignProcessToJobObject(job, process.Handle))
                {
                    Console.WriteLine($"The child {process.Id.ToString(CultureInfo.InvariantCulture)} could not be assigned to the job (error {Marshal.GetLastWin32Error().ToString(CultureInfo.InvariantCulture)}); it may outlive the runner.");
                }
            }
            catch (InvalidOperationException)
            {
                // The process ended before its handle could be read: nothing to own.
            }
            catch (Win32Exception)
            {
                // The handle exists but the assignment was refused: the child is named above
                // only when the call returns false, so this path says nothing and the scan
                // of the attempt's survivors is what reports it.
            }
        }

        [SupportedOSPlatform("windows")]
        [LibraryImport("kernel32.dll")]
        private static partial IntPtr GetCurrentProcess();
    }
}
