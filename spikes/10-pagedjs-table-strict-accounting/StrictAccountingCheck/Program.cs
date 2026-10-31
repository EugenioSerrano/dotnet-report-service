using System.Globalization;
using StrictAccountingCheck;

// Strict accounting check of the table-and-grid experiment. Reads the printed PDFs
// with poppler-utils only (never with the layout library, its page or its console),
// feeds the accounting rules of spikes/02-pdf-layout from them page by page through
// the PDF reader of spikes/09-two-pass-page-subtotals, and adds a column-agnostic
// character check and a column-geometry observation. Commands:
//   probe     --evidence DIR --fixtures DIR --out DIR [--timeout-seconds 300]
//   account   --fixtures DIR --runs DIR[,DIR] --integrity FILE[,FILE] --out DIR [--timeout-seconds 300]
//   controls  --fixtures DIR --runs DIR[,DIR] --integrity FILE[,FILE] --accounting FILE --extraction-grid FILE
//             [--extraction-table FILE] --probe FILE --out DIR [--timeout-seconds 300]
//   compare   --reference-summary FILE --reference-integrity FILE --accounting FILE --runs DIR --out FILE [--base-digest D]
//   verdict   --runs DIR[,DIR] --integrity FILE[,FILE] --accounting FILE --controls FILE --identity FILE[,FILE]
//             --parity FILE --comparison FILE --summary FILE [--environment FILE]
// Exit codes: 0 success (probe: every known answer holds; verdict: the instrument is healthy); 1 failure
// (probe: a known answer does not hold; verdict: see the printed reasons); 2 invalid arguments.

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

List<string> Many(string name) => [.. options[name].Split(',', StringSplitOptions.RemoveEmptyEntries)];

int Timeout() => int.TryParse(options.GetValueOrDefault("timeout-seconds", "300"), NumberStyles.None, CultureInfo.InvariantCulture, out var t) && t > 0 ? t : -1;

try
{
    return args[0] switch
    {
        "probe" when Has("evidence", "fixtures", "out") && Timeout() > 0 =>
            await Probe.RunAsync(options["evidence"], options["fixtures"], options["out"], Timeout(), cancellation.Token),
        "account" when Has("fixtures", "runs", "integrity", "out") && Timeout() > 0 =>
            await Account.RunAsync(options["fixtures"], Many("runs"), Many("integrity"), options["out"], Timeout(), cancellation.Token),
        "controls" when Has("fixtures", "runs", "integrity", "accounting", "extraction-grid", "probe", "out") && Timeout() > 0 =>
            await Controls.RunAsync(options["fixtures"], Many("runs"), Many("integrity"), options["accounting"], options["extraction-grid"],
                options.GetValueOrDefault("extraction-table"), options["probe"], options["out"], Timeout(), cancellation.Token),
        "compare" when Has("reference-summary", "reference-integrity", "accounting", "runs", "out") =>
            await Compare.RunAsync(options["reference-summary"], options["reference-integrity"], options["accounting"], options["runs"], options["out"], options.GetValueOrDefault("base-digest")),
        "verdict" when Has("runs", "integrity", "accounting", "controls", "identity", "parity", "comparison", "summary") =>
            await Verdict.RunAsync(Many("runs"), Many("integrity"), options["accounting"], options["controls"], Many("identity"), options["parity"],
                options["comparison"], options["summary"], options.GetValueOrDefault("environment")),
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
    Console.Error.WriteLine("Usage: probe --evidence DIR --fixtures DIR --out DIR | account --fixtures DIR --runs DIR[,DIR] --integrity FILE[,FILE] --out DIR | "
        + "controls ... | compare ... | verdict ... (see the comments at the top of Program.cs)");
    return 2;
}
