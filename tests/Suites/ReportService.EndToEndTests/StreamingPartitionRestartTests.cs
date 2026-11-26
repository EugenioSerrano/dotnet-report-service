using ReportService.Data;
using ReportService.Data.Partitioning;
using ReportService.Storage;

namespace ReportService.EndToEndTests;

/// <summary>
/// A forced mid-partition restart against real object storage (US-009 AC-9,
/// partitioning stage; ADR-012 §3.5, Block RULE-05). A large snapshot is partitioned,
/// the driver is interrupted after a committed block, and the partition is resumed from
/// the durable oracle — the committed block objects, the durable block index and the
/// last returned checkpoint. The resumed partition is field-identical to a clean,
/// no-restart run — same block count, ordinals, counts, digests, continuity, layout and
/// opening carried-forward — with each record present exactly once. The attempt-scoped
/// storage key is the one field free to differ across attempts; the block content it
/// points at is compared instead. Authored in the V-Bounce; run in the e2e cycle.
/// </summary>
public sealed class StreamingPartitionRestartTests
{
    private const string JobId = "0123456789abcdef0123456789abcdef";

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    private static PartitionSpec GroupedSpec => new("group", "amount", PartitionLimits.Default);

    [Fact]
    public async Task APartitionResumedAfterAForcedRestartIsFieldIdenticalToACleanRun()
    {
        using var dataset = StreamingPartitionDataset.Grouped(recordCount: 5_000, groupSize: 250, largeGroupSize: 2_500, gzip: false);

        // A clean, no-restart run is the oracle.
        using var cleanStorage = TemporaryObjectStore.Open();
        var cleanDocument = dataset.Read(Cancellation, out var cleanHandle);
        DurablePartition clean;
        using (cleanHandle)
        {
            clean = await new DurablePartitionDriver(cleanStorage.Store, GroupedSpec)
                .RunAsync(JobId, cleanDocument, resumeFrom: null, Cancellation);
        }

        Assert.True(clean.Index.Blocks.Count >= 3, "the oracle run has several blocks so a mid-partition restart is meaningful");

        // The interrupted run shares one store with its resume: committed blocks stay durable across the restart.
        using var storage = TemporaryObjectStore.Open();
        var driver = new DurablePartitionDriver(storage.Store, GroupedSpec);

        // Interrupt after the second block is durably committed, capturing its checkpoint.
        PartitionCheckpoint? resumeFrom = null;
        var interruptAfter = 2;
        var committed = 0;
        var interruptedDocument = dataset.Read(Cancellation, out var interruptedHandle);
        using (interruptedHandle)
        {
            await Assert.ThrowsAnyAsync<Exception>(() => driver.RunAsync(
                JobId,
                interruptedDocument,
                resumeFrom: null,
                Cancellation,
                onBlockCommitted: checkpoint =>
                {
                    resumeFrom = checkpoint;
                    if (++committed >= interruptAfter)
                    {
                        throw new RestartForced();
                    }
                }));
        }

        Assert.NotNull(resumeFrom);
        Assert.Equal(interruptAfter - 1, resumeFrom!.LastCommittedBlockIndex);

        // Resume from the durable checkpoint and the committed blocks already in the store.
        var resumeDocument = dataset.Read(Cancellation, out var resumeHandle);
        DurablePartition resumed;
        using (resumeHandle)
        {
            resumed = await new DurablePartitionDriver(storage.Store, GroupedSpec)
                .RunAsync(JobId, resumeDocument, resumeFrom, Cancellation);
        }

        // The resumed index is field-identical to the clean one (the storage key is the only field free to differ).
        Assert.Equal(clean.Index.ChunkLayout, resumed.Index.ChunkLayout);
        Assert.Equal(clean.Index.Blocks.Count, resumed.Index.Blocks.Count);
        for (var i = 0; i < clean.Index.Blocks.Count; i++)
        {
            var a = clean.Index.Blocks[i];
            var b = resumed.Index.Blocks[i];
            Assert.Equal(a.BlockIndex, b.BlockIndex);
            Assert.Equal(a.FirstRecordOrdinal, b.FirstRecordOrdinal);
            Assert.Equal(a.RecordCount, b.RecordCount);
            Assert.Equal(a.RecordBytes, b.RecordBytes);
            Assert.Equal(a.Sha256, b.Sha256);
            Assert.Equal(a.SizeBytes, b.SizeBytes);
            Assert.Equal(a.ContinuesPreviousGroup, b.ContinuesPreviousGroup);
            Assert.Equal(a.ContinuesNextGroup, b.ContinuesNextGroup);
            Assert.Equal(a.OpeningGroupKey, b.OpeningGroupKey);
            Assert.Equal(a.OpeningCarriedForward, b.OpeningCarriedForward);
            Assert.Equal(a.BlockAmountTotal, b.BlockAmountTotal);

            // The block the resumed index points at is durable and holds the same records as the clean one.
            await using var read = await OpenDurableAsync(storage.Store, b, Cancellation);
            var records = await ReadRecordTextsAsync(read.Content, Cancellation);
            await using var cleanRead = await OpenDurableAsync(cleanStorage.Store, a, Cancellation);
            var cleanRecords = await ReadRecordTextsAsync(cleanRead.Content, Cancellation);
            Assert.Equal(cleanRecords, records);
        }

        // Every block index appears exactly once — nothing duplicated or lost by the restart.
        var indices = resumed.Index.Blocks.Select(x => x.BlockIndex).ToArray();
        Assert.Equal(indices.Length, indices.Distinct().Count());
        Assert.Equal([.. Enumerable.Range(0, indices.Length)], indices);
    }

    private static async Task<VerifiedRead> OpenDurableAsync(IObjectStore store, BlockIndexEntry block, CancellationToken cancellationToken)
    {
        var result = await store.OpenReadAsync(ObjectKey.Create(block.Key), block.SizeBytes, block.Sha256, cancellationToken);
        Assert.True(result.Succeeded, "the committed block object is durable and verifies against its recorded size and digest");
        return result.Value!;
    }

    private static async Task<List<string>> ReadRecordTextsAsync(Stream block, CancellationToken cancellationToken)
    {
        var records = new List<string>();
        await foreach (var value in HeaderItemsReader.ReadAsync(block, cancellationToken))
        {
            if (value.Ordinal > 0)
            {
                records.Add(System.Text.Encoding.UTF8.GetString(value.Json.Span));
            }
        }

        return records;
    }

    /// <summary>The sentinel that forces the modelled mid-partition restart.</summary>
    private sealed class RestartForced() : Exception("forced mid-partition restart");
}
