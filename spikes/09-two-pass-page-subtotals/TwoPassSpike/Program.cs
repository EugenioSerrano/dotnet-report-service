using System.Diagnostics;
using System.Text.Json;
using PaginationFixtures;
using TwoPassSpike;

// Two-pass experiment: pass 1 prints a fixture with Chromium's own print layout
// and invisible row markers; pass 2 reads the markers back from the PDF, computes
// the page subtotals and the brought/carried-forward chain from the block's source
// data, stamps them into the reserved bottom margin, removes the markers and saves.
// Commands:
//   versions                                                  Playwright, Chromium, .NET and PDF library versions
//   run --fixtures DIR --out DIR --matrix full|quick [--resume] [--font FILE] [--stamped-markups table,grid]
//                                                             the matrix, in series; writes plan.json, runs.jsonl,
//                                                             twopass.jsonl, summary.json, runs.csv, pdf/, readback/, stamp/
//   probe markers|stamp|split-rows|stamped-ab --fixtures DIR --out DIR [--font FILE]  the marker probe (B100 and T300u, both markups, "cell",
//                                                             pass 1 and a read-only reading) or the stamp probe
//                                                             (B100 table, both strategies, both passes)
//   layout-one ... / stamp-one ... (internal: one pass in a fresh process, started by "run")
// Exit codes: 0 the matrix ran to its end (every run has a record, whatever its outcome);
// 1 a fixture file does not match its SHA256SUMS line; 2 invalid arguments.
// Accounting and text are judged by the separate checkers, never here.

const string FontFamily = "Liberation Sans";

if (args.Length == 0)
{
    return Usage();
}

var command = args[0];
var first = 1;
if (command == "probe")
{
    if (args.Length < 2 || args[1] is not ("markers" or "stamp" or "split-rows" or "stamped-ab"))
    {
        return Usage();
    }

    command = "probe-" + args[1];
    first = 2;
}

var options = new Dictionary<string, string>(StringComparer.Ordinal);
var flags = new HashSet<string>(StringComparer.Ordinal);
for (var i = first; i < args.Length; i++)
{
    if (!args[i].StartsWith("--", StringComparison.Ordinal))
    {
        return Usage();
    }

    if (args[i] is "--resume" or "--read-only")
    {
        flags.Add(args[i][2..]);
        continue;
    }

    if (i + 1 >= args.Length)
    {
        return Usage();
    }

    options[args[i][2..]] = args[++i];
}

bool Has(params string[] names) => names.All(options.ContainsKey);

string? Dash(string name) => options[name] == "-" ? null : options[name];

switch (command)
{
    case "versions":
        return await LayoutRun.VersionsAsync();

    case "run" when Has("fixtures", "out") && options.GetValueOrDefault("matrix") is MatrixDriver.Full or MatrixDriver.Quick:
        return await RunMatrixAsync(options["matrix"], flags.Contains("resume"));

    case "probe-markers" when Has("fixtures", "out"):
        return await RunMatrixAsync(MatrixDriver.ProbeMarkers, resume: false);

    case "probe-stamp" when Has("fixtures", "out"):
        return await RunMatrixAsync(MatrixDriver.ProbeStamp, resume: false);

    case "probe-split-rows" when Has("fixtures", "out"):
        return await RunMatrixAsync(MatrixDriver.ProbeSplitRows, resume: false);

    case "probe-stamped-ab" when Has("fixtures", "out"):
        return await RunMatrixAsync(MatrixDriver.ProbeStampedAb, resume: false);

    case "plan" when Has("out") && options.GetValueOrDefault("matrix") is MatrixDriver.Full or MatrixDriver.Quick:
        {
            // Writes the arranged plan only (nothing runs): the entries, the replaced runs and the counts.
            var stamped = options.GetValueOrDefault("stamped-markups", "table,grid").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct().ToList();
            var (runs, replaced) = MatrixDriver.Arrange(options["matrix"], stamped);
            Directory.CreateDirectory(options["out"]);
            File.WriteAllText(Path.Combine(options["out"], "plan-dry.json"), JsonSerializer.Serialize(new { matrix = options["matrix"], stampedMarkups = stamped, runs, replaced }, new JsonSerializerOptions(RunRecord.Json) { WriteIndented = true }) + "\n");
            var run = runs.Where(r => r.NotRunReason is null).ToList();
            Console.WriteLine($"plan entries {runs.Count}, run {run.Count}, planned not-run {runs.Count - run.Count}, replaced {replaced.Count}, records {runs.Count + replaced.Count}, not-run records {runs.Count - run.Count + replaced.Count}");
            foreach (var g in run.GroupBy(r => $"{r.Markup} {r.Strategy}").OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                Console.WriteLine($"  {g.Key}: {g.Count()}");
            }

            return 0;
        }

    case "layout-one" when Has("fixtures", "out", "run", "fixture", "markup", "strategy", "mutation"):
        {
            var recordPath = Path.Combine(options["out"], "records", options["run"] + ".p1.json");
            PassRecord record;
            try
            {
                var fixture = FixtureFile.Read(FixtureFile.PathOf(options["fixtures"], options["fixture"]));
                var css = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "assets", "native.css"));
                record = await LayoutRun.ExecuteAsync(
                    fixture, options["markup"], options["strategy"], Dash("mutation"), new HtmlBuilder(css, FontFamily), options["out"],
                    Path.Combine("pdf", options["run"] + ".p1.pdf"), MatrixDriver.PassCap);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                record = new PassRecord { Outcome = "error", Reason = $"{e.GetType().Name}: {e.Message.Split('\n')[0]}" };
            }

            File.WriteAllText(recordPath, JsonSerializer.Serialize(record, RunRecord.Json));

            // Exit at once: a timed-out print leaves the browser busy; the driver kills what remains.
            Environment.Exit(record.Outcome == "ok" ? 0 : 3);
            return 0;
        }

    case "stamp-one" when Has("fixtures", "out", "run", "fixture", "strategy", "fault", "font"):
        {
            var recordPath = Path.Combine(options["out"], "records", options["run"] + ".p2.json");
            PassRecord record;
            try
            {
                var fixture = FixtureFile.Read(FixtureFile.PathOf(options["fixtures"], options["fixture"]));
                record = await StampRun.ExecuteAsync(
                    fixture, options["strategy"], Dash("fault"), options["out"], Path.Combine("pdf", options["run"] + ".p1.pdf"), options["run"],
                    options["font"], flags.Contains("read-only"), MatrixDriver.PassCap);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                record = new PassRecord { Outcome = "error", Reason = $"{e.GetType().Name}: {e.Message.Split('\n')[0]}" };
            }

            File.WriteAllText(recordPath, JsonSerializer.Serialize(record, RunRecord.Json));
            Environment.Exit(record.Outcome == "ok" ? 0 : 3);
            return 0;
        }

    default:
        return Usage();
}

async Task<int> RunMatrixAsync(string matrix, bool resume)
{
    var fixtures = options["fixtures"];
    var outDirectory = options["out"];
    var startedAt = DateTimeOffset.Now;
    var sumsPath = Path.Combine(fixtures, FixtureFile.SumsFileName);
    var sums = Sums.Read(sumsPath);
    foreach (var id in MatrixDriver.Plan(matrix).Select(p => p.FixtureId).Distinct())
    {
        try
        {
            _ = FixtureFile.ReadVerified(fixtures, id, sums);
        }
        catch (Exception e) when (e is InvalidDataException or IOException)
        {
            Console.Error.WriteLine(e.Message);
            return 1;
        }
    }

    var stamped = options.GetValueOrDefault("stamped-markups", "table,grid").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct().ToList();
    if (stamped.Count == 0 || stamped.Any(m => m is not (HtmlBuilder.Table or HtmlBuilder.Grid)))
    {
        Console.Error.WriteLine("--stamped-markups must name table, grid or both; with no markup for strategy \"stamped\" the run stops before the matrix.");
        return 2;
    }

    var font = options.GetValueOrDefault("font") ?? await FontFileAsync();
    Directory.CreateDirectory(outDirectory);
    Console.WriteLine($"Fixtures verified against {sumsPath}. Matrix '{matrix}', {Environment.ProcessorCount} CPUs, memory.max {ResultsWriter.ReadCgroup("memory.max") ?? "n/a"}, stamp font {font}, stamped in {string.Join('+', stamped)}.");
    var driver = new MatrixDriver(fixtures, outDirectory, font, stamped);
    var runs = await driver.ExecuteAsync(matrix, resume);
    await ResultsWriter.WriteAsync(outDirectory, matrix, startedAt, Sums.HashFile(sumsPath), font, runs);
    Console.WriteLine($"{runs.Count} runs recorded. Pass 1: " + string.Join(", ", runs.GroupBy(r => r.Pass1.Outcome).Select(g => $"{g.Count()} {g.Key}"))
        + ". Pass 2: " + string.Join(", ", runs.GroupBy(r => r.Pass2.Outcome).Select(g => $"{g.Count()} {g.Key}")) + $". Results in {outDirectory}.");
    return 0;
}

// The regular Liberation Sans file as fontconfig resolves it in the image (the face the first pass prints with).
static async Task<string> FontFileAsync()
{
    try
    {
        var info = new ProcessStartInfo("fc-match") { RedirectStandardOutput = true, UseShellExecute = false };
        info.ArgumentList.Add("--format=%{file}");
        info.ArgumentList.Add("Liberation Sans:style=Regular");
        using var process = Process.Start(info)!;
        var output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        return process.ExitCode == 0 && File.Exists(output.Trim()) ? output.Trim() : LiberationFontResolver.DefaultFile;
    }
    catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or TimeoutException)
    {
        return LiberationFontResolver.DefaultFile;
    }
}

static int Usage()
{
    Console.Error.WriteLine("Usage: versions | run --fixtures DIR --out DIR --matrix full|quick [--resume] [--font FILE] [--stamped-markups table,grid] | probe markers|stamp|split-rows|stamped-ab --fixtures DIR --out DIR [--font FILE] | plan --matrix full|quick --out DIR [--stamped-markups LIST]");
    return 2;
}
