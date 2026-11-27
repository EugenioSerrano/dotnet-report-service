using ReportService.Data;
using ReportService.Data.Partitioning;

namespace ReportService.Data.Tests;

/// <summary>
/// The streaming partitioner: one sequential pass that cuts a validated record stream
/// into contiguous, ordered blocks within the soft and hard limits, prefers group
/// boundaries, carries continuity metadata across a group that spans a cut, lays the
/// blocks out page-boundary, records a durable ordered index, and computes the opening
/// carried-forward from previous records only. The expectations are derived from
/// the large-report story's block-limit, group-boundary and carried-forward rules —
/// never from the partitioner. The cases drive small test limits so the cut logic is exercised
/// cheaply; production runs on <see cref="PartitionLimits.Default"/>.
/// </summary>
public sealed class StreamingPartitionerTests
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    // Soft target 2 records, hard maximum 3 records; bytes left wide so the record count drives the cuts.
    private static PartitionLimits SmallRecordLimits => new(SoftMaxRecords: 2, SoftMaxRecordBytes: 1_000_000, HardMaxRecords: 3, HardMaxRecordBytes: 1_000_000);

    // Hard byte maximum 40 bytes — below two ordinary records (~28 bytes each) but above one, so each
    // ordinary record fills a block of its own by the hard byte rule; records drive nothing.
    private static PartitionLimits SmallByteLimits => new(SoftMaxRecords: 1_000_000, SoftMaxRecordBytes: 20, HardMaxRecords: 1_000_000, HardMaxRecordBytes: 40);

    [Fact]
    public async Task TheRecordsAreCutIntoContiguousOrderedBlocksWithNoGapOrOverlap()
    {
        var records = new[]
        {
            PartitionFixtures.GroupedRecord(1, "a", "1.00"),
            PartitionFixtures.GroupedRecord(2, "a", "2.00"),
            PartitionFixtures.GroupedRecord(3, "b", "3.00"),
            PartitionFixtures.GroupedRecord(4, "b", "4.00"),
            PartitionFixtures.GroupedRecord(5, "c", "5.00"),
        };

        var (partition, sink) = await PartitionFixtures.PartitionAsync(records, PartitionFixtures.GroupedSpec(SmallRecordLimits), Cancellation);
        var blocks = partition.Index.Blocks;

        // Block indices 0..N contiguous.
        Assert.Equal([.. Enumerable.Range(0, blocks.Count)], [.. blocks.Select(b => b.BlockIndex)]);

        // Record ordinals contiguous across blocks: each block's first ordinal is the previous block's first + count.
        var expectedFirst = 1;
        foreach (var block in blocks)
        {
            Assert.Equal(expectedFirst, block.FirstRecordOrdinal);
            Assert.True(block.RecordCount >= 1, "a block of a non-empty snapshot holds at least one record");
            expectedFirst += (int)block.RecordCount;
        }

        // Every record present exactly once, in order — nothing lost, duplicated or reordered.
        var readBack = await PartitionFixtures.ReadAllRecordsAsync(sink, Cancellation);
        Assert.Equal(records, readBack);
    }

    [Fact]
    public async Task EachBlockStaysWithinTheLimits()
    {
        var records = Enumerable.Range(1, 7).Select(n => PartitionFixtures.GroupedRecord(n, "g" + n, n + ".00")).ToArray();

        var (partition, _) = await PartitionFixtures.PartitionAsync(records, PartitionFixtures.GroupedSpec(SmallRecordLimits), Cancellation);

        Assert.All(partition.Index.Blocks, block =>
        {
            Assert.True(block.RecordCount <= SmallRecordLimits.HardMaxRecords, "no block exceeds the hard record maximum");
            Assert.True(block.RecordBytes <= SmallRecordLimits.HardMaxRecordBytes, "no block exceeds the hard byte maximum");
        });
    }

    [Fact]
    public async Task TheCutPrefersAGroupBoundaryOnceTheSoftTargetIsReached()
    {
        // Two-record groups with a soft target of two: every cut should fall on a group boundary,
        // so each block holds exactly one group and no group spans a block.
        var records = new[]
        {
            PartitionFixtures.GroupedRecord(1, "a", "1.00"),
            PartitionFixtures.GroupedRecord(2, "a", "2.00"),
            PartitionFixtures.GroupedRecord(3, "b", "3.00"),
            PartitionFixtures.GroupedRecord(4, "b", "4.00"),
        };

        var (partition, _) = await PartitionFixtures.PartitionAsync(records, PartitionFixtures.GroupedSpec(SmallRecordLimits), Cancellation);
        var blocks = partition.Index.Blocks;

        Assert.All(blocks, block =>
        {
            Assert.False(block.ContinuesPreviousGroup, "a boundary cut does not carry a group in");
            Assert.False(block.ContinuesNextGroup, "a boundary cut does not carry a group out");
        });
    }

    [Fact]
    public async Task AGroupLargerThanOneBlockIsSplitAtTheHardMaximumWithContinuityMetadata()
    {
        // Group "a" has four records; the hard maximum is three, so "a" must span two blocks.
        var records = new[]
        {
            PartitionFixtures.GroupedRecord(1, "a", "1.00"),
            PartitionFixtures.GroupedRecord(2, "a", "2.00"),
            PartitionFixtures.GroupedRecord(3, "a", "3.00"),
            PartitionFixtures.GroupedRecord(4, "a", "4.00"),
            PartitionFixtures.GroupedRecord(5, "b", "5.00"),
        };

        var (partition, sink) = await PartitionFixtures.PartitionAsync(records, PartitionFixtures.GroupedSpec(SmallRecordLimits), Cancellation);
        var blocks = partition.Index.Blocks;

        // The group spans the first cut: the first block carries "a" out, the second carries it in.
        Assert.True(blocks[0].ContinuesNextGroup, "the block that ends mid-group carries the group out");
        Assert.True(blocks[1].ContinuesPreviousGroup, "the next block carries the same group in");
        Assert.Equal("a", blocks[1].OpeningGroupKey);

        // The block that continues the group records what the group summed to in the earlier
        // block, so it can print the group's true total without reading the earlier block.
        Assert.Null(blocks[0].OpeningGroupAccumulated);
        Assert.Equal(6.00m, blocks[1].OpeningGroupAccumulated);

        // No record is lost by the split.
        var readBack = await PartitionFixtures.ReadAllRecordsAsync(sink, Cancellation);
        Assert.Equal(records, readBack);
    }

    [Fact]
    public async Task AGroupThatStartsAtACutCarriesNoOpeningAmount()
    {
        var records = new[]
        {
            PartitionFixtures.GroupedRecord(1, "a", "1.00"),
            PartitionFixtures.GroupedRecord(2, "a", "2.00"),
            PartitionFixtures.GroupedRecord(3, "b", "3.00"),
            PartitionFixtures.GroupedRecord(4, "b", "4.00"),
        };

        var (partition, _) = await PartitionFixtures.PartitionAsync(records, PartitionFixtures.GroupedSpec(SmallRecordLimits), Cancellation);

        Assert.All(partition.Index.Blocks, block => Assert.Null(block.OpeningGroupAccumulated));
    }

    [Fact]
    public async Task TheEmptySnapshotRecordsNoOpenGroupAmount()
    {
        var (partition, _) = await PartitionFixtures.PartitionAsync([], PartitionFixtures.GroupedSpec(SmallRecordLimits), Cancellation);

        var block = Assert.Single(partition.Index.Blocks);
        Assert.Equal(0, block.RecordCount);
        Assert.Null(block.OpeningGroupAccumulated);
        Assert.Null(block.OpeningGroupKey);
    }

    [Fact]
    public async Task ARecordThatWouldCrossTheHardByteMaximumStartsANewBlockAndIsNeverSplit()
    {
        var records = new[]
        {
            PartitionFixtures.PlainRecord(1, "1.00"),
            PartitionFixtures.PlainRecord(2, "2.00"),
            PartitionFixtures.PlainRecord(3, "3.00"),
        };

        var (partition, sink) = await PartitionFixtures.PartitionAsync(records, PartitionFixtures.PlainAmountSpec(SmallByteLimits), Cancellation);

        // Each ordinary record is wider than the soft byte target, so each lands in its own block, whole.
        Assert.Equal(3, partition.Index.Blocks.Count);
        Assert.All(partition.Index.Blocks, block => Assert.Equal(1, block.RecordCount));
        var readBack = await PartitionFixtures.ReadAllRecordsAsync(sink, Cancellation);
        Assert.Equal(records, readBack);
    }

    [Fact]
    public async Task AnEmptySnapshotBecomesExactlyOneEmptyBlockZero()
    {
        var (partition, sink) = await PartitionFixtures.PartitionAsync([], PartitionFixtures.GroupedSpec(PartitionLimits.Default), Cancellation);
        var block = Assert.Single(partition.Index.Blocks);

        Assert.Equal(0, block.BlockIndex);
        Assert.Equal(0, block.RecordCount);
        Assert.Equal(0, block.RecordBytes);
        Assert.Equal(1, block.FirstRecordOrdinal);
        Assert.False(block.ContinuesPreviousGroup);
        Assert.False(block.ContinuesNextGroup);
        Assert.Null(block.OpeningGroupKey);
        Assert.Equal(0m, block.OpeningCarriedForward);
        Assert.Equal(0m, block.BlockAmountTotal);

        // The one block is a header-items-v1 document with an empty items array.
        var read = await SnapshotBytes.ReadAllAsync(sink.Block(0), Cancellation);
        Assert.Single(read); // the header only
    }

    [Fact]
    public async Task NoBlockOfANonEmptySnapshotIsEmpty()
    {
        var records = Enumerable.Range(1, 6).Select(n => PartitionFixtures.GroupedRecord(n, "a", "1.00")).ToArray();

        var (partition, _) = await PartitionFixtures.PartitionAsync(records, PartitionFixtures.GroupedSpec(SmallRecordLimits), Cancellation);

        Assert.All(partition.Index.Blocks, block => Assert.True(block.RecordCount >= 1));
    }

    [Fact]
    public async Task ThePageBoundaryLayoutIsRecordedOnTheIndex()
    {
        var records = Enumerable.Range(1, 5).Select(n => PartitionFixtures.GroupedRecord(n, "g" + n, "1.00")).ToArray();

        var (partition, _) = await PartitionFixtures.PartitionAsync(records, PartitionFixtures.GroupedSpec(SmallRecordLimits), Cancellation);

        Assert.Equal("page-boundary", partition.Index.ChunkLayout);
    }

    [Fact]
    public async Task EveryBlockRecordsItsOpeningCarriedForwardFromPreviousRecordsOnly()
    {
        var records = new[]
        {
            PartitionFixtures.GroupedRecord(1, "a", "10.00"),
            PartitionFixtures.GroupedRecord(2, "a", "20.00"),
            PartitionFixtures.GroupedRecord(3, "b", "30.00"),
            PartitionFixtures.GroupedRecord(4, "b", "40.00"),
            PartitionFixtures.GroupedRecord(5, "c", "50.00"),
        };

        var (partition, _) = await PartitionFixtures.PartitionAsync(records, PartitionFixtures.GroupedSpec(SmallRecordLimits), Cancellation);
        var blocks = partition.Index.Blocks;

        // Blocks are [1,2]=30, [3,4]=70, [5]=50. Each opening is the exact sum of every earlier block's amounts.
        var opening = 0m;
        foreach (var block in blocks)
        {
            Assert.Equal(opening, block.OpeningCarriedForward);
            opening += block.BlockAmountTotal ?? 0m;
        }

        // The last block's opening + its own total is the running sum of every record — but it is
        // carried as opening + block total, never printed as a grand total here (the partition-stage boundary).
        Assert.Equal(30m, blocks[0].BlockAmountTotal);
        Assert.Equal(30m, blocks[1].OpeningCarriedForward);
        Assert.Equal(100m, blocks[2].OpeningCarriedForward);
    }

    [Fact]
    public async Task ThePartitionIsDeterministicForAFixedPartitionerVersion()
    {
        var records = Enumerable.Range(1, 5).Select(n => PartitionFixtures.GroupedRecord(n, "g" + (n / 2), n + ".00")).ToArray();

        var (first, firstSink) = await PartitionFixtures.PartitionAsync(records, PartitionFixtures.GroupedSpec(SmallRecordLimits), Cancellation);
        var (second, secondSink) = await PartitionFixtures.PartitionAsync(records, PartitionFixtures.GroupedSpec(SmallRecordLimits), Cancellation);

        // The index serialises byte-identically, and every block document is byte-identical.
        Assert.Equal(BlockIndexPage.Serialize(first.Index), BlockIndexPage.Serialize(second.Index));
        Assert.Equal(firstSink.BlockIndices, secondSink.BlockIndices);
        foreach (var index in firstSink.BlockIndices)
        {
            Assert.Equal(firstSink.Block(index), secondSink.Block(index));
        }
    }

    [Fact]
    public async Task EachIndexEntryDigestMatchesTheBlockDocumentItPointsAt()
    {
        var records = Enumerable.Range(1, 5).Select(n => PartitionFixtures.GroupedRecord(n, "g" + n, "1.00")).ToArray();

        var (partition, sink) = await PartitionFixtures.PartitionAsync(records, PartitionFixtures.GroupedSpec(SmallRecordLimits), Cancellation);

        Assert.All(partition.Index.Blocks, block =>
        {
            var bytes = sink.Block(block.BlockIndex);
            Assert.Equal(PartitionFixtures.Sha256(bytes), block.Sha256);
            Assert.Equal(bytes.Length, block.SizeBytes);
        });
    }

    [Fact]
    public async Task ALateInvalidRecordStopsThePassAndNothingIsReportedAsSuccess()
    {
        // Several good records, then a record whose amount has no exact decimal where one is required
        // the pass fails DATA_INVALID reason=number-out-of-range.
        var records = new[]
        {
            PartitionFixtures.PlainRecord(1, "1.00"),
            PartitionFixtures.PlainRecord(2, "2.00"),
            PartitionFixtures.PlainRecord(3, "1e1000"),
        };

        var violation = await Assert.ThrowsAsync<DataContractViolation>(
            () => PartitionFixtures.PartitionAsync(records, PartitionFixtures.PlainAmountSpec(SmallRecordLimits), Cancellation));

        Assert.Equal(DataContractViolation.DataInvalid, violation.Code);
        Assert.Equal("number-out-of-range", violation.Reason);
    }
}
