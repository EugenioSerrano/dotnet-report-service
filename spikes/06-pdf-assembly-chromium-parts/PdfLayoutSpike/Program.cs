using System.Globalization;
using System.Security.Cryptography;
using PdfLayoutSpike;

// Producer of partial PDFs printed by Chromium for the assembly experiment. The
// layout harness (builder, runner, accounting checks, pagination library) is used
// unchanged; this entry point only drives it. Commands:
//   versions                                                         Playwright, Chromium and .NET versions
//   produce --pages 1000|5000|10000 [--seed N] [--set-name NAME] --dataset FILE --sums FILE
//           --baseline FILE [--cross-check-only] --out DIR
//                                                                    --cross-check-only stops after the cross-check
//   produce-probe --dataset FILE --sums FILE --out DIR               2 parts of exactly 10 pages each
// Exit codes: 0 success; 1 a part cannot be produced or accepted, or the page-count
// lists differ from the earlier experiment's; 2 invalid arguments.

const string Font = "Liberation Sans";
const string WorkDirectory = "/tmp/produce";
const int TimeoutSeconds = 600;

if (args.Length == 0)
{
    return Usage();
}

var options = new Dictionary<string, string>(StringComparer.Ordinal);
for (var i = 1; i < args.Length; i++)
{
    if (args[i] == "--cross-check-only")
    {
        options["cross-check-only"] = "true";
        continue;
    }

    if (!args[i].StartsWith("--", StringComparison.Ordinal) || i + 1 >= args.Length)
    {
        return Usage();
    }

    options[args[i][2..]] = args[++i];
}

var baseCss = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "assets", "layout.css"));
var builder = new BlockHtmlBuilder(baseCss, Font);

switch (args[0])
{
    case "versions":
        {
            await using var runner = await LayoutRunner.StartAsync();
            Console.WriteLine($"Playwright {LayoutRunner.PlaywrightVersion}");
            Console.WriteLine($"Chromium {runner.ChromiumVersion}");
            Console.WriteLine($".NET {Environment.Version}");
            Console.WriteLine($"Producer code commit {Environment.GetEnvironmentVariable("PRODUCER_CODE_COMMIT") ?? "not recorded in-container"}");
            Console.WriteLine($"PDF base digest {Environment.GetEnvironmentVariable("PDF_BASE_DIGEST") ?? "not recorded in-container"}");
            return 0;
        }

    case "produce":
    case "produce-probe":
        {
            bool probe = args[0] == "produce-probe";
            if (!options.TryGetValue("dataset", out var dataset) || !options.TryGetValue("sums", out var sums) || !options.TryGetValue("out", out var outDirectory))
            {
                return Usage();
            }

            int targetPages = 20;
            ulong seed = PartSetProducer.DefaultSeed;
            string? baseline = null;
            if (!probe)
            {
                if (!options.TryGetValue("pages", out var pagesText)
                    || !int.TryParse(pagesText, NumberStyles.None, CultureInfo.InvariantCulture, out targetPages)
                    || targetPages is not (1000 or 5000 or 10000)
                    || !options.TryGetValue("baseline", out baseline)
                    || (options.TryGetValue("seed", out var seedText) && !ulong.TryParse(seedText, NumberStyles.None, CultureInfo.InvariantCulture, out seed)))
                {
                    return Usage();
                }
            }

            string setName = probe ? "probe" : options.GetValueOrDefault("set-name", targetPages.ToString(CultureInfo.InvariantCulture));
            if (setName.Length == 0 || setName.IndexOfAny(['/', '\\', '.']) >= 0)
            {
                return Usage();
            }

            int[] counts;
            try
            {
                counts = probe ? [10, 10] : PartSetProducer.DrawPageCounts(targetPages, seed);
                if (!probe)
                {
                    PartSetProducer.CrossCheck(targetPages, seed, counts, baseline!);
                    Console.WriteLine($"Page counts of set {targetPages}: {counts.Length} parts, equal to the earlier experiment's list.");
                }
            }
            catch (ProductionException e)
            {
                Console.Error.WriteLine("cross-check failed: " + e.Message);
                return 1;
            }

            if (!probe && options.ContainsKey("cross-check-only"))
            {
                for (int i = 0; i < counts.Length; i++)
                {
                    Console.WriteLine($"{i + 1} {PartSetProducer.FileName(seed, targetPages, i + 1)} {counts[i]}");
                }

                Console.WriteLine($"cross-check only: {counts.Length} parts, {counts.Sum()} pages; nothing laid out.");
                return 0;
            }

            BlockSource source;
            try
            {
                source = BlockSource.Load(dataset, sums);
            }
            catch (InvalidDataException e)
            {
                Console.Error.WriteLine(e.Message);
                return 1;
            }

            Console.WriteLine($"Dataset {source.FileName} sha256 {source.Sha256} ({source.Records.Count} records) verified.");
            await using var runner = await LayoutRunner.StartAsync();
            var pagedJs = Path.Combine(AppContext.BaseDirectory, "assets", "vendor", "pagedjs-0.4.3", "paged.polyfill.js");
            var producer = new ProducerInfo(
                Environment.GetEnvironmentVariable("PRODUCER_CODE_COMMIT") ?? "not recorded in-container",
                "0.4.3",
                Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(pagedJs))),
                PartSetProducer.Configuration.Name,
                "on",
                LayoutRunner.PlaywrightVersion,
                runner.ChromiumVersion,
                Environment.GetEnvironmentVariable("PDF_BASE_DIGEST") ?? "not recorded in-container",
                Font,
                Environment.Version.ToString());
            Console.WriteLine($"Playwright {producer.PlaywrightVersion}, Chromium {producer.ChromiumVersion}, configuration {producer.Configuration}, footer on, font '{Font}'.");
            var setProducer = new PartSetProducer(source, builder, runner, WorkDirectory, TimeoutSeconds, Font);
            try
            {
                string manifest = await setProducer.ProduceAsync(targetPages, seed, counts, Path.Combine(outDirectory, setName), producer);
                Console.WriteLine($"produced {manifest}");
                return 0;
            }
            catch (ProductionException e)
            {
                Console.Error.WriteLine("production stopped: " + e.Message);
                return 1;
            }
        }

    default:
        return Usage();
}

static int Usage()
{
    Console.Error.WriteLine("Usage: versions | produce --pages 1000|5000|10000 [--seed N] [--set-name NAME] --dataset FILE --sums FILE --baseline FILE [--cross-check-only] --out DIR | produce-probe --dataset FILE --sums FILE --out DIR");
    return 2;
}
