using ReportService.Data;
using ReportService.Data.Partitioning;
using ReportService.Storage;

namespace ReportService.EndToEndTests;

/// <summary>
/// The streaming partition at scale, against real object storage (US-009 AC-2, AC-7;
/// boundary AC-4). A large multi-block snapshot — ordinary and grouped, plain and gzip —
/// is driven through the durable partition driver against the filesystem object store;
/// the committed block objects and the durable block index are read back and verified:
/// block count, per-block digest and size against the stored object, ordinal contiguity,
/// page-boundary layout, group continuity, and the opening carried-forward against an
/// independently computed running sum. The expectations are computed here, never read
/// from the partitioner. Authored in the V-Bounce; run in the e2e cycle.
/// </summary>
public sealed class StreamingPartitionAtScaleTests
{
    // 32 lower-case hex characters: a valid object-key segment for the job the blocks live under.
    private const string JobId = "0123456789abcdef0123456789abcdef";

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    private static PartitionSpec GroupedSpec => new("group", "amount", PartitionLimits.Default);

    private static PartitionSpec PlainSpec => new(GroupKeyField: null, "amount", PartitionLimits.Default);

    [Fact]
    public async Task AGroupedPlainSnapshotPartitionsIntoAVerifiedDurableMultiBlockIndex()
    {
        using var dataset = StreamingPartitionDataset.Grouped(recordCount: 5_000, groupSize: 250, largeGroupSize: 2_500, gzip: false);
        await RunAndVerifyAsync(dataset, GroupedSpec);
    }

    [Fact]
    public async Task AGroupedGzipSnapshotPartitionsIntoAVerifiedDurableMultiBlockIndex()
    {
        using var dataset = StreamingPartitionDataset.Grouped(recordCount: 5_000, groupSize: 250, largeGroupSize: 2_500, gzip: true);
        await RunAndVerifyAsync(dataset, GroupedSpec);
    }

    [Fact]
    public async Task AnOrdinaryPlainSnapshotPartitionsIntoAVerifiedDurableMultiBlockIndex()
    {
        using var dataset = StreamingPartitionDataset.Plain(recordCount: 4_000, gzip: false);
        await RunAndVerifyAsync(dataset, PlainSpec);
    }

    [Fact]
    public async Task AnOrdinaryGzipSnapshotPartitionsIntoAVerifiedDurableMultiBlockIndex()
    {
        using var dataset = StreamingPartitionDataset.Plain(recordCount: 4_000, gzip: true);
        await RunAndVerifyAsync(dataset, PlainSpec);
    }

    private static async Task RunAndVerifyAsync(StreamingPartitionDataset dataset, PartitionSpec spec)
    {
        using var storage = TemporaryObjectStore.Open();
        var driver = new DurablePartitionDriver(storage.Store, spec);

        var document = dataset.Read(Cancellation, out var handle);
        DurablePartition partition;
        using (handle)
        {
            partition = await driver.RunAsync(JobId, document, resumeFrom: null, Cancellation);
        }

        var blocks = partition.Index.Blocks;

        // The snapshot is far larger than one block, so it must have cut into several.
        Assert.True(blocks.Count >= 2, "a snapshot far larger than one block partitions into several blocks");

        // The page-boundary layout is recorded.
        Assert.Equal("page-boundary", partition.Index.ChunkLayout);

        // Block indices 0..N contiguous; record ordinals contiguous across blocks; every block within the limits.
        var expectedFirst = 1;
        long totalRecords = 0;
        for (var i = 0; i < blocks.Count; i++)
        {
            var block = blocks[i];
            Assert.Equal(i, block.BlockIndex);
            Assert.Equal(expectedFirst, block.FirstRecordOrdinal);
            Assert.True(block.RecordCount >= 1, "no block of a non-empty snapshot is empty");
            Assert.True(block.RecordCount <= PartitionLimits.Default.HardMaxRecords, "a block is within the hard record maximum");
            Assert.True(block.RecordBytes <= PartitionLimits.Default.HardMaxRecordBytes, "a block is within the hard byte maximum");

            // The opening carried-forward is the exact running sum over every record in earlier blocks.
            Assert.Equal(dataset.CarriedForwardBefore(block.FirstRecordOrdinal - 1), block.OpeningCarriedForward);

            // Group continuity is consistent across the cut.
            if (block.ContinuesNextGroup)
            {
                Assert.True(blocks[i + 1].ContinuesPreviousGroup, "a group carried out of a block is carried into the next");
                Assert.Equal(block.OpeningGroupKeyAtEnd(dataset), blocks[i + 1].OpeningGroupKey);
            }

            expectedFirst += (int)block.RecordCount;
            totalRecords += block.RecordCount;

            // The block object is durable and verifies against the digest and size the index records.
            await using var read = await OpenDurableAsync(storage.Store, block, Cancellation);
            var recordsInBlock = await CountRecordsAsync(read.Content, Cancellation);
            Assert.Equal(block.RecordCount, recordsInBlock);
        }

        // Every record is accounted for, exactly once.
        Assert.Equal(dataset.RecordCount, totalRecords);

        // A grouped snapshot with a group of 2 500 records must have split that group across a block
        // (the hard maximum is 2 000), so at least one continuity edge is present.
        if (spec.GroupKeyField is not null)
        {
            Assert.Contains(blocks, b => b.ContinuesNextGroup);
        }
    }

    private static async Task<VerifiedRead> OpenDurableAsync(IObjectStore store, BlockIndexEntry block, CancellationToken cancellationToken)
    {
        var result = await store.OpenReadAsync(ObjectKey.Create(block.Key), block.SizeBytes, block.Sha256, cancellationToken);
        Assert.True(result.Succeeded, "the committed block object is durable and opens against its recorded size and digest");
        return result.Value!;
    }

    private static async Task<long> CountRecordsAsync(Stream block, CancellationToken cancellationToken)
    {
        long records = 0;
        await foreach (var value in HeaderItemsReader.ReadAsync(block, cancellationToken))
        {
            if (value.Ordinal > 0)
            {
                records++;
            }
        }

        return records;
    }
}

/// <summary>Small oracle extensions the scale class reads its expectations through.</summary>
internal static class BlockIndexEntryOracle
{
    /// <summary>The group key open at the end of <paramref name="block"/>, from the dataset's own groups.</summary>
    public static string? OpeningGroupKeyAtEnd(this BlockIndexEntry block, StreamingPartitionDataset dataset)
    {
        var lastOrdinal = block.FirstRecordOrdinal + (int)block.RecordCount - 1;
        return lastOrdinal >= 1 && lastOrdinal <= dataset.Groups.Count ? dataset.Groups[lastOrdinal - 1] : null;
    }
}
