using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using XlsxWriterSpike.Inspection;
using XlsxWriterSpike.Measurement;
using XlsxWriterSpike.Suite;
using XlsxWriterSpike.Typing;
using XlsxWriterSpike.Writers;

namespace XlsxWriterSpike;

/// <summary>Entry point: parses the command line and dispatches to the command.</summary>
internal static class Program
{
    private const string Usage = """
        Writes a header-items-v1 JSON dataset to XLSX with several writers and measures memory.

        Usage:
          write        --candidate NAME --input FILE --columns FILE --output FILE
                       [--rows N] [--row-limit-policy error|split] [--sheet-row-limit N]
                       [--ignore-header-count]
          typing-check [--work DIR]
          inspect      --input XLSX [--source JSON --columns FILE [--rows N] [--sheet-row-limit N]] [--probes FILE]
          validate     --input XLSX
          suite        --datasets DIR --results DIR [--work DIR] [--sizes 100k,500k,1m]
                       [--candidates LIST] [--repetitions 3]

        Candidates: miniexcel, openxml-default, openxml-writeonly, openxml-zip, read-only.
        Default --work: <temp>/xlsx-writer-spike/work.
        Exit codes: 0 success, 1 failure, 2 invalid arguments, 3 OUTPUT_LIMIT_EXCEEDED.
        """;

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

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
                "write" => Write(Options.Parse(args, ["--candidate", "--input", "--columns", "--output", "--rows", "--row-limit-policy", "--sheet-row-limit"], ["--ignore-header-count"])),
                "typing-check" => TypingCheck.Run(Options.Parse(args, ["--work"], []).Get("--work") ?? DefaultWork(), Console.Out),
                "inspect" => Inspect(Options.Parse(args, ["--input", "--source", "--columns", "--rows", "--sheet-row-limit", "--probes"], [])),
                "validate" => Validate(Options.Parse(args, ["--input"], [])),
                "suite" => RunSuite(Options.Parse(args, ["--datasets", "--results", "--work", "--sizes", "--candidates", "--repetitions"], [])),
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
        catch (Exception exception) when (exception is SpikeException or IOException or InvalidDataException or UnauthorizedAccessException or JsonException or System.Xml.XmlException)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }

    private static string DefaultWork() => Path.Combine(Path.GetTempPath(), "xlsx-writer-spike", "work");

    private static int Write(Options options)
    {
        var request = new WriteRequest(
            options.Required("--candidate"),
            options.Required("--input"),
            ColumnProfile.Load(options.Required("--columns")),
            options.Get("--output") ?? string.Empty,
            options.Long("--rows"),
            options.Get("--row-limit-policy") switch
            {
                null or "error" => RowLimitPolicy.Error,
                "split" => RowLimitPolicy.Split,
                var other => throw new UsageException($"--row-limit-policy must be 'error' or 'split', not '{other}'."),
            },
            options.Long("--sheet-row-limit") ?? SheetPlan.MaxDataRowsPerSheet,
            options.Flag("--ignore-header-count"));
        if (request.Candidate != "read-only" && request.OutputPath.Length == 0)
        {
            throw new UsageException("--output is required for a writing candidate.");
        }

        var measureTemp = Environment.GetEnvironmentVariable("XLSX_SPIKE_MEASURE_TEMP") == "1" ? Path.GetTempPath() : null;
        var clock = Stopwatch.StartNew();
        WriteOutcome outcome;
        long heapPeak;
        long? tempPeak;
        using (var sampler = new MemorySampler(measureTemp))
        {
            outcome = WriteRunner.Execute(request);
            clock.Stop();
            sampler.Stop();
            heapPeak = sampler.HeapPeakBytes;
            tempPeak = sampler.TempPeakBytes;
        }

        var seconds = clock.Elapsed.TotalSeconds;
        var result = new RunResult
        {
            Candidate = request.Candidate,
            Profile = request.Profile.Name,
            Dataset = Path.GetFileName(request.InputPath),
            RowsWritten = outcome.ExitCode == 0 ? outcome.RowsWritten : 0,
            Sheets = outcome.ExitCode == 0 ? outcome.Sheets : 0,
            Outcome = outcome.Outcome,
            ErrorCode = outcome.LimitError is null ? (outcome.ExitCode == 0 ? null : "failed") : OutputLimitExceededException.ErrorCode,
            ErrorReason = outcome.LimitError?.Reason,
            ErrorRow = outcome.LimitError?.Row,
            ErrorColumn = outcome.LimitError?.Column,
            ErrorMessage = outcome.Message,
            ElapsedSeconds = Math.Round(seconds, 3),
            RowsPerSecond = outcome.ExitCode == 0 && seconds > 0 ? Math.Round(outcome.RowsWritten / seconds, 1) : 0,
            ManagedHeapPeakMiB = RunResult.MiB(heapPeak),
            ProcessPeakRssMiB = RunResult.MiB(MemorySampler.ProcessPeakRssBytes()),
            ChromiumTreePeakRssMiB = null,
            TempDiskPeakMiB = tempPeak is null ? null : RunResult.MiB(tempPeak.Value),
            OutputMiB = RunResult.MiB(outcome.OutputBytes),
            AllocatedMiB = RunResult.MiB(GC.GetTotalAllocatedBytes(true)),
            Gen2Collections = GC.CollectionCount(2),
            SourceEnumerations = outcome.SourceEnumerations,
        };

        if (outcome.ExitCode != 0)
        {
            Console.Error.WriteLine(outcome.LimitError?.Message ?? $"error code=failed message={outcome.Message} type={outcome.ExceptionType}");
        }

        Console.WriteLine(result.ToJson());
        return outcome.ExitCode;
    }

    private static int Inspect(Options options)
    {
        var input = options.Required("--input");
        RecordSource? source = null;
        ColumnProfile? profile = null;
        if (options.Get("--source") is { } sourcePath)
        {
            profile = ColumnProfile.Load(options.Required("--columns"));
            source = new RecordSource(sourcePath, profile);
        }

        var report = Inspector.Inspect(input, source, options.Long("--rows"), options.Long("--sheet-row-limit") ?? SheetPlan.MaxDataRowsPerSheet);
        foreach (var line in report.Lines())
        {
            Console.WriteLine(line);
        }

        if (options.Get("--probes") is { } probes)
        {
            if (profile is null)
            {
                throw new UsageException("--probes needs --source and --columns.");
            }

            Inspector.WriteProbes(report, profile, probes);
            Console.WriteLine($"  probes: {report.Probes.Count} written to {Path.GetFileName(probes)}");
        }

        var ok = report.Formulas == 0 && (!report.Compared || (report.Mismatches == 0 && report.HeadersOk && report.SequenceContinuous));
        Console.WriteLine($"{(ok ? "PASS" : "FAIL")} inspect {report.File}");
        return ok ? 0 : 1;
    }

    private static int Validate(Options options)
    {
        var input = options.Required("--input");
        var (count, first) = PackageValidator.Validate(input);
        Console.WriteLine($"{(count == 0 ? "PASS" : "FAIL")} validate file={Path.GetFileName(input)} errors={count}");
        foreach (var error in first)
        {
            Console.WriteLine("  " + error);
        }

        return count == 0 ? 0 : 1;
    }

    private static int RunSuite(Options options)
    {
        var sizes = (options.Get("--sizes") ?? "100k,500k,1m").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var candidates = (options.Get("--candidates") ?? string.Join(',', Candidates.Names)).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var candidate in candidates)
        {
            _ = Candidates.Create(candidate);
        }

        var repetitions = (int)(options.Long("--repetitions") ?? 3);
        return SuiteRunner.Run(
            options.Required("--datasets"),
            options.Required("--results"),
            options.Get("--work") ?? DefaultWork(),
            sizes,
            candidates,
            repetitions,
            Console.Out);
    }

    /// <summary>Parsed <c>--name value</c> options and <c>--flag</c> switches of one command.</summary>
    private sealed class Options
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);
        private readonly HashSet<string> _flags = new(StringComparer.Ordinal);

        public static Options Parse(string[] args, string[] valued, string[] flags)
        {
            var options = new Options();
            for (var i = 1; i < args.Length; i++)
            {
                var name = args[i];
                if (flags.Contains(name))
                {
                    options._flags.Add(name);
                }
                else if (valued.Contains(name))
                {
                    if (i + 1 >= args.Length)
                    {
                        throw new UsageException($"Option '{name}' needs a value.");
                    }

                    if (!options._values.TryAdd(name, args[++i]))
                    {
                        throw new UsageException($"Option '{name}' is given more than once.");
                    }
                }
                else
                {
                    throw new UsageException($"Unknown argument '{name}' for '{args[0]}'.");
                }
            }

            return options;
        }

        public string? Get(string name) => _values.GetValueOrDefault(name);

        public string Required(string name) => Get(name) ?? throw new UsageException($"Option '{name}' is required.");

        public bool Flag(string name) => _flags.Contains(name);

        public long? Long(string name)
        {
            if (Get(name) is not { } text)
            {
                return null;
            }

            return long.TryParse(text, NumberStyles.None, Inv, out var value) && value > 0
                ? value
                : throw new UsageException($"Option '{name}' must be a positive whole number, not '{text}'.");
        }
    }
}
