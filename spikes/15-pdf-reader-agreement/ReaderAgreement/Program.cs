using System.Globalization;
using ReaderAgreement;

// Measures PDF readers whose licence the product may ship against the earlier
// experiments' reference reading. Commands:
//   selftest
//   synthetic   --out FILE                                             (a two-page test PDF outside the measured set, for smoke tests)
//   inventory   --repo DIR --results DIR
//   fixtures    --repo DIR --out DIR
//   freeze      --results DIR
//   regenerated --repo DIR --dir DIR --results DIR --images TEXT
//   drive       --stage oracle|candidates --repo DIR --work DIR --results DIR
//   extract     --reader oracle|pdfpig|pdfsharp --pdf FILE --id ID --out DIR --repetition N   (one run; started by drive)
//   reference   --repo DIR --work DIR --fixtures DIR --results DIR        (the reference reading alone, before any candidate result)
//   compare     --repo DIR --work DIR --fixtures DIR --results DIR        (each candidate against the trusted reference)
// Exit codes: 0 success; 1 failure (a check, an untrusted reference, a difference); 2 invalid arguments.

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

return args[0] switch
{
    "selftest" => SelfTest.Run(),
    "synthetic" when Has("out") => SelfTest.Synthetic(options["out"]),
    "inventory" when Has("repo", "results") => Inventory.Run(options["repo"], options["results"]),
    "fixtures" when Has("repo", "out") => FixturesCommand.Run(options["repo"], options["out"]),
    "freeze" when Has("results") => Freeze.Write(options["results"]),
    "regenerated" when Has("repo", "dir", "results", "images") => Regeneration.Locate(options["repo"], options["dir"], options["results"], options["images"]),
    "drive" when Has("stage", "repo", "work", "results") => await Driver.RunAsync(options["stage"], options["repo"], options["work"], options["results"]),
    "extract" when Has("reader", "pdf", "id", "out", "repetition") =>
        await ExtractCommand.RunAsync(options["reader"], options["pdf"], options["id"], options["out"], int.Parse(options["repetition"], CultureInfo.InvariantCulture)),
    "reference" when Has("repo", "work", "fixtures", "results") => Assessment.OracleVerdict(options["repo"], options["work"], options["fixtures"], options["results"]),
    "compare" when Has("repo", "work", "fixtures", "results") => Assessment.Compare(options["repo"], options["work"], options["fixtures"], options["results"]),
    _ => Usage(),
};

static int Usage()
{
    Console.Error.WriteLine("Usage: selftest | inventory | fixtures | freeze | regenerated | drive | extract | reference | compare (see Program.cs).");
    return 2;
}
