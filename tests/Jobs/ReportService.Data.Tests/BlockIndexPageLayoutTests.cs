using System.Text;
using ReportService.Data;
using ReportService.Data.Partitioning;

namespace ReportService.Data.Tests;

/// <summary>
/// The enriched, durable index page: the page-level chunk layout and the per-entry
/// group-continuity flags, opening group key and opening carried-forward / block total
/// are written in block order and read back exactly (ADR-022 §3.6 item 2, ADR-021 §3.3,
/// Block RULE-06). Contiguity is validated on read — block indices 0..N with no gap and
/// record ordinals contiguous across entries — and an old single-entry page still parses
/// with the new fields defaulted (SPEC §12 compatibility).
/// </summary>
public sealed class BlockIndexPageLayoutTests
{
    private static BlockIndexEntry Entry(
        int index,
        int firstOrdinal,
        long count,
        bool continuesPrevious = false,
        bool continuesNext = false,
        string? openingGroupKey = null,
        decimal? opening = null,
        decimal? total = null) =>
        new(
            index,
            "block/job-0123456789abcdef0123456789abcdef/" + index + "/aa",
            new string('a', 64),
            1_024,
            firstOrdinal,
            count,
            256,
            continuesPrevious,
            continuesNext,
            openingGroupKey,
            opening,
            total);

    [Fact]
    public void TheChunkLayoutRoundTripsThroughTheJson()
    {
        var page = new BlockIndexPage(StreamingPartitioner.PartitionerVersion, [Entry(0, 1, 2)], "page-boundary");

        var json = Encoding.UTF8.GetString(BlockIndexPage.Serialize(page));
        var parsed = BlockIndexPage.Parse(BlockIndexPage.Serialize(page));

        Assert.Contains("\"chunkLayout\":\"page-boundary\"", json, StringComparison.Ordinal);
        Assert.Equal("page-boundary", parsed.ChunkLayout);
    }

    [Fact]
    public void TheContinuityFlagsGroupKeyAndCarriedForwardRoundTripExactly()
    {
        var page = new BlockIndexPage(
            StreamingPartitioner.PartitionerVersion,
            [
                Entry(0, 1, 3, continuesNext: true, openingGroupKey: "a", opening: 0m, total: 30.50m),
                Entry(1, 4, 2, continuesPrevious: true, openingGroupKey: "a", opening: 30.50m, total: -5.25m),
            ],
            "page-boundary");

        var parsed = BlockIndexPage.Parse(BlockIndexPage.Serialize(page));

        Assert.True(parsed.Blocks[0].ContinuesNextGroup);
        Assert.False(parsed.Blocks[0].ContinuesPreviousGroup);
        Assert.Equal("a", parsed.Blocks[0].OpeningGroupKey);
        Assert.Equal(0m, parsed.Blocks[0].OpeningCarriedForward);
        Assert.Equal(30.50m, parsed.Blocks[0].BlockAmountTotal);

        Assert.True(parsed.Blocks[1].ContinuesPreviousGroup);
        Assert.Equal(30.50m, parsed.Blocks[1].OpeningCarriedForward);
        Assert.Equal(-5.25m, parsed.Blocks[1].BlockAmountTotal);
    }

    [Fact]
    public void AGapInTheBlockIndicesIsRefused()
    {
        // Blocks 0 and 2, no block 1 — a gap the reader must refuse rather than read.
        var page = new BlockIndexPage(
            StreamingPartitioner.PartitionerVersion,
            [Entry(0, 1, 2), Entry(2, 3, 2)],
            "page-boundary");

        Assert.Throws<System.Text.Json.JsonException>(() => BlockIndexPage.Parse(BlockIndexPage.Serialize(page)));
    }

    [Fact]
    public void NonContiguousRecordOrdinalsAreRefused()
    {
        // Block 0 holds ordinals 1..2, so block 1 must start at ordinal 3; starting at 5 leaves a gap.
        var page = new BlockIndexPage(
            StreamingPartitioner.PartitionerVersion,
            [Entry(0, 1, 2), Entry(1, 5, 2)],
            "page-boundary");

        Assert.Throws<System.Text.Json.JsonException>(() => BlockIndexPage.Parse(BlockIndexPage.Serialize(page)));
    }

    [Fact]
    public void AContiguousMultiBlockPageIsAccepted()
    {
        var page = new BlockIndexPage(
            StreamingPartitioner.PartitionerVersion,
            [Entry(0, 1, 2), Entry(1, 3, 3), Entry(2, 6, 1)],
            "page-boundary");

        var parsed = BlockIndexPage.Parse(BlockIndexPage.Serialize(page));

        Assert.Equal(3, parsed.Blocks.Count);
    }

    [Fact]
    public void AnOldSingleEntryPageWithoutTheNewFieldsStillParsesWithDefaults()
    {
        // The shape the live single-block path writes today: no chunkLayout, no continuity, no carried-forward.
        var old = Encoding.UTF8.GetBytes(
            """{"partitionerVersion":1,"blocks":[{"blockIndex":0,"key":"block/job-0123456789abcdef0123456789abcdef/0/aa","sha256":"""
            + "\"" + new string('a', 64) + "\""
            + ""","sizeBytes":1024,"firstRecordOrdinal":1,"recordCount":25,"recordBytes":900}]}""");

        var parsed = BlockIndexPage.Parse(old);
        var entry = Assert.Single(parsed.Blocks);

        Assert.Equal("page-boundary", parsed.ChunkLayout);
        Assert.False(entry.ContinuesPreviousGroup);
        Assert.False(entry.ContinuesNextGroup);
        Assert.Null(entry.OpeningGroupKey);
        Assert.Null(entry.OpeningCarriedForward);
        Assert.Null(entry.BlockAmountTotal);
    }

    [Fact]
    public void TheEmptyBlockZeroPageIsAcceptedAndRoundTrips()
    {
        var page = new BlockIndexPage(
            StreamingPartitioner.PartitionerVersion,
            [new BlockIndexEntry(0, "block/job-0123456789abcdef0123456789abcdef/0/bb", new string('b', 64), 64, 1, 0, 0, false, false, null, 0m, 0m)],
            "page-boundary");

        var parsed = BlockIndexPage.Parse(BlockIndexPage.Serialize(page));
        var entry = Assert.Single(parsed.Blocks);

        Assert.Equal(0, entry.RecordCount);
        Assert.Equal(1, entry.FirstRecordOrdinal);
        Assert.Equal(0m, entry.OpeningCarriedForward);
    }

    [Fact]
    public void AMalformedPageIsStillRefused()
    {
        var malformed = Encoding.UTF8.GetBytes("""{"partitionerVersion":1,"blocks":[{"blockIndex":0}]}""");

        Assert.Throws<System.Text.Json.JsonException>(() => BlockIndexPage.Parse(malformed));
    }
}
