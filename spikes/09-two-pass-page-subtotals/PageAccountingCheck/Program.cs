using System.Globalization;
using PageAccountingCheck;

// Accounting checker of the two-pass experiment. Reads the printed PDFs back with
// poppler-utils and qpdf (never with the PDF library under test), feeds the earlier
// layout spike's accounting rules from them, and judges the second pass's reading,
// the area outside the reserved area and the page boxes. Commands:
//   ordinary-fixtures --dataset FILE --sums FILE --reference DIR --out DIR
//   account   --fixtures DIR --runs DIR --integrity FILE --out DIR [--reference-integrity FILE] [--work DIR] [--timeout-seconds 300]
//   controls  --fixtures DIR --runs DIR --out DIR [--work DIR] [--timeout-seconds 300]
//   verdict   --runs DIR --integrity FILE --controls FILE --accounting FILE --accounting-controls FILE --identity FILE
//             --matrix full|quick --fixtures DIR --shared-sums FILE [--summary FILE] [--harness-summary FILE] [--environment FILE]
//   probe markers|stamp|split-rows|stamped-ab --fixtures DIR --runs DIR --integrity FILE --out DIR [--reference-integrity FILE] [--work DIR]
// Exit codes: 0 success (verdict: instrument healthy; probe: every item holds); 1 failure (verdict: see the
// printed reasons; ordinary-fixtures: dataset hash mismatch or a failed self-check; probe: stop); 2 invalid arguments.

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
for (var i = first; i < args.Length; i++)
{
    if (!args[i].StartsWith("--", StringComparison.Ordinal) || i + 1 >= args.Length)
    {
        return Usage();
    }

    options[args[i][2..]] = args[++i];
}

bool Has(params string[] names) => names.All(options.ContainsKey);

int Timeout() => int.TryParse(options.GetValueOrDefault("timeout-seconds", "300"), NumberStyles.None, CultureInfo.InvariantCulture, out var t) && t > 0 ? t : -1;

string Work() => options.GetValueOrDefault("work") ?? Path.Combine(Path.GetTempPath(), "page-accounting-check");

try
{
    switch (command)
    {
        case "ordinary-fixtures" when Has("dataset", "sums", "reference", "out"):
            return OrdinaryFixtures.Run(options["dataset"], options["sums"], options["reference"], options["out"]);
        case "account" when Has("fixtures", "runs", "integrity", "out") && Timeout() > 0:
            return await AccountCommand.RunAsync(options["fixtures"], options["runs"], options["integrity"], options.GetValueOrDefault("reference-integrity"), options["out"], Work(), Timeout(), cancellation.Token);
        case "controls" when Has("fixtures", "runs", "out") && Timeout() > 0:
            return await ControlsCommand.RunAsync(options["fixtures"], options["runs"], options["out"], Work(), Timeout(), cancellation.Token);
        case "verdict" when Has("runs", "integrity", "controls", "accounting", "accounting-controls", "identity", "matrix", "fixtures", "shared-sums") && options["matrix"] is "full" or "quick":
            return await VerdictCommand.RunAsync(options);
        case "probe-markers" when Has("fixtures", "runs", "integrity", "out"):
            return await ProbeCommand.RunAsync("markers", options, cancellation.Token);
        case "probe-stamp" when Has("fixtures", "runs", "integrity", "out"):
            return await ProbeCommand.RunAsync("stamp", options, cancellation.Token);
        case "probe-split-rows" when Has("fixtures", "runs", "integrity", "out"):
            return await ProbeCommand.RunAsync("split-rows", options, cancellation.Token);
        case "probe-stamped-ab" when Has("fixtures", "runs", "integrity", "out"):
            return await ProbeCommand.RunAsync("stamped-ab", options, cancellation.Token);
        default:
            return Usage();
    }
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Cancelled.");
    return 1;
}

static int Usage()
{
    Console.Error.WriteLine("Usage: ordinary-fixtures --dataset FILE --sums FILE --reference DIR --out DIR | "
        + "account --fixtures DIR --runs DIR --integrity FILE --out DIR [--reference-integrity FILE] [--work DIR] [--timeout-seconds 300] | "
        + "controls --fixtures DIR --runs DIR --out DIR [--work DIR] [--timeout-seconds 300] | "
        + "verdict --runs DIR --integrity FILE --controls FILE --accounting FILE --accounting-controls FILE --identity FILE --matrix full|quick --fixtures DIR --shared-sums FILE [--summary FILE] [--harness-summary FILE] [--environment FILE] | "
        + "probe markers|stamp|split-rows|stamped-ab --fixtures DIR --runs DIR --integrity FILE --out DIR [--reference-integrity FILE] [--work DIR]");
    return 2;
}
