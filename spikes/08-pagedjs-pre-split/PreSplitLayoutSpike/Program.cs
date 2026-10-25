using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using PaginationFixtures;
using PreSplitLayoutSpike;

// Pre-split layout experiment: lays fixtures out with the pagination library of
// spikes/02-pdf-layout after rows taller than a page are split, in the page,
// into continuation parts that each fit one page. One fresh process per run.
// Commands:
//   versions                                                    Playwright, Chromium and .NET versions
//   run --matrix quick --fixtures DIR --out DIR [--resume]      the quick loop (contract smoke test), defaults
//   run --stage 1 --fixtures DIR --out DIR [--resume]           controls, the ordinary block, sweeps A and B
//   run --stage 2 --fixtures DIR --out DIR --trigger T --part-limit F --budget-seconds S --selection FILE [--resume]
//                                                               everything else, with the selected values only
//   select --stage1 DIR --integrity FILE --out FILE             the trigger and part limit, by fixed rules
//   verdict --matrix quick|full --runs DIR[,DIR] --integrity FILE[,FILE] --controls FILE [--selection FILE]
//           --identity FILE --repo DIR --fixtures DIR --out DIR
//   controls-base --matrix quick|full --runs DIR[,DIR] --integrity FILE[,FILE]   the runs folder and run id of the controls base
//   run-one ... (internal: one run in a fresh process, started by "run")
// Exit codes: run 0 when every planned run has a record (whatever its outcome), 1 when a fixture file does not
// match its SHA256SUMS line, 2 invalid arguments (stage 2 also when its values differ from selection.json);
// verdict 0 when the instrument is healthy, 1 otherwise with every reason printed.

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

    case "run" when Has("fixtures", "out") && (options.GetValueOrDefault("matrix") == "quick" || options.GetValueOrDefault("stage") is "1" or "2"):
        {
            var fixtures = options["fixtures"];
            var outDirectory = options["out"];
            string matrix;
            List<PlannedRun> plan;
            var budget = TimeSpan.FromSeconds(SelectCommand.TotalBudgetSeconds);
            if (options.GetValueOrDefault("matrix") == "quick")
            {
                matrix = "quick";
                plan = Plans.Quick();
            }
            else if (options["stage"] == "1")
            {
                matrix = "stage1";
                plan = Plans.Stage1();
            }
            else
            {
                if (Number("trigger") is not { } t || Number("part-limit") is not { } f || Number("budget-seconds") is not { } s || !Has("selection"))
                {
                    return Usage();
                }

                if (!SelectCommand.Matches(options["selection"], t, f, out var detail))
                {
                    Console.Error.WriteLine("Stage 2 refuses to start: its values must be those of the selection. " + detail);
                    return 2;
                }

                Console.WriteLine("Stage 2 values match the selection: " + detail);
                matrix = "stage2";
                plan = Plans.Stage2(t, f);
                var runsPath = Path.Combine(outDirectory, "runs.jsonl");
                var used = resume ? MatrixDriver.RecordedWallSeconds(runsPath) : 0;
                budget = TimeSpan.FromSeconds(Math.Max(0, s - used));
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

            if (matrix == "stage1" && resume && File.Exists(Path.Combine(outDirectory, "selection.json")))
            {
                Console.Error.WriteLine("Stage 1 cannot be resumed once a selection exists: re-running stage 1 voids the selection; remove it and run check and select again.");
                return 2;
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

    case "select" when Has("stage1", "integrity", "out"):
        return await SelectCommand.RunAsync(options["stage1"], options["integrity"], options["out"]);

    case "verdict" when Has("matrix", "runs", "integrity", "controls", "identity", "repo", "fixtures", "out") && options["matrix"] is "quick" or "full":
        return await VerdictCommand.RunAsync(
            options["matrix"],
            options["runs"].Split(',', StringSplitOptions.RemoveEmptyEntries),
            options["integrity"].Split(',', StringSplitOptions.RemoveEmptyEntries),
            options["controls"],
            options.GetValueOrDefault("selection"),
            options["identity"],
            options["repo"],
            options["fixtures"],
            options["out"]);

    case "controls-base" when Has("matrix", "runs", "integrity") && options["matrix"] is "quick" or "full":
        {
            // Prints the folder and the run id the extraction-level controls must use, by the fixed base rule.
            var loaded = Evidence.LoadStages(
                options["runs"].Split(',', StringSplitOptions.RemoveEmptyEntries),
                options["integrity"].Split(',', StringSplitOptions.RemoveEmptyEntries));
            var chosen = VerdictCommand.ControlsBase(new Evidence(loaded.Records, loaded.Integrity), options["matrix"] == "full");
            if (chosen is null)
            {
                Console.WriteLine("none");
                return 1;
            }

            Console.WriteLine($"{loaded.DirectoryOf[chosen.RunId]} {Evidence.Unqualified(chosen.RunId)}");
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
                var builder = new PageBuilder(File.ReadAllText(Path.Combine(assets, "layout.css")), File.ReadAllText(Path.Combine(assets, "fixture.css")), FontFamily);
                run = await PreSplitRunner.ExecuteAsync(run, fixture, builder, options["out"], MatrixDriver.RunCap, cancellation.Token);
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
    Console.Error.WriteLine("Usage: versions | run --matrix quick|--stage 1|--stage 2 ... --fixtures DIR --out DIR [--resume] | select --stage1 DIR --integrity FILE --out FILE | "
        + "verdict --matrix quick|full --runs DIR[,DIR] --integrity FILE[,FILE] --controls FILE [--selection FILE] --identity FILE --repo DIR --fixtures DIR --out DIR");
    return 2;
}
