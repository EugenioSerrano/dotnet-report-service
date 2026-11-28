using System.Globalization;
using System.IO.Compression;
using System.Text;
using ReportService.Data;
using ReportService.Data.Partitioning;
using ReportService.DataContract;
using ReportService.DataReaderProbe;

namespace ReportService.PerformanceTests;

/// <summary>
/// The hostile inputs of the data pass at the two-mebibyte record bound, in-process: each ends
/// with its stable code and limit or reason - or, for the admissible cases, is read whole - with
/// no out-of-memory and no stack overflow. The test host's own footprint is not the pass's, so
/// the memory assertion here is the growth of the working set over a baseline taken after a
/// warm-up pass of the same case; the absolute peak of the process is asserted in the
/// memory-capped container. Baseline, peak and growth are recorded for every case.
/// </summary>
[Collection(nameof(MemoryMeasured))]
public sealed class DataLimitsAtTwoMebibytesMemoryTests : IDisposable
{
    private const long GrowthAllowance = 32L * 1024 * 1024;

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "data-limits-2mib", Guid.NewGuid().ToString("N"));

    public DataLimitsAtTwoMebibytesMemoryTests() => Directory.CreateDirectory(_folder);

    public static TheoryData<string> Cases => new(HostileCases.All.Select(hostile => hostile.Name));

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task EachInputEndsAsExpectedAndTheWorkingSetGrowsWithinTheAllowance(string name)
    {
        var hostile = HostileCases.All.Single(candidate => candidate.Name == name);
        var path = Path.Combine(_folder, hostile.FileName);
        hostile.Write(path);

        // The warm-up pass: code paths jitted and buffers sized before the baseline is read.
        await RunAsync(path, hostile.WithSchema);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var baseline = Environment.WorkingSet;
        var peak = baseline;
        using var stop = new CancellationTokenSource();
        var sampler = Task.Factory.StartNew(
            () =>
            {
                while (!stop.IsCancellationRequested)
                {
                    peak = Math.Max(peak, Environment.WorkingSet);
                    Thread.Sleep(2);
                }
            },
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

        var outcome = await RunAsync(path, hostile.WithSchema);

        await stop.CancelAsync();
        await sampler;
        peak = Math.Max(peak, Environment.WorkingSet);

        var growth = peak - baseline;
        var figure = string.Create(
            CultureInfo.InvariantCulture,
            $"{hostile.Name}: outcome={outcome.Code ?? "ok"} {outcome.Token} baseline={baseline / 1048576.0:F1} MiB peak={peak / 1048576.0:F1} MiB growth={growth / 1048576.0:F1} MiB (allowance 32 MiB)");
        TestContext.Current.TestOutputHelper?.WriteLine(figure);
        TestContext.Current.AddAttachment("data-limits-" + hostile.Name, figure);

        Assert.Equal(hostile.ExpectedCode, outcome.Code);
        if (hostile.ExpectedLimitOrReason is not null)
        {
            Assert.Equal(hostile.ExpectedLimitOrReason, outcome.Token);
        }

        Assert.True(growth <= GrowthAllowance, figure);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temporary folder is not a test failure.
        }
    }

    private static async Task<(string? Code, string? Token)> RunAsync(string path, bool withSchema)
    {
        using var contract = withSchema
            ? DataContractCompiler.Compile(Encoding.UTF8.GetBytes(HostileCases.PatternSchema), "perf", "schema.json").Contract
            : null;
        try
        {
            await using var file = File.OpenRead(path);
            await using Stream source = path.EndsWith(".gz", StringComparison.Ordinal) ? new GZipStream(file, CompressionMode.Decompress) : file;
            var values = HeaderItemsReader.ReadAsync(source, TestContext.Current.CancellationToken);
            await StreamingPartitioner.PartitionAsync(
                Validated(values, contract),
                new NullBlockSink(),
                new PartitionSpec(GroupKeyField: null, AmountField: null, PartitionLimits.Default),
                TestContext.Current.CancellationToken);
            return (null, null);
        }
        catch (DataContractViolation violation)
        {
            var token = violation.Limit is not null ? "limit=" + violation.Limit : "reason=" + violation.Reason;
            return (violation.Code, token);
        }
    }

    private static async IAsyncEnumerable<RawRecord> Validated(IAsyncEnumerable<RawRecord> values, PreparedDataContract? contract)
    {
        await foreach (var value in values)
        {
            if (contract is not null && value.Ordinal > 0)
            {
                var check = contract.CheckRecord(value.Json);
                if (!check.IsValid)
                {
                    throw new DataContractViolation(DataContractViolation.DataInvalid, "schema-violation", null, value.Ordinal, value.Offset, check.Path, check.Keyword);
                }
            }

            yield return value;
        }
    }
}
