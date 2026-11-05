using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace ReportService.Eng.TestRunner;

/// <summary>
/// How the hermetic level executes its projects: the mode and its degree, or
/// the reason it ran one invocation at a time, and the coverage role of the run.
/// A run is never half-parallel: the mode says which one produced it.
/// </summary>
internal sealed record HermeticOptions(string Mode, int? Degree, string? Reason, string CoverageRole)
{
    /// <summary>The projects run with a bounded degree of concurrency.</summary>
    public const string ParallelMode = "parallel";

    /// <summary>The projects run one at a time, in the level's order, with a recorded reason.</summary>
    public const string SequentialMode = "sequential";

    /// <summary>The run collects coverage and the floors are evaluated from its reports.</summary>
    public const string MeasureRole = "measure";

    /// <summary>The run repeats the suite plainly: no coverage options, no floor evaluation.</summary>
    public const string VerificationRole = "verification";

    /// <summary>The degree of concurrency of a parallel run when the caller passes none.</summary>
    public const int DefaultDegree = 4;

    /// <summary>Whether this run passes the coverage options and expects a report per project.</summary>
    public bool CollectsCoverage => string.Equals(CoverageRole, MeasureRole, StringComparison.Ordinal);
}

/// <summary>The command a caller asked for: the level, whether the printing classes are left out, and the hermetic execution options.</summary>
internal sealed record RunnerCommand(string Level, bool WithoutPrinting, HermeticOptions? Execution);

/// <summary>
/// The hermetic level's execution mechanics: the options of one run, the single
/// Release build that precedes every invocation, and the scheduler that runs the
/// level's projects against that build. One responsibility per member — how the
/// hermetic level's projects are built and run; the level, the run directory, the
/// exit code and the record stay in <see cref="Program"/>.
/// </summary>
internal static partial class HermeticExecution
{
    /// <summary>The generated MSBuild traversal of the level's projects, written into the run directory.</summary>
    public const string TraversalFileName = "hermetic-build.proj";

    /// <summary>The full output of the one build, written into the run directory.</summary>
    public const string BuildLogFileName = "hermetic-build.log";

    /// <summary>The command line of the level, as the usage message states it.</summary>
    public const string UsageLine = "Usage: dotnet run --project eng/test/ReportService.Eng.TestRunner -- hermetic|integration|rendering|e2e|performance [--without-printing]";

    /// <summary>The options the hermetic level accepts, as the usage message states them.</summary>
    public const string UsageOptions = "       hermetic: [--degree <n>] [--sequential --reason \"<text>\"] [--coverage-role <measure|verification>]";

    /// <summary>The message of <c>--without-printing</c> on the hermetic level, which has no printing classes.</summary>
    public const string WithoutPrintingOnHermetic =
        "--without-printing selects the group of the integration and end-to-end levels that needs the printing steps; the hermetic level has none.";

    /// <summary>The message of <c>--without-printing</c> on the rendering level, which prints by definition.</summary>
    public const string WithoutPrintingOnRendering =
        "--without-printing selects the group of the integration and end-to-end levels that needs the printing steps; the rendering level prints every document it judges.";

    /// <summary>The message of <c>--without-printing</c> on the performance level, which prints by definition.</summary>
    public const string WithoutPrintingOnPerformance =
        "--without-printing selects the group of the integration and end-to-end levels that needs the printing steps; the performance level prints the documents it measures.";

    /// <summary>
    /// Reads the caller's arguments. The level comes first; the hermetic level
    /// accepts <c>--degree</c>, <c>--sequential --reason</c> and
    /// <c>--coverage-role</c>, and nothing else does. A repeated option, an
    /// unknown option, a degree that is not an integer or is outside
    /// <c>2…projectCount</c>, a missing or empty reason, a reason without
    /// <c>--sequential</c> and <c>--sequential</c> with <c>--degree</c> are all
    /// configuration errors: the caller gets the message and the usage line.
    /// </summary>
    public static bool TryParse(IReadOnlyList<string> args, int projectCount, out RunnerCommand? command, out string? error)
    {
        command = null;
        error = null;
        string? level = null;
        var withoutPrinting = false;
        int? degree = null;
        var sequential = false;
        string? reason = null;
        string? coverageRole = null;

        for (var index = 0; index < args.Count; index++)
        {
            var argument = args[index];
            switch (argument)
            {
                case "--without-printing":
                    if (withoutPrinting)
                    {
                        error = $"{argument} is given more than once.";
                        return false;
                    }

                    withoutPrinting = true;
                    break;

                case "--degree":
                    if (degree is not null)
                    {
                        error = $"{argument} is given more than once.";
                        return false;
                    }

                    if (!TryReadValue(args, ref index, argument, out var degreeText, out error))
                    {
                        return false;
                    }

                    if (!int.TryParse(degreeText, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) || parsed < 2 || parsed > projectCount)
                    {
                        error = $"{argument} takes an integer from 2 to {projectCount.ToString(CultureInfo.InvariantCulture)}, the number of projects in the level; '{degreeText}' is not one.";
                        return false;
                    }

                    degree = parsed;
                    break;

                case "--sequential":
                    if (sequential)
                    {
                        error = $"{argument} is given more than once.";
                        return false;
                    }

                    sequential = true;
                    break;

                case "--reason":
                    if (reason is not null)
                    {
                        error = $"{argument} is given more than once.";
                        return false;
                    }

                    if (!TryReadValue(args, ref index, argument, out var reasonText, out error))
                    {
                        return false;
                    }

                    if (reasonText.Trim().Length == 0)
                    {
                        error = $"{argument} needs the text that explains why the level ran one invocation at a time.";
                        return false;
                    }

                    reason = reasonText;
                    break;

                case "--coverage-role":
                    if (coverageRole is not null)
                    {
                        error = $"{argument} is given more than once.";
                        return false;
                    }

                    if (!TryReadValue(args, ref index, argument, out var roleText, out error))
                    {
                        return false;
                    }

                    if (roleText is not (HermeticOptions.MeasureRole or HermeticOptions.VerificationRole))
                    {
                        error = $"{argument} takes '{HermeticOptions.MeasureRole}' or '{HermeticOptions.VerificationRole}'; '{roleText}' is not one.";
                        return false;
                    }

                    coverageRole = roleText;
                    break;

                default:
                    if (argument.Length > 0 && argument[0] == '-')
                    {
                        error = $"{argument} is not an option of the runner.";
                        return false;
                    }

                    if (level is not null)
                    {
                        error = $"The command takes exactly one level; '{argument}' follows '{level}'.";
                        return false;
                    }

                    level = argument;
                    break;
            }
        }

        if (level is not ("hermetic" or "integration" or "rendering" or "e2e" or "performance"))
        {
            error = level is null ? "The command needs one level." : $"'{level}' is not a level of the runner.";
            return false;
        }

        if (level == "performance" && withoutPrinting)
        {
            error = WithoutPrintingOnPerformance;
            return false;
        }

        var askedForHermeticOptions = degree is not null || sequential || reason is not null || coverageRole is not null;
        if (level != "hermetic")
        {
            if (askedForHermeticOptions)
            {
                error = $"--degree, --sequential, --reason and --coverage-role apply to the hermetic level only; the {level} level does not take them.";
                return false;
            }

            command = new RunnerCommand(level, withoutPrinting, null);
            return true;
        }

        if (withoutPrinting)
        {
            error = level == "rendering" ? WithoutPrintingOnRendering : WithoutPrintingOnHermetic;
            return false;
        }

        if (sequential && degree is not null)
        {
            error = "--sequential runs one invocation at a time and --degree bounds a parallel run; the two cannot be given together.";
            return false;
        }

        if (sequential && reason is null)
        {
            error = "--sequential needs --reason with the text that explains why the level ran one invocation at a time.";
            return false;
        }

        if (!sequential && reason is not null)
        {
            error = "--reason explains a sequential run; it needs --sequential. A parallel run records its degree instead of a reason.";
            return false;
        }

        var execution = new HermeticOptions(
            sequential ? HermeticOptions.SequentialMode : HermeticOptions.ParallelMode,
            sequential ? null : degree ?? HermeticOptions.DefaultDegree,
            sequential ? reason : null,
            coverageRole ?? HermeticOptions.MeasureRole);
        command = new RunnerCommand(level, withoutPrinting, execution);
        return true;
    }

    /// <summary>
    /// Performs the one build that precedes the level's invocations: a bare MSBuild
    /// traversal of the level's projects, generated into the run directory, built
    /// once with <c>Restore;Build</c> so a tree without restore output builds too.
    /// The output goes to the run's build log and to the console; a failing build
    /// names the project its error lines point at.
    /// </summary>
    public static async Task<BuildRecord> BuildAsync(string root, string runDirectory, IReadOnlyList<string> projects)
    {
        var projectFile = Path.Combine(runDirectory, TraversalFileName);
        var log = Path.Combine(runDirectory, BuildLogFileName);
        await File.WriteAllTextAsync(projectFile, CreateTraversal(root, projects)).ConfigureAwait(false);

        var arguments = new List<string>
        {
            "msbuild",
            Path.GetRelativePath(root, projectFile),
            "-target:Build",
            // The same implicit restore `dotnet build` performs: the Restore target of
            // the traversal runs in its own pass, before anything is evaluated for the
            // build, so a tree without restore output builds from this one command.
            "-restore",
            "-property:Configuration=Release",
            "-nologo",
            "-verbosity:minimal",
        };
        var command = new List<string> { "dotnet" };
        command.AddRange(arguments);

        var line = $"> dotnet {string.Join(' ', arguments.Select(Program.Quote))}";
        Console.WriteLine(line);
        var stopwatch = Stopwatch.StartNew();
        var exitCode = -1;
        await using (var writer = new StreamWriter(log, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)) { NewLine = "\n" })
        {
            await writer.WriteLineAsync(line).ConfigureAwait(false);
            exitCode = await Program.RunStreamingAsync(root, "dotnet", arguments, new Dictionary<string, string>(), writer).ConfigureAwait(false);
        }

        stopwatch.Stop();
        return new BuildRecord(
            command,
            Path.GetRelativePath(root, log),
            exitCode,
            Math.Round(stopwatch.Elapsed.TotalSeconds, 1),
            exitCode == 0 ? [] : ReadFailedProjects(root, log));
    }

    /// <summary>
    /// Runs the level's projects against the build that preceded them. Each
    /// project appears once in the work set, so two invocations of the same
    /// project are never concurrent; the result is written at the project's own
    /// index, so the record follows the level's order whatever order the
    /// invocations complete in. A failing project never cancels its siblings:
    /// the level's exit code is decided after every invocation has ended.
    /// </summary>
    public static async Task<IReadOnlyList<ProjectRun>> RunAsync(
        IReadOnlyList<string> projects,
        HermeticOptions options,
        Func<string, Task<ProjectRun>> runProject)
    {
        var results = new ProjectRun[projects.Count];
        var degree = string.Equals(options.Mode, HermeticOptions.SequentialMode, StringComparison.Ordinal)
            ? 1
            : options.Degree ?? HermeticOptions.DefaultDegree;

        await Parallel.ForEachAsync(
            Enumerable.Range(0, projects.Count),
            new ParallelOptions { MaxDegreeOfParallelism = degree },
            async (index, _) =>
            {
                var project = projects[index];
                var name = Path.GetFileNameWithoutExtension(project);
                Console.WriteLine($"> start {name} ({index + 1}/{projects.Count})");
                var run = await runProject(project).ConfigureAwait(false);
                results[index] = run;
                var duration = run.DurationSeconds is { } seconds
                    ? " in " + seconds.ToString("0.0", CultureInfo.InvariantCulture) + " s"
                    : string.Empty;
                Console.WriteLine($"< {name} exit {run.ExitCode}{duration}");
            }).ConfigureAwait(false);

        return results;
    }

    /// <summary>
    /// Prints the level's result once, in its fixed project order: one line per
    /// project with the counts its log carries, or its exit code when the log has
    /// no summary block. The console of a parallel run interleaves and is not the
    /// evidence; this summary and the record are.
    /// </summary>
    public static void PrintSummary(string root, IReadOnlyList<ProjectRun> runs)
    {
        Console.WriteLine("Projects, in the level's order:");
        foreach (var run in runs)
        {
            var summary = ReadSummary(Path.Combine(root, run.Log));
            var duration = run.DurationSeconds is { } seconds
                ? " in " + seconds.ToString("0.0", CultureInfo.InvariantCulture) + " s"
                : string.Empty;
            Console.WriteLine(summary is null
                ? $"  {run.Name}: exit code {run.ExitCode}{duration}"
                : $"  {run.Name}: {summary}{duration}");
        }
    }

    /// <summary>
    /// The projects a failing build names, read from the error lines of its log:
    /// the project MSBuild prints beside the error when it prints one, otherwise
    /// the nearest project file above the file the error points at. Distinct, in
    /// name order, relative to the repository root.
    /// </summary>
    public static IReadOnlyList<string> ReadFailedProjects(string root, string log)
    {
        var projects = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(log))
        {
            return [];
        }

        foreach (var line in File.ReadLines(log))
        {
            if (!ErrorLine().IsMatch(line))
            {
                continue;
            }

            var bracketed = BracketedProject().Match(line);
            if (bracketed.Success && IsUnderRoot(root, bracketed.Groups["project"].Value))
            {
                projects.Add(Path.GetRelativePath(root, bracketed.Groups["project"].Value));
                continue;
            }

            foreach (Match path in ErrorPath().Matches(line))
            {
                var named = Path.IsPathRooted(path.Groups["path"].Value)
                    ? path.Groups["path"].Value
                    : Path.Combine(root, path.Groups["path"].Value);
                if (!IsUnderRoot(root, named))
                {
                    continue;
                }

                var project = named.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
                    ? named
                    : NearestProject(root, named);
                if (project is not null)
                {
                    projects.Add(Path.GetRelativePath(root, project));
                }
            }
        }

        return [.. projects];
    }

    private static bool TryReadValue(IReadOnlyList<string> args, ref int index, string option, out string value, out string? error)
    {
        if (index + 1 >= args.Count)
        {
            value = string.Empty;
            error = $"{option} needs a value.";
            return false;
        }

        index++;
        value = args[index];
        error = null;
        return true;
    }

    private static string CreateTraversal(string root, IReadOnlyList<string> projects)
    {
        var items = new XElement("ItemGroup");
        foreach (var project in projects)
        {
            items.Add(new XElement("ProjectToBuild", new XAttribute("Include", Path.GetFullPath(Path.Combine(root, project)))));
        }

        // Restore and Build are two targets of this file, so that MSBuild's -restore
        // switch restores in its own pass and the projects are evaluated again, with
        // the restore output in place, before they are built. Reaching both targets in
        // one pass would compile a tree whose restore output did not exist yet — the
        // package imports are read when a project is evaluated, not when it is built.
        var document = new XDocument(
            new XComment("Generated by the test runner for one hermetic run, from the level's fixed project list. It is not part of the repository."),
            new XElement(
                "Project",
                new XComment("A bare traversal: no SDK attribute, so the repository's own build settings do not apply to this file."),
                items,
                new XElement(
                    "Target",
                    new XAttribute("Name", "Restore"),
                    new XElement(
                        "MSBuild",
                        new XAttribute("Projects", "@(ProjectToBuild)"),
                        new XAttribute("Targets", "Restore"),
                        new XAttribute("BuildInParallel", "false"))),
                new XElement(
                    "Target",
                    new XAttribute("Name", "Build"),
                    new XElement(
                        "MSBuild",
                        new XAttribute("Projects", "@(ProjectToBuild)"),
                        new XAttribute("Targets", "Build"),
                        new XAttribute("BuildInParallel", "false")))));

        return document.ToString() + "\n";
    }

    private static string? NearestProject(string root, string file)
    {
        var fullRoot = Path.GetFullPath(root);
        var directory = Path.GetDirectoryName(Path.GetFullPath(file));
        while (directory is not null)
        {
            string[] projects;
            try
            {
                projects = Directory.GetFiles(directory, "*.csproj", SearchOption.TopDirectoryOnly);
            }
            catch (IOException)
            {
                return null;
            }

            if (projects.Length > 0)
            {
                return projects.Order(StringComparer.Ordinal).First();
            }

            if (string.Equals(directory, fullRoot, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            directory = Path.GetDirectoryName(directory);
        }

        return null;
    }

    private static bool IsUnderRoot(string root, string path)
    {
        var fullRoot = Path.GetFullPath(root);
        var fullPath = Path.GetFullPath(path);
        return fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static string? ReadSummary(string log)
    {
        if (!File.Exists(log))
        {
            return null;
        }

        string? verdict = null;
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        var collecting = false;
        foreach (var line in File.ReadLines(log))
        {
            if (line.StartsWith("Test run summary", StringComparison.Ordinal))
            {
                verdict = line.Trim();
                fields.Clear();
                collecting = true;
                continue;
            }

            if (!collecting)
            {
                continue;
            }

            if (line.Length == 0 || line[0] != ' ')
            {
                collecting = false;
                continue;
            }

            var parts = line.Trim().Split(':', 2);
            if (parts.Length == 2 && parts[0] is "total" or "succeeded" or "failed" or "skipped" or "duration")
            {
                fields[parts[0]] = parts[1].Trim();
            }
        }

        if (verdict is null)
        {
            return null;
        }

        var counts = fields.Count == 0
            ? "no summary block in its log"
            : $"total {fields.GetValueOrDefault("total", "?")}, succeeded {fields.GetValueOrDefault("succeeded", "?")}, " +
              $"failed {fields.GetValueOrDefault("failed", "?")}, skipped {fields.GetValueOrDefault("skipped", "?")}, " +
              $"duration {fields.GetValueOrDefault("duration", "?")}";
        return $"{verdict} ({counts})";
    }

    [GeneratedRegex(@":\s*error\s+[A-Za-z]+\d+", RegexOptions.CultureInvariant)]
    private static partial Regex ErrorLine();

    [GeneratedRegex(@"\[(?<project>[^\[\]\r\n]+\.csproj)\]", RegexOptions.CultureInvariant)]
    private static partial Regex BracketedProject();

    [GeneratedRegex(@"(?<path>(?:[A-Za-z]:[\\/]|/)[^()\[\]:\r\n]*?\.(?:csproj|cs|props|targets))(?=\(\d+,\d+\)|\s*:)", RegexOptions.CultureInvariant)]
    private static partial Regex ErrorPath();
}
