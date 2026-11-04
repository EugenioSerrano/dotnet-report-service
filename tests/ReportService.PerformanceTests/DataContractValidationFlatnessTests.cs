using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using ReportService.Data;
using ReportService.DataContract;
using ReportService.Testing;

namespace ReportService.PerformanceTests;

/// <summary>
/// The cost of the data path with the template's schema on: the reading-and-validation
/// pass over the deterministic volume datasets at one hundred thousand, five hundred
/// thousand and one million records, plain and inside a complete gzip stream, measured
/// three times each and reported as the median. The pass is the product's own — the
/// reader, the contract's two roots and a counting sink in place of the partition, which
/// the first increment's single block cannot hold at these sizes — so nothing here
/// re-implements what it measures.
///
/// Two properties are asserted: the peak working set the pass reaches stays flat as the
/// record count grows — the median at five hundred thousand and at one million within
/// 1.20 × the median at one hundred thousand plus 8 MiB — and the one-million-record gzip
/// run completes under a 128 MiB working set, which is the cap the reference environment
/// gives it (a container capped at 128 MiB, swap off). The per-record cost of the schema
/// evaluation is measured as the difference between the pass with validation on and the
/// same pass with it off, and recorded; it is an observation, not a bound.
///
/// These cases read <c>samples/datasets</c>, which the repository's generator produces and
/// git ignores, and they run on the reference environment only: the runner's hermetic,
/// integration and end-to-end levels do not include this project.
/// </summary>
[Trait("Requires", "reference-environment")]
public sealed class DataContractValidationFlatnessTests
{
    /// <summary>The working set one run of the pass over one million records may reach.</summary>
    private const long WorkingSetCapBytes = 128L * 1024 * 1024;

    /// <summary>The growth one million records may cost over one hundred thousand.</summary>
    private const double GrowthFactor = 1.20;

    /// <summary>What the growth bound adds to the smallest run's working set.</summary>
    private const long GrowthAllowanceBytes = 8L * 1024 * 1024;

    /// <summary>The record counts the measurement runs at, smallest first.</summary>
    public static TheoryData<int> Sizes { get; } = new(100_000, 500_000, 1_000_000);

    [Theory]
    [MemberData(nameof(Sizes))]
    public async Task ThePassKeepsItsWorkingSetFlatAsTheRecordCountGrows(int records)
    {
        var plain = await MeasureAsync(records, compressed: false, validate: true);
        var gzip = await MeasureAsync(records, compressed: true, validate: true);

        Console.WriteLine(Describe(records, "plain", plain));
        Console.WriteLine(Describe(records, "gzip", gzip));

        if (records == 1_000_000)
        {
            Assert.True(
                plain.PeakWorkingSetBytes <= WorkingSetCapBytes && gzip.PeakWorkingSetBytes <= WorkingSetCapBytes,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"the pass at one million records reached {plain.PeakWorkingSetBytes / (1024 * 1024)} MiB plain and {gzip.PeakWorkingSetBytes / (1024 * 1024)} MiB gzip, against the {WorkingSetCapBytes / (1024 * 1024)} MiB cap"));
        }
    }

    [Fact]
    public async Task TheWorkingSetOfOneMillionRecordsIsWithinTheGrowthBoundOfOneHundredThousand()
    {
        var smallest = await MedianWorkingSetAsync(100_000);
        var middle = await MedianWorkingSetAsync(500_000);
        var largest = await MedianWorkingSetAsync(1_000_000);
        var allowed = Math.Max(smallest, (long)(smallest * GrowthFactor)) + GrowthAllowanceBytes;

        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"working set medians: {smallest / (1024 * 1024)} MiB at 100 000, {middle / (1024 * 1024)} MiB at 500 000, {largest / (1024 * 1024)} MiB at 1 000 000; the bound is {allowed / (1024 * 1024)} MiB"));

        Assert.True(
            middle <= allowed,
            string.Create(CultureInfo.InvariantCulture, $"the pass at 500 000 records reached {middle} bytes, over the {allowed} the bound allows"));
        Assert.True(
            largest <= allowed,
            string.Create(CultureInfo.InvariantCulture, $"the pass at 1 000 000 records reached {largest} bytes, over the {allowed} the bound allows"));
    }

    [Fact]
    public async Task ThePerRecordCostOfTheValidationIsRecorded()
    {
        var withoutValidation = await MeasureAsync(1_000_000, compressed: false, validate: false);
        var withValidation = await MeasureAsync(1_000_000, compressed: false, validate: true);
        var extraMilliseconds = withValidation.MedianElapsed.TotalMilliseconds - withoutValidation.MedianElapsed.TotalMilliseconds;
        var extraNanoseconds = extraMilliseconds * 1_000_000 / 1_000_000;
        var extraBytes = (withValidation.AllocatedBytes - withoutValidation.AllocatedBytes) / 1_000_000.0;

        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"per record at one million records: {withoutValidation.MedianElapsed.TotalMilliseconds:F0} ms and {withoutValidation.AllocatedBytes / (1024.0 * 1024.0):F0} MiB reading, {withValidation.MedianElapsed.TotalMilliseconds:F0} ms and {withValidation.AllocatedBytes / (1024.0 * 1024.0):F0} MiB with the schema on; the validation costs about {extraNanoseconds:F0} ns and {extraBytes:F1} B per record"));

        Assert.True(
            withValidation.MedianElapsed > TimeSpan.Zero,
            "the measurement has to take time; a zero is a broken measurement, not a fast pass");
    }

    private static async Task<long> MedianWorkingSetAsync(int records)
    {
        var runs = new List<long>();
        for (var run = 0; run < 3; run++)
        {
            runs.Add((await MeasureAsync(records, compressed: false, validate: true)).PeakWorkingSetBytes);
        }

        runs.Sort();
        return runs[1];
    }

    // One pass over the dataset: the reader, the contract's two roots evaluated on the
    // values the reader yields, and a sink that only counts them. The process's working set
    // is read at its peak after the pass.
    private static async Task<Measurement> MeasureAsync(int records, bool compressed, bool validate)
    {
        var path = DatasetPath(records);
        Assert.True(
            File.Exists(path),
            $"{path} is missing: the measurement runs over the datasets the repository's generator produces (dotnet run --project samples/datasets/ReportService.Samples.DatasetGenerator -c Release -- generate --dataset volume --count {records.ToString(CultureInfo.InvariantCulture)}).");

        using var contract = Lease(validate);
        var sink = new CountingSink();

        // A full collection before the pass, so what it reaches is the pass's own.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        var started = Stopwatch.GetTimestamp();
        await using (var file = File.OpenRead(path))
        {
            await using Stream content = compressed
                ? new GZipStream(file, CompressionMode.Decompress)
                : file;
            await ReadAsync(content, contract, sink, TestContext.Current.CancellationToken);
        }

        var elapsed = Stopwatch.GetElapsedTime(started);
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;

        using var process = Process.GetCurrentProcess();
        process.Refresh();
        var workingSet = Math.Max(process.PeakWorkingSet64, GC.GetTotalMemory(forceFullCollection: false));

        Assert.Equal(records, sink.Records);
        return new Measurement(elapsed, workingSet, allocated);
    }

    private static async Task ReadAsync(
        Stream content,
        PreparedDataContract? contract,
        CountingSink sink,
        CancellationToken cancellationToken)
    {
        await foreach (var value in HeaderItemsReader.ReadAsync(content, cancellationToken))
        {
            if (contract is not null)
            {
                var check = value.Ordinal == 0 ? contract.CheckHeader(value.Json) : contract.CheckRecord(value.Json);
                Assert.True(check.IsValid, string.Create(
                    CultureInfo.InvariantCulture,
                    $"the dataset's value at ordinal {value.Ordinal} has to satisfy the template's schema, and {check.Path} broke {check.Keyword}"));
            }

            sink.Accept(value);
        }
    }

    // The volume template's own schema, compiled once per measurement: the same document
    // the take would read, at the two roots the service evaluates.
    private static PreparedDataContract? Lease(bool validate)
    {
        if (!validate)
        {
            return null;
        }

        var path = Path.Combine(
            RepositoryPaths.Root,
            "samples",
            "templates",
            "pilot-volume",
            "schema.json");
        var compilation = DataContractCompiler.Compile(
            File.ReadAllBytes(path),
            new string('0', 64),
            "schema.json");
        Assert.True(
            compilation.Succeeded,
            $"the pilot-volume schema has to stay inside the profile; it was refused by rule {compilation.Profile.Rule}");
        return compilation.Contract;
    }

    private static string DatasetPath(int records)
    {
        var name = records switch
        {
            100_000 => "header-items-v1.volume.100k.json",
            500_000 => "header-items-v1.volume.500k.json",
            _ => "header-items-v1.volume.1m.json",
        };

        return Path.Combine(RepositoryPaths.Root, "samples", "datasets", name);
    }

    private static string Describe(int records, string flavour, Measurement measurement) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{records:N0} records, {flavour}: {measurement.MedianElapsed.TotalMilliseconds:F0} ms, peak working set {measurement.PeakWorkingSetBytes / (1024 * 1024)} MiB");

    /// <summary>One measurement of one pass: how long it took, how much memory it reached and what it allocated.</summary>
    /// <param name="MedianElapsed">The elapsed time, from one run.</param>
    /// <param name="PeakWorkingSetBytes">The working set the process reached.</param>
    /// <param name="AllocatedBytes">What the pass allocated in total.</param>
    private sealed record Measurement(TimeSpan MedianElapsed, long PeakWorkingSetBytes, long AllocatedBytes);

    /// <summary>The sink the measurement drains the pass into: it counts and releases.</summary>
    private sealed class CountingSink
    {
        public int Records { get; private set; }

        public void Accept(RawRecord value)
        {
            if (value.Ordinal > 0)
            {
                Records++;
            }
        }
    }
}
