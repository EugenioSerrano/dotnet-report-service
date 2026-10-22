using System.Diagnostics;
using System.Globalization;

namespace ReportService.Samples.DatasetGenerator;

/// <summary>Entry point: parses the command line and dispatches to the command.</summary>
internal static class Program
{
    private const string Usage = """
        Generates and verifies the seeded pilot datasets in the header-items-v1 envelope.

        Usage:
          generate [--dataset simple|grouped|volume|all] [--count N] [--seed N] [--output DIR]
          verify   [--sums FILE] [--templates DIR]
          check    FILE [--initial-buffer BYTES]
          samples  [--templates DIR]

        Defaults: --dataset all, --seed 20261021, --count = the dataset's default
        (simple 25, grouped 10000, volume 1000000), --output samples/datasets,
        --sums samples/datasets/SHA256SUMS, --templates samples/templates, all
        relative to the repository root (the nearest folder above the current
        directory that contains dotnet-report-service.slnx).

        Exit codes: 0 success, 1 failure, 2 invalid arguments.
        """;

    /// <summary>Runs one command and returns its exit code.</summary>
    public static int Main(string[] args)
    {
        try
        {
            if (args.Length == 0)
            {
                throw new UsageException("A command is required.");
            }

            return args[0] switch
            {
                "generate" => Commands.Generate(CommandLine.Parse(args, ["--dataset", "--count", "--seed", "--output"], 0)),
                "verify" => Commands.Verify(CommandLine.Parse(args, ["--sums", "--templates"], 0)),
                "check" => Commands.Check(CommandLine.Parse(args, ["--initial-buffer"], 1)),
                "samples" => Commands.Samples(CommandLine.Parse(args, ["--templates"], 0)),
                _ => throw new UsageException($"Unknown command '{args[0]}'."),
            };
        }
        catch (UsageException exception)
        {
            Console.Error.WriteLine(exception.Message);
            Console.Error.WriteLine();
            Console.Error.WriteLine(Usage);
            return 2;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or FailureException or FormatException)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }
}

/// <summary>Invalid command-line arguments (exit code 2).</summary>
internal sealed class UsageException(string message) : Exception(message);

/// <summary>A command that cannot be carried out (exit code 1).</summary>
internal sealed class FailureException(string message) : Exception(message);

/// <summary>Parsed options and positional arguments of one command.</summary>
/// <param name="Options">Option values by name.</param>
/// <param name="Positional">Positional arguments, in order.</param>
internal sealed record CommandLine(IReadOnlyDictionary<string, string> Options, IReadOnlyList<string> Positional)
{
    /// <summary>Parses <c>--name value</c> pairs and exactly <paramref name="positionalCount"/> positional arguments.</summary>
    public static CommandLine Parse(string[] args, string[] allowed, int positionalCount)
    {
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        var positional = new List<string>();
        for (var i = 1; i < args.Length; i++)
        {
            var argument = args[i];
            if (argument.StartsWith("--", StringComparison.Ordinal))
            {
                if (!allowed.Contains(argument))
                {
                    throw new UsageException($"Unknown option '{argument}' for '{args[0]}'.");
                }

                if (i + 1 >= args.Length)
                {
                    throw new UsageException($"Option '{argument}' needs a value.");
                }

                if (!options.TryAdd(argument, args[++i]))
                {
                    throw new UsageException($"Option '{argument}' is given more than once.");
                }
            }
            else
            {
                positional.Add(argument);
            }
        }

        if (positional.Count != positionalCount)
        {
            throw new UsageException($"'{args[0]}' takes {positionalCount} positional argument(s), {positional.Count} given.");
        }

        return new CommandLine(options, positional);
    }

    /// <summary>Returns the option value, or <c>null</c> when it is not given.</summary>
    public string? Get(string name) => Options.TryGetValue(name, out var value) ? value : null;
}

/// <summary>The four commands.</summary>
internal static class Commands
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary><c>generate</c>: writes the requested datasets and prints their statistics.</summary>
    public static int Generate(CommandLine line)
    {
        var datasetName = line.Get("--dataset") ?? "all";
        DatasetDefinition[] datasets = datasetName == "all"
            ? DatasetDefinitions.All
            : [DatasetDefinitions.All.FirstOrDefault(d => d.Name == datasetName)
                ?? throw new UsageException($"Unknown dataset '{datasetName}' (simple, grouped, volume or all).")];

        long? count = null;
        if (line.Get("--count") is { } countText)
        {
            if (datasetName == "all")
            {
                throw new UsageException("--count needs a single --dataset, not 'all'.");
            }

            if (!long.TryParse(countText, NumberStyles.None, Inv, out var parsed) || parsed <= 0)
            {
                throw new UsageException($"--count must be a positive whole number, not '{countText}'.");
            }

            count = parsed;
        }

        var seed = DatasetDefinitions.DefaultSeed;
        if (line.Get("--seed") is { } seedText && !ulong.TryParse(seedText, NumberStyles.None, Inv, out seed))
        {
            throw new UsageException($"--seed must be a non-negative whole number, not '{seedText}'.");
        }

        var output = line.Get("--output") ?? RepositoryPaths.DatasetsFolder(RequireRoot("--output"));
        Directory.CreateDirectory(output);
        foreach (var definition in datasets)
        {
            var records = count ?? definition.DefaultCount;
            var file = EnvelopeWriter.Write(definition, seed, records, Path.Combine(output, definition.FileName(records)), definition.Indented);
            PrintFile(file);
        }

        PrintPeakWorkingSet();
        return 0;
    }

    /// <summary><c>verify</c>: the repeatable check of determinism, hashes, structure, characteristics and sample files.</summary>
    public static int Verify(CommandLine line)
    {
        var sumsPath = line.Get("--sums") ?? RepositoryPaths.SumsFile(RequireRoot("--sums"));
        var templates = line.Get("--templates") ?? RepositoryPaths.TemplatesFolder(RequireRoot("--templates"));
        var checks = new List<bool>();
        var temp = Path.Combine(Path.GetTempPath(), "report-service-datasets-" + Guid.NewGuid().ToString("N"));
        var first = Path.Combine(temp, "first");
        var second = Path.Combine(temp, "second");
        var samples = Path.Combine(temp, "samples");
        try
        {
            Directory.CreateDirectory(first);
            Directory.CreateDirectory(second);
            var files = new List<GeneratedFile>();
            foreach (var definition in DatasetDefinitions.All)
            {
                var name = definition.FileName(definition.DefaultCount);
                var a = EnvelopeWriter.Write(definition, DatasetDefinitions.DefaultSeed, definition.DefaultCount, Path.Combine(first, name), definition.Indented);
                var b = EnvelopeWriter.Write(definition, DatasetDefinitions.DefaultSeed, definition.DefaultCount, Path.Combine(second, name), definition.Indented);
                PrintFile(a);
                Report(checks, a.Sha256 == b.Sha256, $"same bytes on two runs: {name}", $"{a.Sha256} vs {b.Sha256}");
                files.Add(a);
            }

            Dictionary<string, string>? sums = null;
            if (File.Exists(sumsPath))
            {
                sums = Hashes.ReadSums(sumsPath);
            }
            else
            {
                Report(checks, false, "expected hashes", $"{sumsPath} not found");
            }

            foreach (var file in files)
            {
                var name = Path.GetFileName(file.Path);
                if (sums is not null)
                {
                    var expected = sums.GetValueOrDefault(name);
                    Report(checks, expected == file.Sha256, $"hash equals SHA256SUMS: {name}", $"expected {expected ?? "no entry"}, got {file.Sha256}");
                }
            }

            if (checks.Contains(false))
            {
                Console.WriteLine("Computed SHA256SUMS:");
                Console.Write(Hashes.FormatSums(files.Select(f => KeyValuePair.Create(Path.GetFileName(f.Path), f.Sha256))));
            }

            foreach (var (definition, file) in DatasetDefinitions.All.Zip(files))
            {
                var name = Path.GetFileName(file.Path);
                var report = EnvelopeChecker.Check(file.Path);
                Report(checks, report.Passed, $"structure: {name}", string.Join(" | ", report.Errors));
                report.WriteCounters(Console.Out);
                var (passed, detail) = Characteristics(definition, report);
                Report(checks, passed, $"characteristics: {name}", detail);
            }

            foreach (var definition in DatasetDefinitions.All)
            {
                var generated = Path.Combine(samples, definition.DatasetId, "sample.json");
                Directory.CreateDirectory(Path.GetDirectoryName(generated)!);
                EnvelopeWriter.Write(definition, DatasetDefinitions.DefaultSeed, definition.SampleCount, generated, indented: true);
                var committed = Path.Combine(templates, definition.DatasetId, "sample.json");
                var same = File.Exists(committed) && File.ReadAllBytes(committed).AsSpan().SequenceEqual(File.ReadAllBytes(generated));
                Report(checks, same, $"sample regenerates byte for byte: {definition.DatasetId}/sample.json", File.Exists(committed) ? "content differs" : $"{committed} not found");
            }
        }
        finally
        {
            if (Directory.Exists(temp))
            {
                Directory.Delete(temp, recursive: true);
            }
        }

        PrintPeakWorkingSet();
        var failed = checks.Count(static c => !c);
        Console.WriteLine(failed == 0
            ? string.Create(Inv, $"verify: all {checks.Count} checks PASS")
            : string.Create(Inv, $"verify: {failed} of {checks.Count} checks FAIL"));
        return failed == 0 ? 0 : 1;
    }

    /// <summary><c>check FILE</c>: the streaming structural check of any generated file, with its counters.</summary>
    public static int Check(CommandLine line)
    {
        var path = line.Positional[0];
        var initialBuffer = EnvelopeChecker.DefaultInitialBuffer;
        if (line.Get("--initial-buffer") is { } bufferText
            && (!int.TryParse(bufferText, NumberStyles.None, Inv, out initialBuffer) || initialBuffer < 16))
        {
            throw new UsageException($"--initial-buffer must be a whole number of at least 16, not '{bufferText}'.");
        }

        if (!File.Exists(path))
        {
            throw new FailureException($"File not found: {path}");
        }

        var report = EnvelopeChecker.Check(path, initialBuffer);
        Console.WriteLine(report.Passed ? $"PASS  structure: {Path.GetFileName(path)}" : $"FAIL  structure: {Path.GetFileName(path)}");
        foreach (var error in report.Errors)
        {
            Console.WriteLine("  " + error);
        }

        report.WriteCounters(Console.Out);
        PrintPeakWorkingSet();
        return report.Passed ? 0 : 1;
    }

    /// <summary><c>samples</c>: writes the <c>sample.json</c> of the three sample templates.</summary>
    public static int Samples(CommandLine line)
    {
        var templates = line.Get("--templates") ?? RepositoryPaths.TemplatesFolder(RequireRoot("--templates"));
        foreach (var definition in DatasetDefinitions.All)
        {
            var folder = Path.Combine(templates, definition.DatasetId);
            Directory.CreateDirectory(folder);
            PrintFile(EnvelopeWriter.Write(definition, DatasetDefinitions.DefaultSeed, definition.SampleCount, Path.Combine(folder, "sample.json"), indented: true));
        }

        return 0;
    }

    private static (bool Passed, string Detail) Characteristics(DatasetDefinition definition, EnvelopeReport report)
    {
        var failures = new List<string>();
        void Expect(bool condition, string description)
        {
            if (!condition)
            {
                failures.Add(description);
            }
        }

        switch (definition.Kind)
        {
            case DatasetKind.Simple:
                Expect(report.HostileDescriptions == 2, "exactly 2 hostile descriptions");
                break;
            case DatasetKind.Grouped:
                Expect(report.MinGroupSize == 1, "a group of 1 record");
                Expect(report.MaxGroupSize > 1_000, "a group of more than 1000 records");
                Expect(report.LongDescriptions >= 50, "at least 50 descriptions of 1500 characters or more");
                Expect(report.NegativeAmounts > 0, "negative amounts");
                Expect(report.HostileDescriptions > 0, "hostile descriptions");
                Expect(report.NonAsciiDescriptions > 0, "non-ASCII descriptions");
                break;
            default:
                var share = report.Records == 0 ? 0d : (double)report.UniqueDescriptions / report.Records;
                Expect(share is >= 0.29 and <= 0.31, "29 % to 31 % unique descriptions");
                foreach (var (prefix, occurrences) in report.FormulaLikeByPrefix)
                {
                    Expect(occurrences > 0, $"formula-like descriptions starting with '{prefix}'");
                }

                Expect(report.AccountIds == report.Records && report.ValidAccountIds == report.Records, "every accountId has 20 digits and a leading zero");
                Expect(report.Quantities == report.Records && report.SixteenDigitQuantities == report.Records, "every quantity has 16 significant digits");
                Expect(report.References == report.Records && report.ReferencesAbove2Pow53 == report.Records, "every reference is above 2^53");
                break;
        }

        return (failures.Count == 0, "missing: " + string.Join(", ", failures));
    }

    private static void Report(List<bool> checks, bool passed, string name, string failureDetail)
    {
        checks.Add(passed);
        Console.WriteLine(passed ? $"PASS  {name}" : $"FAIL  {name}: {failureDetail}");
    }

    private static string RequireRoot(string option) =>
        RepositoryPaths.FindRoot()
        ?? throw new FailureException($"No folder containing dotnet-report-service.slnx was found above the current directory; run from inside the repository or pass {option}.");

    private static void PrintFile(GeneratedFile file) =>
        Console.WriteLine(string.Create(Inv, $"{Path.GetFileName(file.Path)}  records={file.Records}  groups={file.Groups}  bytes={file.Bytes}  sha256={file.Sha256}  seconds={file.Seconds:F2}"));

    private static void PrintPeakWorkingSet()
    {
        using var process = Process.GetCurrentProcess();
        Console.WriteLine(string.Create(Inv, $"peakWorkingSetMiB={process.PeakWorkingSet64 / 1048576d:F1}"));
    }
}
