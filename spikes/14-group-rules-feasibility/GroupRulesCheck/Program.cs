using System.Globalization;
using GroupContinuityCheck;
using GroupRulesCheck;

// Check of the group-rules experiment. Reads the printed PDFs with poppler-utils
// only (never with the layout library, its page or its console). Commands:
//   group-fixtures --dataset FILE --sums FILE --reference DIR --tall FILE --out DIR   (the builder of spikes/12-group-continuity, unchanged)
//   evaluate  --fixtures DIR --runs DIR --out DIR
//             the reading (repeated-header lines set aside), the unchanged text rules, strict accounting
//             and group check on it, and the three behaviour observations, per planned run
//   controls  --fixtures DIR --runs DIR --evaluation FILE --integrity-raw FILE --extraction FILE --reference-groups FILE --out DIR
//   planted   --fixtures DIR --runs DIR --evaluation FILE --prefix q-|s1- --out DIR    RH-3 … RH-6, OX-4, OX-5 alone (development loop)
//   anchors   --reference-accounting FILE --reference-integrity FILE --evaluation FILE --out FILE
//   verdict   --runs DIR --evaluation FILE --integrity-raw FILE --controls FILE --anchors FILE --identity FILE[,FILE]
//             --definitions FILE,FILE --fixture-report FILE --reference-sums FILE --runner-diff FILE --summary FILE [--environment FILE]
// Every command takes [--timeout-seconds 300] where it reads PDFs. Exit codes: 0 success (verdict: the
// instrument is healthy); 1 failure; 2 invalid arguments.

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cancellation.Cancel();
};

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

int Timeout() => int.TryParse(options.GetValueOrDefault("timeout-seconds", "300"), NumberStyles.None, CultureInfo.InvariantCulture, out var t) && t > 0 ? t : -1;

List<string> FileList(string name) => [.. options[name].Split(',', StringSplitOptions.RemoveEmptyEntries)];

try
{
    return args[0] switch
    {
        "group-fixtures" when Has("dataset", "sums", "reference", "tall", "out") =>
            GroupFixtures.Run(options["dataset"], options["sums"], options["reference"], options["tall"], options["out"]),
        "evaluate" when Has("fixtures", "runs", "out") && Timeout() > 0 =>
            await EvaluateCommand.RunAsync(options["fixtures"], options["runs"], options["out"], Timeout(), cancellation.Token),
        "controls" when Has("fixtures", "runs", "evaluation", "integrity-raw", "extraction", "reference-groups", "out") && Timeout() > 0 =>
            await Controls.RunAsync(options["fixtures"], options["runs"], options["evaluation"], options["integrity-raw"], options["extraction"],
                options["reference-groups"], options["out"], Timeout(), cancellation.Token),
        "planted" when Has("fixtures", "runs", "evaluation", "prefix", "out") && Timeout() > 0 =>
            await Controls.PlantedAsync(options["fixtures"], options["runs"], options["evaluation"], options["prefix"], options["out"], Timeout(), cancellation.Token),
        "anchors" when Has("reference-accounting", "reference-integrity", "evaluation", "out") =>
            await AnchorsCommand.RunAsync(options["reference-accounting"], options["reference-integrity"], options["evaluation"], options["out"]),
        "verdict" when Has("runs", "evaluation", "integrity-raw", "controls", "anchors", "identity", "definitions", "fixture-report", "reference-sums", "runner-diff", "summary") =>
            await Verdict.RunAsync(options["runs"], options["evaluation"], options["integrity-raw"], options["controls"], options["anchors"],
                FileList("identity"), FileList("definitions"), options["fixture-report"], options["reference-sums"], options["runner-diff"], options["summary"],
                options.GetValueOrDefault("environment")),
        _ => Usage(),
    };
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Cancelled.");
    return 1;
}

static int Usage()
{
    Console.Error.WriteLine("Usage: group-fixtures | evaluate | controls | anchors | verdict (see the comments at the top of Program.cs)");
    return 2;
}
