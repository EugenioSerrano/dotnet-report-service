using System.Diagnostics;
using System.Globalization;
using ReportService.Data;
using ReportService.Testing.Limits;

namespace ReportService.PerformanceTests;

/// <summary>
/// What a refused report costs: the data pass over the snapshot of fifty thousand records,
/// which stops at the first record over the limit - here the thousandth and first - so the
/// measurement is of the refusal itself and never of a document the pass read to its end. The
/// wall time and the peak working set of the process are reported as the observation they are:
/// this class commits to no latency and no memory budget, because the product commits to none
/// for a refusal. It records the numbers a later change can compare against, nothing more.
/// <para>
/// The pass is the product's own partitioner, reading a generated snapshot of the boundary
/// family the fixture library owns, with the limit the printing workers run with. The class runs
/// on the reference environment only: the runner's other levels do not include this project.
/// </para>
/// </summary>
[Trait("Requires", "reference-environment")]
public sealed class SingleBlockLimitRefusalMeasurementTests
{
    /// <summary>The record count the case configures, as the printing workers do.</summary>
    private const int ConfiguredRecords = 1_000;

    [Fact]
    public async Task TheRefusalOfFiftyThousandRecordsStopsAtTheFirstRecordOverTheLimitAndIsMeasured()
    {
        var document = LimitSnapshots.RecordsBeyondBoth();
        var limit = new SingleBlockLimit(ConfiguredRecords);
        using var output = new MemoryStream();

        // The process's own peak, before and after: the difference is what this pass added, and
        // the pass never holds the document - it reads record by record into one block it
        // refuses before writing anything.
        var peakBefore = Process.GetCurrentProcess().PeakWorkingSet64;
        var stopwatch = Stopwatch.StartNew();
        var refusal = await RefuseAsync(document, limit, output);
        stopwatch.Stop();
        var peakAfter = Process.GetCurrentProcess().PeakWorkingSet64;

        Assert.Equal(DataContractViolation.QuotaExceeded, refusal.Code);
        Assert.Equal("block", refusal.Limit);
        Assert.Equal("dimension=records applied=1000", refusal.Reason);
        Assert.Equal(1_001, refusal.Ordinal);
        Assert.Equal(0, output.Length);

        var observation = string.Create(
            CultureInfo.InvariantCulture,
            $"single-block refusal, {ConfiguredRecords} configured records: "
            + $"wall={stopwatch.Elapsed.TotalMilliseconds:0.0} ms, "
            + $"peak working set before={peakBefore / (1024 * 1024)} MiB after={peakAfter / (1024 * 1024)} MiB, "
            + $"reason={refusal.Message}");
        Console.WriteLine(observation);

        // The observation is kept with the run when the run has a folder of its own, so a later
        // change compares against a number rather than a memory of one.
        if (ReportService.Testing.RunEnvironment.RunDirectory is not null)
        {
            ReportService.Testing.RunEnvironment.WriteEvidence(
                "single-block-refusal.measurement.txt",
                System.Text.Encoding.UTF8.GetBytes(observation + Environment.NewLine));
        }
    }

    private static async Task<DataContractViolation> RefuseAsync(
        byte[] document,
        SingleBlockLimit limit,
        Stream sink)
    {
        try
        {
            // The pass reads the document the way it always does - the delivered reader, one
            // record at a time - so what is measured is the delivered pass and not a second one.
            await SingleBlockPartitioner.PartitionAsync(
                HeaderItemsReader.ReadAsync(new MemoryStream(document, writable: false)),
                sink,
                limit: limit);
        }
        catch (DataContractViolation refusal)
        {
            return refusal;
        }

        throw new InvalidOperationException("the snapshot had to be refused");
    }
}
