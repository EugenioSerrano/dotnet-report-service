using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using PaginationFixtures;
using PreSplitLayoutSpike;

// Group-continuity experiment: lays fixtures out in grid markup with the
// pagination library of spikes/02-pdf-layout after rows taller than a page are
// split, in the page, into continuation parts (the grid path of
// spikes/10-pagedjs-table-strict-accounting, whose runner and driver are linked
// unchanged). One fresh process per run. Commands:
//   versions                                                    Playwright, Chromium and .NET versions
//   run --matrix quick --fixtures DIR --out DIR [--resume]      the quick loop (contract test)
//   run --stage 1 --fixtures DIR --out DIR --budget-seconds S [--resume]
//                                                               the measured stage: 64 runs
//   run-one ... (internal: one run in a fresh process, started by "run")
// Exit codes: run 0 when every planned run has a record (whatever its outcome), 1 when a fixture file does not
// match its SHA256SUMS line, 2 invalid arguments.

const string FontFamily = "Liberation Sans";

if (args.Length == 0)
{
    return Usage();
}

var options = new Dictionary<string, string>(StringComparer.Ordinal);
var resume = false;
for (var i = 1; i < args.Length; i++)
{
    if (args[i] == "--resume")
    {
        resume = true;
        continue;
    }

    if (!args[i].StartsWith("--", StringComparison.Ordinal) || i + 1 >= args.Length)
    {
        return Usage();
    }

    options[args[i][2..]] = args[++i];
}

bool Has(params string[] names) => names.All(options.ContainsKey);

double? Number(string name) => options.TryGetValue(name, out var text)
    && double.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value) && value > 0 ? value : null;

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cancellation.Cancel();
};

switch (args[0])
{
    case "versions":
        return await PreSplitRunner.VersionsAsync();

    case "run" when Has("fixtures", "out") && (options.GetValueOrDefault("matrix") == "quick" || (options.GetValueOrDefault("stage") == "1" && Number("budget-seconds") is not null)):
        {
            var fixtures = options["fixtures"];
            var outDirectory = options["out"];
            string matrix;
            List<PlannedRun> plan;
            var budget = TimeSpan.FromSeconds(3600);
            if (options.GetValueOrDefault("matrix") == "quick")
            {
                matrix = "quick";
                plan = Plans.Quick();
            }
            else
            {
                matrix = "stage1-grid";
                plan = Plans.Stage1();
                var runsPath = Path.Combine(outDirectory, "runs.jsonl");
                var used = resume ? MatrixDriver.RecordedWallSeconds(runsPath) : 0;
                budget = TimeSpan.FromSeconds(Math.Max(0, Number("budget-seconds")!.Value - used));
            }

            var startedAt = DateTimeOffset.Now;
            var sumsPath = Path.Combine(fixtures, FixtureFile.SumsFileName);
            var sums = Sums.Read(sumsPath);
            foreach (var id in plan.Select(p => p.FixtureId).Distinct())
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
            Console.WriteLine($"Fixtures verified against {sumsPath}. Matrix '{matrix}', {plan.Count} runs, {Environment.ProcessorCount} CPUs, memory.max {ReadCgroup("memory.max") ?? "n/a"}, budget {budget.TotalSeconds:0} s.");
            var driver = new MatrixDriver(fixtures, outDirectory);
            var runs = await driver.ExecuteAsync(matrix, plan, resume, budget);
            var endedAt = DateTimeOffset.Now;
            var elapsed = resume ? MatrixDriver.RecordedWallSeconds(driver.RunsPath) : (endedAt - startedAt).TotalSeconds;
            var summary = new JsonObject
            {
                ["matrix"] = matrix,
                ["startedAt"] = startedAt.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture),
                ["endedAt"] = endedAt.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture),
                ["elapsedSeconds"] = Math.Round(elapsed, 1),
                ["elapsedNote"] = resume ? "resumed: the sum of the recorded runs' wall clock" : "driver wall clock of this stage",
                ["budgetSeconds"] = budget.TotalSeconds,
                ["fixtureSumsSha256"] = Sums.HashFile(sumsPath),
                ["environment"] = new JsonObject
                {
                    ["pdfBaseDigest"] = Environment.GetEnvironmentVariable("PDF_BASE_DIGEST"),
                    ["chromium"] = runs.FirstOrDefault(r => r.ChromiumVersion is not null)?.ChromiumVersion,
                    ["playwright"] = PreSplitRunner.PlaywrightVersion,
                    ["dotnet"] = Environment.Version.ToString(),
                    ["processors"] = Environment.ProcessorCount,
                    ["memoryMax"] = ReadCgroup("memory.max"),
                    ["memorySwapMax"] = ReadCgroup("memory.swap.max"),
                    ["memoryTotalKiB"] = PdfLayoutSpike.ProcessTreeSampler.MemoryTotalKiB(),
                },
                ["counts"] = JsonSerializer.SerializeToNode(runs.GroupBy(r => r.Outcome).ToDictionary(g => g.Key, g => g.Count())),
            };
            await File.WriteAllTextAsync(Path.Combine(outDirectory, "summary.json"), summary.ToJsonString(RunRecord.JsonIndented) + "\n");
            Console.WriteLine($"{runs.Count} runs recorded: " + string.Join(", ", runs.GroupBy(r => r.Outcome).Select(g => $"{g.Count()} {g.Key}")) + $"; {elapsed:0.0} s. Results in {outDirectory}.");
            return 0;
        }

    case "run-one" when Has("fixtures", "out", "plan"):
        {
            var planned = JsonSerializer.Deserialize<PlannedRun>(File.ReadAllText(options["plan"]), RunRecord.Json)!;
            var run = MatrixDriver.FromPlan(planned);
            var recordPath = Path.Combine(options["out"], "records", run.RunId + ".json");
            try
            {
                var fixture = FixtureFile.Read(FixtureFile.PathOf(options["fixtures"], run.FixtureId));
                var assets = Path.Combine(AppContext.BaseDirectory, "assets");
                var layoutCss = File.ReadAllText(Path.Combine(assets, "layout.css"));
                Func<Fixture, bool, (string Semantic, string Pdf)> build = new PageBuilder(layoutCss, File.ReadAllText(Path.Combine(assets, "fixture.css")), FontFamily).Build;
                run = await PreSplitRunner.ExecuteAsync(run, fixture, build, options["out"], MatrixDriver.RunCap, cancellation.Token);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                run.Outcome = "error";
                run.Reason = $"{e.GetType().Name}: {e.Message.Split('\n')[0]}";
            }

            File.WriteAllText(recordPath, JsonSerializer.Serialize(run, RunRecord.Json));

            // Exit at once: a timed-out layout leaves the browser busy; the driver kills what remains.
            Environment.Exit(run.Outcome == "ok" ? 0 : 3);
            return 0;
        }

    default:
        return Usage();
}

static string? ReadCgroup(string name)
{
    try
    {
        return File.ReadAllText(Path.Combine("/sys/fs/cgroup", name)).Trim();
    }
    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
    {
        return null;
    }
}

static int Usage()
{
    Console.Error.WriteLine("Usage: versions | run --matrix quick --fixtures DIR --out DIR [--resume] | run --stage 1 --fixtures DIR --out DIR --budget-seconds S [--resume]");
    return 2;
}
