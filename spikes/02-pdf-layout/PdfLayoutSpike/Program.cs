using System.Globalization;
using PdfLayoutSpike;

// Layout experiment: lays out blocks of the grouped pilot dataset with the
// pagination library inside headless Chromium, checks the page accounting and
// measures memory and time. Commands:
//   versions                                   prints Playwright and Chromium versions
//   probe-native --out DIR                     native margin-box probe (3-page PDF)
//   run --dataset FILE --sums FILE --out DIR [--matrix full|quick] [--timeout-seconds 600] [--font NAME]
// Exit codes of run: 0 when every check on the ordinary blocks passes, no ordinary run logs
// "Layout repeated", and the giant-row fixture shows exactly its documented failure signature
// (with displacement and escaping passing) in every configuration run; 1 otherwise (an
// ordinary check fails, a run errors, or the giant-row fixture behaves in any other way,
// a pass included); 2 invalid arguments. The tall-row series never sets the exit code,
// except through an escaping failure or a run error.

const string DefaultFont = "Liberation Sans";

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

var font = options.GetValueOrDefault("font", DefaultFont);
var baseCss = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "assets", "layout.css"));
var builder = new BlockHtmlBuilder(baseCss, font);

switch (args[0])
{
    case "versions":
        {
            await using var runner = await LayoutRunner.StartAsync();
            Console.WriteLine($"Playwright {LayoutRunner.PlaywrightVersion}");
            Console.WriteLine($"Chromium {runner.ChromiumVersion}");
            Console.WriteLine($".NET {Environment.Version}");
            return 0;
        }

    case "probe-native":
        {
            if (!options.TryGetValue("out", out var outDirectory))
            {
                return Usage();
            }

            Directory.CreateDirectory(outDirectory);
            await using var runner = await LayoutRunner.StartAsync();
            var (path, pages) = await runner.ProbeNativeAsync(builder.BuildNativeProbe(), outDirectory);
            Console.WriteLine($"Native probe printed to {path}: {pages} pages (heuristic). Check the header and 'Page X of Y' on pages 1 and 3.");
            return 0;
        }

    case "run":
        {
            if (!options.TryGetValue("dataset", out var dataset) || !options.TryGetValue("sums", out var sums) || !options.TryGetValue("out", out var outDirectory))
            {
                return Usage();
            }

            var matrixName = options.GetValueOrDefault("matrix", "full");
            if (matrixName is not ("full" or "quick")
                || !int.TryParse(options.GetValueOrDefault("timeout-seconds", "600"), NumberStyles.None, CultureInfo.InvariantCulture, out var timeoutSeconds)
                || timeoutSeconds <= 0)
            {
                return Usage();
            }

            var startedAt = DateTimeOffset.Now;
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
            foreach (var folder in new[] { "pdf", "png", "layout" })
            {
                Directory.CreateDirectory(Path.Combine(outDirectory, folder));
            }

            await using var runner = await LayoutRunner.StartAsync();
            Console.WriteLine($"Playwright {LayoutRunner.PlaywrightVersion}, Chromium {runner.ChromiumVersion}, {Environment.ProcessorCount} CPUs, font '{font}'.");
            var baseline = await runner.SampleIdleAsync(TimeSpan.FromSeconds(2));
            var commandLine = runner.Sampler.ChromiumCommandLine();
            Console.WriteLine($"Idle browser baseline: Chromium tree RSS {baseline.ChromiumTreePeakRssKiB} KiB, PSS {baseline.ChromiumTreePeakPssKiB} KiB, driver {baseline.DriverPeakRssKiB} KiB.");

            var matrix = new RunMatrix(source, builder, runner, outDirectory, timeoutSeconds);
            await matrix.ExecuteAsync(matrixName == "full");
            var exitCode = matrix.ExitCode();

            var environment = new RunEnvironment(
                startedAt.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture),
                DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture),
                Environment.ProcessorCount,
                ProcessTreeSampler.MemoryTotalKiB(),
                ResultsWriter.ReadCgroup("memory.max"),
                ResultsWriter.ReadCgroup("cpu.max"),
                ResultsWriter.OperatingSystemDescription,
                $".NET {Environment.Version}",
                LayoutRunner.PlaywrightVersion,
                runner.ChromiumVersion,
                commandLine,
                font,
                timeoutSeconds,
                matrixName);
            await ResultsWriter.WriteAsync(outDirectory, environment, source, baseline, matrix, exitCode);
            Console.WriteLine($"Primary configuration: {matrix.Primary.Name} — {matrix.PrimaryNote}.");
            matrix.PrintVerdict();
            Console.WriteLine($"Results written to {outDirectory}. Exit code {exitCode}.");
            return exitCode;
        }

    default:
        return Usage();
}

static int Usage()
{
    Console.Error.WriteLine("Usage: versions | probe-native --out DIR | run --dataset FILE --sums FILE --out DIR [--matrix full|quick] [--timeout-seconds 600] [--font NAME]");
    return 2;
}
