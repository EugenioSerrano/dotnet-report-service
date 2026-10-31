using System.Globalization;
using GroupContinuityCheck;
using StrictAccountingCheck;

// Check of the group-continuity experiment. Reads the printed PDFs with poppler-utils
// only (never with the layout library, its page or its console). Commands:
//   group-fixtures --dataset FILE --sums FILE --reference DIR --tall FILE --out DIR
//   probe     --evidence DIR --fixtures DIR --reference DIR --out DIR
//   account   --fixtures DIR --runs DIR --integrity FILE --out DIR       (the strict check of spikes/10-pagedjs-table-strict-accounting)
//   groups    --fixtures DIR --runs DIR --out DIR
//   controls  --fixtures DIR --runs DIR --integrity FILE --accounting FILE --groups FILE --extraction FILE --probe FILE --out DIR
//   anchors   --reference-summary FILE --reference-integrity FILE --accounting FILE --runs DIR --out FILE
//   verdict   --runs DIR --integrity FILE --accounting FILE --groups FILE --controls FILE --anchors FILE
//             --identity FILE[,FILE] --fixture-report FILE --summary FILE [--environment FILE]
// Every command takes [--timeout-seconds 300] where it reads PDFs. Exit codes: 0 success (probe: every known
// answer holds; verdict: the instrument is healthy); 1 failure; 2 invalid arguments.

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

try
{
    return args[0] switch
    {
        "group-fixtures" when Has("dataset", "sums", "reference", "tall", "out") =>
            GroupFixtures.Run(options["dataset"], options["sums"], options["reference"], options["tall"], options["out"]),
        "probe" when Has("evidence", "fixtures", "reference", "out") && Timeout() > 0 =>
            await ProbeCommand.RunAsync(options["evidence"], options["fixtures"], options["reference"], options["out"], Timeout(), cancellation.Token),
        "account" when Has("fixtures", "runs", "integrity", "out") && Timeout() > 0 =>
            await Account.RunAsync(options["fixtures"], [options["runs"]], [options["integrity"]], options["out"], Timeout(), cancellation.Token),
        "groups" when Has("fixtures", "runs", "out") && Timeout() > 0 =>
            await GroupsCommand.RunAsync(options["fixtures"], options["runs"], options["out"], Timeout(), cancellation.Token),
        "controls" when Has("fixtures", "runs", "integrity", "accounting", "groups", "extraction", "probe", "out") && Timeout() > 0 =>
            await GroupControls.RunAsync(options["fixtures"], options["runs"], options["integrity"], options["accounting"], options["groups"], options["extraction"], options["probe"], options["out"], Timeout(), cancellation.Token),
        "anchors" when Has("reference-summary", "reference-integrity", "accounting", "runs", "out") =>
            await AnchorsCommand.RunAsync(options["reference-summary"], options["reference-integrity"], options["accounting"], options["runs"], options["out"]),
        "verdict" when Has("runs", "integrity", "accounting", "groups", "controls", "anchors", "identity", "fixture-report", "summary") =>
            await Verdict.RunAsync(options["runs"], options["integrity"], options["accounting"], options["groups"], options["controls"], options["anchors"],
                [.. options["identity"].Split(',', StringSplitOptions.RemoveEmptyEntries)], options["fixture-report"], options["summary"], options.GetValueOrDefault("environment")),
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
    Console.Error.WriteLine("Usage: group-fixtures | probe | account | groups | controls | anchors | verdict (see the comments at the top of Program.cs)");
    return 2;
}
