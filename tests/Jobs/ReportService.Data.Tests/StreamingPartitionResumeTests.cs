using ReportService.Data;
using ReportService.Data.Partitioning;

namespace ReportService.Data.Tests;

/// <summary>
/// Restart-safe resume during partitioning (US-009 AC-9, partitioning stage; ADR-012
/// §3.5, Block RULE-05). A pass that resumes from the durable checkpoint and the
/// committed blocks completes with a block set and index identical to a clean,
/// no-restart run — no duplicated, lost or reordered record or block — and a
/// speculative block that was never durably committed is discarded and rewritten,
/// never reported as committed. The oracle is the clean run; the resume must match it.
/// </summary>
public sealed class StreamingPartitionResumeTests
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    private static PartitionLimits SmallRecordLimits => new(SoftMaxRecords: 2, SoftMaxRecordBytes: 1_000_000, HardMaxRecords: 3, HardMaxRecordBytes: 1_000_000);

    private static string[] SevenRecords =>
        [.. Enumerable.Range(1, 7).Select(n => PartitionFixtures.GroupedRecord(n, "g" + n, n + ".00"))];

    private static PartitionSpec Spec => PartitionFixtures.GroupedSpec(SmallRecordLimits);

    private static IAsyncEnumerable<RawRecord> Reader(out Stream source)
    {
        source = new MemoryStream(SnapshotBytes.Document(SnapshotBytes.ValidHeader, SevenRecords), writable: false);
        return HeaderItemsReader.ReadAsync(source, Cancellation);
    }

    private static async Task<(StreamingPartition Partition, InMemoryBlockSink Sink, List<PartitionCheckpoint> Checkpoints)> CleanRunAsync()
    {
        var checkpoints = new List<PartitionCheckpoint>();
        var sink = new InMemoryBlockSink();
        var document = Reader(out var source);
        await using (source)
        {
            var partition = await StreamingPartitioner.PartitionAsync(
                document, sink, Spec, Cancellation, onBlockCommitted: checkpoints.Add);
            return (partition, sink, checkpoints);
        }
    }

    [Fact]
    public async Task AResumeFromTheLastCommittedBlockMatchesACleanRunExactly()
    {
        var (clean, cleanSink, checkpoints) = await CleanRunAsync();

        // Model a crash after block 1 is committed: the sink throws when block 2 is opened.
        var crashedSink = new InMemoryBlockSink(throwFromBlock: 2);
        PartitionCheckpoint? lastCommitted = null;
        var crashingDocument = Reader(out var crashingSource);
        await using (crashingSource)
        {
            await Assert.ThrowsAsync<PartitionInterrupted>(() => StreamingPartitioner.PartitionAsync(
                crashingDocument, crashedSink, Spec, Cancellation, onBlockCommitted: c => lastCommitted = c));
        }

        Assert.NotNull(lastCommitted);
        Assert.Equal(1, lastCommitted!.LastCommittedBlockIndex);

        // Resume from the durable checkpoint, with the committed blocks carried forward.
        var resumedSink = new InMemoryBlockSink();
        resumedSink.Seed(crashedSink, throughBlockIndex: 1);
        var resumeDocument = Reader(out var resumeSource);
        StreamingPartition resumed;
        await using (resumeSource)
        {
            resumed = await StreamingPartitioner.PartitionAsync(
                resumeDocument, resumedSink, Spec, Cancellation, resumeFrom: lastCommitted);
        }

        // The resumed index is byte-identical to the clean one, and every block document matches.
        Assert.Equal(BlockIndexPage.Serialize(clean.Index), BlockIndexPage.Serialize(resumed.Index));
        Assert.Equal(cleanSink.BlockIndices, resumedSink.BlockIndices);
        foreach (var index in cleanSink.BlockIndices)
        {
            Assert.Equal(cleanSink.Block(index), resumedSink.Block(index));
        }

        // No record is lost, duplicated or reordered by the restart.
        Assert.Equal(SevenRecords, await PartitionFixtures.ReadAllRecordsAsync(resumedSink, Cancellation));
    }

    [Fact]
    public async Task TheCommittedBlocksAreNotRewrittenByTheResume()
    {
        var (_, cleanSink, checkpoints) = await CleanRunAsync();

        var crashedSink = new InMemoryBlockSink(throwFromBlock: 2);
        PartitionCheckpoint? lastCommitted = null;
        var crashingDocument = Reader(out var crashingSource);
        await using (crashingSource)
        {
            await Assert.ThrowsAsync<PartitionInterrupted>(() => StreamingPartitioner.PartitionAsync(
                crashingDocument, crashedSink, Spec, Cancellation, onBlockCommitted: c => lastCommitted = c));
        }

        // Seed the committed blocks, then resume: the resume must only write blocks after the checkpoint.
        var resumedSink = new InMemoryBlockSink();
        resumedSink.Seed(crashedSink, throughBlockIndex: 1);
        var resumeDocument = Reader(out var resumeSource);
        await using (resumeSource)
        {
            await StreamingPartitioner.PartitionAsync(resumeDocument, resumedSink, Spec, Cancellation, resumeFrom: lastCommitted);
        }

        // Blocks 0 and 1 were carried in and must be byte-for-byte the committed ones (a block is
        // committed only once its index entry with SHA-256 is durable — Block RULE-05).
        Assert.Equal(cleanSink.Block(0), resumedSink.Block(0));
        Assert.Equal(cleanSink.Block(1), resumedSink.Block(1));
    }

    [Fact]
    public async Task ASpeculativeUncommittedBlockIsDiscardedAndRewrittenNeverDuplicated()
    {
        var (clean, cleanSink, checkpoints) = await CleanRunAsync();

        // The checkpoint after block 1 has a durable index of blocks 0..1 only. Model a speculative,
        // never-committed block 2 left in storage from a dead attempt: its bytes differ from the clean one.
        var checkpointAfterBlock1 = checkpoints[1];
        Assert.Equal(1, checkpointAfterBlock1.LastCommittedBlockIndex);

        var resumedSink = new InMemoryBlockSink();
        resumedSink.Seed(cleanSink, throughBlockIndex: 1);
        PlantSpeculativeBlock(resumedSink, blockIndex: 2, bogus: SnapshotBytes.Utf8("""{"contractVersion":"1.0","header":{},"items":[{"stale":true}]}"""));

        var resumeDocument = Reader(out var resumeSource);
        StreamingPartition resumed;
        await using (resumeSource)
        {
            resumed = await StreamingPartitioner.PartitionAsync(
                resumeDocument, resumedSink, Spec, Cancellation, resumeFrom: checkpointAfterBlock1);
        }

        // The speculative block 2 is overwritten with the deterministic one; the final index is the clean index.
        Assert.Equal(BlockIndexPage.Serialize(clean.Index), BlockIndexPage.Serialize(resumed.Index));
        Assert.Equal(cleanSink.Block(2), resumedSink.Block(2));

        // Each block index appears exactly once in the index — nothing duplicated.
        var indices = resumed.Index.Blocks.Select(b => b.BlockIndex).ToArray();
        Assert.Equal(indices.Length, indices.Distinct().Count());
        Assert.Equal(SevenRecords, await PartitionFixtures.ReadAllRecordsAsync(resumedSink, Cancellation));
    }

    // Writes bytes under a block index directly, modelling a block an earlier attempt wrote but never
    // committed to the durable index. The resume treats any block beyond the checkpoint as rewritable.
    private static void PlantSpeculativeBlock(InMemoryBlockSink sink, int blockIndex, byte[] bogus)
    {
        var stream = sink.OpenBlock(blockIndex, out _);
        stream.Write(bogus, 0, bogus.Length);
    }
}
