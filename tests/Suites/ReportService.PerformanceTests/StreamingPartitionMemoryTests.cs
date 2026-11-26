using System.Diagnostics;
using System.Globalization;
using System.Text;
using ReportService.Data;
using ReportService.Data.Partitioning;

namespace ReportService.PerformanceTests;

/// <summary>
/// The flat-memory profile of the streaming partition pass at scale.
/// Partitioning a snapshot far larger than one block must keep the data
/// worker's working set flat relative to the dataset size — it holds only the header,
/// the current record and the open block, never the collection. A pass over a dataset
/// many times larger grows the working set no more than a small allowance over a pass
/// over a small one; a pass that materialised the records would grow with the dataset.
/// Kept out of the hermetic inner loop and run at the performance level. Authored in
/// the V-Bounce; run in the performance cycle.
/// </summary>
[Collection(nameof(MemoryMeasured))]
public sealed class StreamingPartitionMemoryTests : IDisposable
{
    private const long MemoryAllowance = 32L * 1024 * 1024;
    private const int SmallRecords = 2_000;
    private const int LargeRecords = 400_000;

    private readonly List<string> _files = [];

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    private static PartitionSpec Spec => new(GroupKeyField: null, "amount", PartitionLimits.Default);

    [Fact]
    public async Task ThePartitionPassKeepsAFlatMemoryProfileAsTheDatasetGrows()
    {
        var small = await MeasurePartitionAsync(SmallRecords);
        var large = await MeasurePartitionAsync(LargeRecords);

        var figure = string.Create(
            CultureInfo.InvariantCulture,
            $"Peak working-set growth: {SmallRecords} records {small.Growth / 1024.0 / 1024.0:F1} MiB in {small.Elapsed.TotalSeconds:F1} s; " +
            $"{LargeRecords} records {large.Growth / 1024.0 / 1024.0:F1} MiB in {large.Elapsed.TotalSeconds:F1} s; " +
            $"bound: {LargeRecords} growth <= {SmallRecords} growth + 32 MiB.");
        TestContext.Current.TestOutputHelper?.WriteLine(figure);
        TestContext.Current.AddAttachment("partition-memory-figure", figure);

        // The large pass partitions into many blocks, proving it really reached scale.
        Assert.True(large.BlockCount >= 2, "the large pass partitions into several blocks");
        Assert.True(large.Growth <= small.Growth + MemoryAllowance, figure);
    }

    private async Task<(long Growth, TimeSpan Elapsed, int BlockCount)> MeasurePartitionAsync(int recordCount)
    {
        var path = WriteSnapshot(recordCount);

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

        var elapsed = Stopwatch.StartNew();
        int blockCount;
        await using (var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var sink = new NullBlockSink();
            var partition = await StreamingPartitioner.PartitionAsync(
                HeaderItemsReader.ReadAsync(source, Cancellation),
                sink,
                Spec,
                Cancellation);
            blockCount = partition.Index.Blocks.Count;
        }

        elapsed.Stop();
        await stop.CancelAsync();
        await sampler;
        peak = Math.Max(peak, Environment.WorkingSet);

        return (peak - baseline, elapsed.Elapsed, blockCount);
    }

    private string WriteSnapshot(int recordCount)
    {
        var path = Path.Combine(Path.GetTempPath(), "streaming-partition-perf", Guid.NewGuid().ToString("N") + ".json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _files.Add(path);

        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var writer = new StreamWriter(file, new UTF8Encoding(false));
        writer.Write("""{"contractVersion":"1.0","header":{"title":"Large statement","currency":"EUR"},"items":[""");
        for (var seq = 1; seq <= recordCount; seq++)
        {
            if (seq > 1)
            {
                writer.Write(',');
            }

            var amount = (seq % 97 + 1) / 100m;
            writer.Write(string.Create(CultureInfo.InvariantCulture, $$"""{"sequence":{{seq}},"amount":{{amount}}}"""));
        }

        writer.Write("]}");
        return path;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        foreach (var path in _files)
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
                // Left for the operating system to sweep; cleanup never fails a run.
            }
        }

        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// A block sink that discards the block documents, so the measurement reflects the
/// partitioner's own working set — the header, the current record and the open block —
/// and not the bytes of the blocks it writes.
/// </summary>
internal sealed class NullBlockSink : IBlockSink
{
    /// <inheritdoc />
    public Stream OpenBlock(int blockIndex, out string key)
    {
        key = string.Create(CultureInfo.InvariantCulture, $"block/job-0123456789abcdef0123456789abcdef/{blockIndex}/aa");
        return Stream.Null;
    }
}
