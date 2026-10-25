using System.Globalization;
using TextIntegrityCheck;

// Text-integrity checker of the native pagination experiment. Reads printed PDFs
// back with poppler-utils and compares their text with the fixtures they were
// printed from. Commands:
//   fixtures  --dataset FILE --sums FILE --out DIR [--only ID,...]
//   check     --fixtures DIR --runs DIR --out DIR [--timeout-seconds 300]       reads runs.jsonl, writes integrity.json
//   controls  --fixtures DIR --runs DIR --out DIR [--base RUN] [--timeout-seconds 300]   E1–E8, writes controls.json
//   verdict   --runs DIR --integrity FILE --controls FILE --matrix full|quick [--summary FILE]
//   render    --pdf FILE --pages N,... --out DIR                                 page images with pdftoppm
// Exit codes: 0 success (verdict: instrument healthy); 1 failure (verdict: see the printed reasons;
// fixtures: dataset hash mismatch or a failed assertion); 2 invalid arguments.

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
    switch (args[0])
    {
        case "fixtures" when Has("dataset", "sums", "out"):
            return Commands.Fixtures(options["dataset"], options["sums"], options["out"], options.TryGetValue("only", out var only) ? only.Split(',', StringSplitOptions.RemoveEmptyEntries) : null);
        case "check" when Has("fixtures", "runs", "out") && Timeout() > 0:
            return await Commands.CheckAsync(options["fixtures"], options["runs"], options["out"], Timeout(), cancellation.Token);
        case "controls" when Has("fixtures", "runs", "out") && Timeout() > 0:
            return await Commands.ControlsAsync(options["fixtures"], options["runs"], options["out"], options.GetValueOrDefault("base"), Timeout(), cancellation.Token);
        case "verdict" when Has("runs", "integrity", "controls", "matrix") && options["matrix"] is "full" or "quick":
            return await VerdictCommand.RunAsync(options["runs"], options["integrity"], options["controls"], options["matrix"], options.GetValueOrDefault("summary"));
        case "render" when Has("pdf", "pages", "out"):
            {
                var pages = new List<int>();
                foreach (var part in options["pages"].Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (!int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var page) || page < 1)
                    {
                        return Usage();
                    }

                    pages.Add(page);
                }

                return await Commands.RenderAsync(options["pdf"], pages, options["out"], cancellation.Token);
            }

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
    Console.Error.WriteLine("Usage: fixtures --dataset FILE --sums FILE --out DIR [--only ID,...] | check --fixtures DIR --runs DIR --out DIR [--timeout-seconds 300] | "
        + "controls --fixtures DIR --runs DIR --out DIR [--base RUN] [--timeout-seconds 300] | verdict --runs DIR --integrity FILE --controls FILE --matrix full|quick [--summary FILE] | "
        + "render --pdf FILE --pages N,... --out DIR");
    return 2;
}
