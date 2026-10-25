using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PreSplitLayoutSpike;

/// <summary>
/// The selection step between the two measured stages. A pure function of its
/// inputs — stage 1's runs.jsonl and summary.json and the checker's integrity
/// file of stage 1 — it applies the trigger rule (sweep A) and the part-limit
/// rule (sweep B) exactly, prints every input and the choice, and writes them
/// to selection.json and into the "selection" object of stage 1's summary.json.
/// Nobody chooses the values by hand.
/// </summary>
internal static class SelectCommand
{
    public const double TotalBudgetSeconds = 10_800;
    public const double FallbackTrigger = 0.50;
    public const double FallbackPartLimit = 0.50;
    private static readonly double[] Candidates = [0.90, 0.75, 0.50];

    public static async Task<int> RunAsync(string stage1Directory, string integrityPath, string outPath)
    {
        var runsPath = Path.Combine(stage1Directory, "runs.jsonl");
        var summaryPath = Path.Combine(stage1Directory, "summary.json");
        if (!File.Exists(runsPath) || !File.Exists(summaryPath) || !File.Exists(integrityPath))
        {
            Console.Error.WriteLine("Stage 1's runs.jsonl and summary.json, and its integrity file, are required.");
            return 1;
        }

        var evidence = new Evidence(Evidence.ReadRuns(stage1Directory), Evidence.ReadIntegrity(integrityPath));
        var summary = JsonNode.Parse(await File.ReadAllTextAsync(summaryPath))!.AsObject();
        var elapsed = summary["elapsedSeconds"]!.GetValue<double>();

        // Sweep A — the trigger.
        var sweepA = new JsonArray();
        var inputsA = new List<(string Fixture, double Fraction, bool Passes)>();
        Console.WriteLine("Sweep A (split disabled; measured row-50 height as a fraction of the content area):");
        foreach (var run in evidence.Records.Where(r => r.MatrixRow == 3))
        {
            var fraction = Evidence.Row50Fraction(run);
            var rules = Evidence.Rules1To5Pass(run);
            var ti = evidence.TiVerdict(run);
            var quiet = Evidence.NoLayoutRepeated(run);
            var passes = run.Outcome == "ok" && rules && ti == "pass" && quiet;
            var input = fraction is not null && fraction <= 1.00;
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  {run.FixtureId}: class {run.Outcome}, fraction {fraction?.ToString("0.0000", CultureInfo.InvariantCulture) ?? "n/a"}, rules 1-5 {(rules ? "pass" : "FAIL")}, TI {ti}, {Evidence.Signals(run)} "
                + $"=> {(passes ? "passes" : "fails")}{(input ? string.Empty : " (not an input: fraction above 1.00 or not measured)")}"));
            sweepA.Add(new JsonObject
            {
                ["runId"] = run.RunId,
                ["fixture"] = run.FixtureId,
                ["outcome"] = run.Outcome,
                ["fraction"] = fraction,
                ["rules1To5"] = rules,
                ["ti"] = ti,
                ["layoutRepeatedError"] = run.LayoutRepeatedError,
                ["layoutRepeatedAnyLevel"] = run.LayoutRepeatedAnyLevel,
                ["passes"] = passes,
                ["input"] = input,
            });
            if (input)
            {
                inputsA.Add((run.FixtureId, fraction!.Value, passes));
            }
        }

        string triggerCase;
        double trigger;
        var failing = inputsA.Where(i => !i.Passes).ToList();
        if (failing.Count == 0)
        {
            triggerCase = "a";
            trigger = 1.00;
        }
        else
        {
            var m = failing.Min(i => i.Fraction);
            var below = inputsA.Where(i => i.Passes && i.Fraction < m).ToList();
            if (below.Count > 0)
            {
                triggerCase = "b";
                trigger = below.Max(i => i.Fraction);
            }
            else
            {
                triggerCase = "c";
                trigger = FallbackTrigger;
            }
        }

        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Trigger: case ({triggerCase}) => {Plans.Format(trigger)}."));

        // Sweep B — the part limit.
        var allowed = Candidates.Where(c => c <= trigger + 1e-9).ToList();
        var sweepB = new JsonArray();
        Console.WriteLine("Sweep B (trigger 1.00; rules 1-6 and 8 per run, TI-1 ... TI-8, both console signals):");
        var passByLimit = new Dictionary<double, List<bool>>();
        foreach (var run in evidence.Records.Where(r => r.MatrixRow == 4))
        {
            var rules = Evidence.RulesPerRunPass(run);
            var ti = evidence.TiVerdict(run);
            var quiet = Evidence.NoLayoutRepeated(run);
            var passes = run.Outcome == "ok" && rules && ti == "pass" && quiet;
            var limit = run.PartLimit!.Value;
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  {run.FixtureId} f {limit:0.00}: class {run.Outcome}, split rows {Evidence.SplitRows(run)}, parts {run.SplitResult?["partsTotal"]}, max part fraction {MaxPartFraction(run)}, "
                + $"rules 1-6, 8 {(rules ? "pass" : "FAIL")}, TI {ti}, {Evidence.Signals(run)} => {(passes ? "passes" : "fails")}"));
            sweepB.Add(new JsonObject
            {
                ["runId"] = run.RunId,
                ["fixture"] = run.FixtureId,
                ["partLimit"] = limit,
                ["outcome"] = run.Outcome,
                ["rules"] = rules,
                ["ti"] = ti,
                ["layoutRepeatedError"] = run.LayoutRepeatedError,
                ["layoutRepeatedAnyLevel"] = run.LayoutRepeatedAnyLevel,
                ["passes"] = passes,
                ["selectable"] = Candidates.Contains(limit),
            });
            if (!passByLimit.TryGetValue(limit, out var list))
            {
                list = [];
                passByLimit[limit] = list;
            }

            list.Add(passes);
        }

        var passing = allowed.Where(c => passByLimit.TryGetValue(c, out var l) && l.Count == 2 && l.All(p => p)).ToList();
        var outcome = passing.Count > 0 ? "selected" : "fallback";
        var partLimit = passing.Count > 0 ? passing.Max() : FallbackPartLimit;
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"Part limit: candidates {string.Join(", ", allowed.Select(Plans.Format))}; passing {(passing.Count == 0 ? "none" : string.Join(", ", passing.Select(Plans.Format)))} => {Plans.Format(partLimit)} ({outcome})."));

        var remaining = Math.Max(0, TotalBudgetSeconds - elapsed);
        var selection = new JsonObject
        {
            ["trigger"] = Math.Round(trigger, 4),
            ["partLimit"] = partLimit,
            ["triggerCase"] = triggerCase,
            ["outcome"] = outcome,
            ["candidates"] = new JsonArray([.. allowed.Select(c => (JsonNode)JsonValue.Create(c))]),
            ["passingCandidates"] = new JsonArray([.. passing.Select(c => (JsonNode)JsonValue.Create(c))]),
            ["stage1ElapsedSeconds"] = elapsed,
            ["remainingBudgetSeconds"] = Math.Round(remaining, 1),
            ["sweepA"] = sweepA,
            ["sweepB"] = sweepB,
            ["inputs"] = new JsonObject
            {
                ["runsJsonlSha256"] = HashFile(runsPath),
                ["integritySha256"] = HashFile(integrityPath),
                ["stage1ElapsedSeconds"] = elapsed,
            },
        };
        await File.WriteAllTextAsync(outPath, selection.ToJsonString(RunRecord.JsonIndented) + "\n");
        summary["selection"] = selection.DeepClone();
        await File.WriteAllTextAsync(summaryPath, summary.ToJsonString(RunRecord.JsonIndented) + "\n");
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"Selection: trigger {Plans.Format(trigger)}, part limit {Plans.Format(partLimit)}, outcome {outcome}; stage 1 took {elapsed:0.0} s, remaining budget {remaining:0.0} s. Written to {outPath}."));
        return 0;
    }

    /// <summary>Whether stage 2's arguments are exactly the selected values; stage 2 refuses to start otherwise.</summary>
    public static bool Matches(string selectionPath, double trigger, double partLimit, out string detail)
    {
        if (!File.Exists(selectionPath))
        {
            detail = $"{selectionPath} does not exist";
            return false;
        }

        var node = JsonNode.Parse(File.ReadAllText(selectionPath))!;
        var t = node["trigger"]!.GetValue<double>();
        var f = node["partLimit"]!.GetValue<double>();
        detail = string.Create(CultureInfo.InvariantCulture, $"selection.json: trigger {Plans.Format(t)}, part limit {Plans.Format(f)}; arguments: trigger {Plans.Format(trigger)}, part limit {Plans.Format(partLimit)}");
        return Plans.Format(t) == Plans.Format(trigger) && Plans.Format(f) == Plans.Format(partLimit);
    }

    private static string MaxPartFraction(RunRecord run) =>
        run.SplitResult?["splitRows"]?.AsArray().Select(r => r?["maxPartFraction"]?.GetValue<double>() ?? 0).DefaultIfEmpty(0).Max().ToString("0.0000", CultureInfo.InvariantCulture) ?? "n/a";

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }
}
