using System.Globalization;
using System.Text.Json;
using NativeLayoutSpike;
using PaginationFixtures;

// Native layout experiment: prints fixtures with Chromium's own print layout (no
// pagination script) and measures memory and time, one fresh process per run.
// Commands:
//   versions                                                  prints Playwright, Chromium and .NET versions
//   run --fixtures DIR --out DIR --matrix full|quick [--resume]   the matrix, in series; writes runs.jsonl,
//                                                             plan.json, summary.json, runs.csv and pdf/
//   run-one ... (internal: one run in a fresh process, started by "run")
// Exit codes: 0 the matrix ran to its end (every run has a record, whatever its outcome);
// 1 a fixture file does not match its SHA256SUMS line; 2 invalid arguments.
// Text integrity is judged by the separate checker, never here.

const string FontFamily = "Liberation Sans";

if (args.Length == 0)
{
    return Usage();
}

var options = new Dictionary<string, string>(StringComparer.Ordinal);
var flags = new HashSet<string>(StringComparer.Ordinal);
for (var i = 1; i < args.Length; i++)
{
    if (!args[i].StartsWith("--", StringComparison.Ordinal))
    {
        return Usage();
    }

    if (args[i] == "--resume")
    {
        flags.Add("resume");
        continue;
    }

    if (i + 1 >= args.Length)
    {
        return Usage();
    }

    options[args[i][2..]] = args[++i];
}

switch (args[0])
{
    case "versions":
        return await LayoutRun.VersionsAsync();

    case "run" when options.ContainsKey("fixtures") && options.ContainsKey("out") && options.GetValueOrDefault("matrix") is "full" or "quick":
        {
            var fixtures = options["fixtures"];
            var outDirectory = options["out"];
            var matrix = options["matrix"];
            var startedAt = DateTimeOffset.Now;
            var sumsPath = Path.Combine(fixtures, FixtureFile.SumsFileName);
            var sums = Sums.Read(sumsPath);
            foreach (var id in MatrixDriver.Plan(matrix).Select(p => p.FixtureId).Distinct())
            {
                try
                {
                    _ = FixtureFile.ReadVerified(fixtures, id, sums);
                }
                catch (InvalidDataException e)
                {
                    Console.Error.WriteLine(e.Message);
                    return 1;
                }
            }

            Directory.CreateDirectory(outDirectory);
            Console.WriteLine($"Fixtures verified against {sumsPath}. Matrix '{matrix}', {Environment.ProcessorCount} CPUs, memory.max {ResultsWriter.ReadCgroup("memory.max") ?? "n/a"}.");
            var driver = new MatrixDriver(fixtures, outDirectory);
            var runs = await driver.ExecuteAsync(matrix, flags.Contains("resume"));
            await ResultsWriter.WriteAsync(outDirectory, matrix, startedAt, Sums.HashFile(sumsPath), runs);
            Console.WriteLine($"{runs.Count} runs recorded: " + string.Join(", ", runs.GroupBy(r => r.Outcome).Select(g => $"{g.Count()} {g.Key}")) + $". Results in {outDirectory}.");
            return 0;
        }

    case "run-one" when new[] { "fixtures", "out", "run-id", "fixture", "markup", "repetition", "group", "series", "mutation" }.All(options.ContainsKey):
        {
            var run = new RunRecord
            {
                RunId = options["run-id"],
                FixtureId = options["fixture"],
                Markup = options["markup"],
                Repetition = int.Parse(options["repetition"], CultureInfo.InvariantCulture),
                Group = int.Parse(options["group"], CultureInfo.InvariantCulture),
                Series = options["series"] == "-" ? null : options["series"],
                Mutation = options["mutation"] == "-" ? null : options["mutation"],
            };
            var recordPath = Path.Combine(options["out"], "records", run.RunId + ".json");
            try
            {
                var fixture = FixtureFile.Read(FixtureFile.PathOf(options["fixtures"], run.FixtureId));
                var css = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "assets", "native.css"));
                run = await LayoutRun.ExecuteAsync(run, fixture, new HtmlBuilder(css, FontFamily), options["out"], MatrixDriver.RunCap);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                run.Outcome = "error";
                run.Reason = $"{e.GetType().Name}: {e.Message.Split('\n')[0]}";
            }

            File.WriteAllText(recordPath, JsonSerializer.Serialize(run, RunRecord.Json));

            // Exit at once: a timed-out print leaves the browser busy; the driver kills what remains.
            Environment.Exit(run.Outcome == "ok" ? 0 : 3);
            return 0;
        }

    default:
        return Usage();
}

static int Usage()
{
    Console.Error.WriteLine("Usage: versions | run --fixtures DIR --out DIR --matrix full|quick [--resume]");
    return 2;
}
