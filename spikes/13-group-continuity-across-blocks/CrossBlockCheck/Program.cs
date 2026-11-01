using System.Globalization;
using GroupContinuityCheck;
using StrictAccountingCheck;

// Check of the group-continuity-across-blocks experiment. Reads the assembled PDFs with
// poppler-utils only (never with the layout library, its page or its console). Commands:
//   part-fixtures --dataset FILE --sums FILE --reference DIR --tall FILE --group-reference FILE [--calibration FILE]
//                 [--calibration-from 240 --calibration-to 261] --out DIR
//   calibrate     --runs DIR --out FILE [--calibration-from 240 --calibration-to 261]
//   set-manifest  --fixtures DIR --runs DIR --out DIR                     (one folder per assembled report, the assembly tool's format)
//   slices        --reports DIR --runs DIR --out DIR                      (the unnumbered reports cut back into one PDF per part)
//   report        --fixtures DIR --slices DIR --reports DIR --out DIR     (accounting and group rules across parts, the boundary chain, observations)
//   observe       --fixtures DIR --slices DIR --reports DIR --out DIR     (the observations only)
//   account       --fixtures DIR --runs DIR --integrity FILE --out DIR    (the strict check of spikes/10-pagedjs-table-strict-accounting)
//   controls      --fixtures DIR --runs DIR --slices DIR --reports DIR --integrity FILE --integrity-slices FILE --extraction FILE
//                 --accounting FILE --report FILE --probe FILE --out DIR
//   parity        --reference DIR --fixtures DIR --runs DIR --integrity FILE --accounting FILE --out FILE
//   verdict       --fixtures DIR --runs DIR --reports DIR --report FILE --observations FILE --controls FILE --parity FILE
//                 --integrity FILE --integrity-slices FILE --accounting FILE --identity FILE[,FILE] --fixture-report FILE
//                 --calibration FILE --summary FILE
// Commands that read PDFs take [--timeout-seconds 300]. Exit codes: 0 success (calibrate: a block limit was found;
// verdict: the instrument is healthy); 1 failure; 2 invalid arguments.

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

int Int(string name, int fallback) => int.TryParse(options.GetValueOrDefault(name), NumberStyles.None, CultureInfo.InvariantCulture, out var v) ? v : fallback;

try
{
    return args[0] switch
    {
        "part-fixtures" when Has("dataset", "sums", "reference", "tall", "group-reference", "out") =>
            PartFixtures.Run(options["dataset"], options["sums"], options["reference"], options["tall"], options["group-reference"], options.GetValueOrDefault("calibration"),
                Int("calibration-from", 240), Int("calibration-to", 261), options["out"]),
        "calibrate" when Has("runs", "out") && Timeout() > 0 =>
            await PartFixtures.CalibrateAsync(options["runs"], Int("calibration-from", 240), Int("calibration-to", 261), options["out"], Timeout(), cancellation.Token),
        "set-manifest" when Has("fixtures", "runs", "out") && Timeout() > 0 =>
            await Instances.SetManifestsAsync(options["fixtures"], options["runs"], options["out"], Timeout(), cancellation.Token),
        "slices" when Has("reports", "runs", "out") && Timeout() > 0 =>
            await Instances.SlicesAsync(options["reports"], options["runs"], options["out"], Timeout(), cancellation.Token),
        "report" when Has("fixtures", "slices", "reports", "out") && Timeout() > 0 =>
            await ReportCommand.RunAsync(options["fixtures"], options["slices"], options["reports"], options["out"], false, Timeout(), cancellation.Token),
        "observe" when Has("fixtures", "slices", "reports", "out") && Timeout() > 0 =>
            await ReportCommand.RunAsync(options["fixtures"], options["slices"], options["reports"], options["out"], true, Timeout(), cancellation.Token),
        "account" when Has("fixtures", "runs", "integrity", "out") && Timeout() > 0 =>
            await Account.RunAsync(options["fixtures"], [options["runs"]], [options["integrity"]], options["out"], Timeout(), cancellation.Token),
        "controls" when Has("fixtures", "runs", "slices", "reports", "integrity", "integrity-slices", "extraction", "accounting", "report", "probe", "out") && Timeout() > 0 =>
            await ReportControls.RunAsync(options, Timeout(), cancellation.Token),
        "parity" when Has("reference", "fixtures", "runs", "integrity", "accounting", "out") && Timeout() > 0 =>
            await ParityCommand.RunAsync(options, Timeout(), cancellation.Token),
        "verdict" when Has("fixtures", "runs", "reports", "report", "observations", "controls", "parity", "integrity", "integrity-slices", "accounting", "identity", "fixture-report", "calibration", "summary") =>
            await Verdict.RunAsync(options),
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
    Console.Error.WriteLine("Usage: part-fixtures | calibrate | set-manifest | slices | report | observe | account | controls | parity | verdict (see the comments at the top of Program.cs)");
    return 2;
}
