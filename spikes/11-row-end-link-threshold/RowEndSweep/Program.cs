using System.Text.Json;
using RowEndSweep;

// Row-end link experiment: where native Chromium stops writing the link annotation
// that marks the end of a split row. A tall row (record 50 of an ordinary block) is
// printed at many sizes by the unchanged two-pass harness image, and its links are
// read back by the separate checkers, which hold no browser. Commands:
//   fixtures --dataset FILE --sums FILE --reference-sums FILE --out DIR [--only ID,...]
//                                         the sweep fixtures (after the self-check against the shared series)
//   plan     --round smoke|1|2 [--extra FILE]   prints the plan's counts
//   drive    --fixtures DIR --out DIR --round smoke|1|2 [--extra FILE]
//                                         runs a round, in series, inside the harness image
//   report   --rounds DIR[,DIR] --fixtures DIR --reference DIR --reference-split-rows FILE --out DIR [--propose FILE]
//                                         joins the records and both readings; writes summary.json and runs.csv
// Exit codes: 0 success; 1 a dataset or fixture hash mismatch or a failed self-check; 2 invalid arguments.

if (args.Length == 0)
{
    return Usage();
}

var options = new Dictionary<string, string>(StringComparer.Ordinal);
for (var i = 1; i < args.Length; i++)
{
    if (!args[i].StartsWith("--", StringComparison.Ordinal) || i + 1 >= args.Length)
    {
        return Usage();
    }

    options[args[i][2..]] = args[++i];
}

bool Has(params string[] names) => names.All(options.ContainsKey);

List<string>? Extra() => options.TryGetValue("extra", out var path) ? JsonSerializer.Deserialize<List<string>>(File.ReadAllText(path)) : null;

switch (args[0])
{
    case "fixtures" when Has("dataset", "sums", "reference-sums", "out"):
        return SweepFixtures.Run(options["dataset"], options["sums"], options["reference-sums"], options["out"],
            options.TryGetValue("only", out var only) ? only.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) : null);

    case "plan" when Has("round"):
        {
            var plan = SweepPlan.Build(options["round"], Extra());
            Console.WriteLine($"round {options["round"]}: {plan.Count} runs, {plan.Select(p => p.FixtureId).Distinct(StringComparer.Ordinal).Count()} fixtures");
            foreach (var g in plan.GroupBy(p => $"{p.Markup} {p.Content}").OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                Console.WriteLine($"  {g.Key}: {g.Count()}");
            }

            return 0;
        }

    case "drive" when Has("fixtures", "out", "round"):
        return await SweepDriver.RunAsync(options["fixtures"], options["out"], options["round"], Extra());

    case "report" when Has("rounds", "fixtures", "reference", "reference-split-rows", "out"):
        return SweepReport.Run(options);

    default:
        return Usage();
}

static int Usage()
{
    Console.Error.WriteLine("Usage: fixtures --dataset FILE --sums FILE --reference-sums FILE --out DIR [--only ID,...] | plan --round smoke|1|2 [--extra FILE] | "
        + "drive --fixtures DIR --out DIR --round smoke|1|2 [--extra FILE] | "
        + "report --rounds DIR[,DIR] --fixtures DIR --reference DIR --reference-split-rows FILE --out DIR [--propose FILE]");
    return 2;
}
