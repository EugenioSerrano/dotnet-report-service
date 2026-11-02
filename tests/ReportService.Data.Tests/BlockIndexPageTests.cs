using System.Text;

namespace ReportService.Data.Tests;

/// <summary>
/// The index page: the JSON the partitioner records beside a block, and the record
/// of a block interval it is read back into.
/// </summary>
public sealed class BlockIndexPageTests
{
    [Fact]
    public void APageRoundTripsThroughItsJson()
    {
        var page = new BlockIndexPage(
            SingleBlockPartitioner.PartitionerVersion,
            [new BlockIndexEntry(0, "block/job-0123456789abcdef0123456789abcdef/0/aa", new string('a', 64), 1_024, 1, 25, 900)]);

        var parsed = BlockIndexPage.Parse(BlockIndexPage.Serialize(page));

        var entry = Assert.Single(parsed.Blocks);
        Assert.Equal(page.PartitionerVersion, parsed.PartitionerVersion);
        Assert.Equal(0, entry.BlockIndex);
        Assert.Equal("block/job-0123456789abcdef0123456789abcdef/0/aa", entry.Key);
        Assert.Equal(new string('a', 64), entry.Sha256);
        Assert.Equal(1_024, entry.SizeBytes);
        Assert.Equal(1, entry.FirstRecordOrdinal);
        Assert.Equal(25, entry.RecordCount);
        Assert.Equal(900, entry.RecordBytes);
    }

    [Fact]
    public void TheEmptyBlockIsEncodedWithOrdinalOneAndNoRecord()
    {
        var page = new BlockIndexPage(
            SingleBlockPartitioner.PartitionerVersion,
            [new BlockIndexEntry(0, "block/job-0123456789abcdef0123456789abcdef/0/bb", new string('b', 64), 64, 1, 0, 0)]);

        var json = Encoding.UTF8.GetString(BlockIndexPage.Serialize(page));
        var parsed = BlockIndexPage.Parse(BlockIndexPage.Serialize(page));

        Assert.Contains("\"firstRecordOrdinal\":1", json, StringComparison.Ordinal);
        Assert.Contains("\"recordCount\":0", json, StringComparison.Ordinal);
        Assert.Contains("\"recordBytes\":0", json, StringComparison.Ordinal);
        var entry = Assert.Single(parsed.Blocks);
        Assert.Equal(1, entry.FirstRecordOrdinal);
        Assert.Equal(0, entry.RecordCount);
    }

    [Fact]
    public void TheSerialisedPageIsStableAcrossCalls()
    {
        var page = new BlockIndexPage(
            SingleBlockPartitioner.PartitionerVersion,
            [new BlockIndexEntry(0, "block/job-0123456789abcdef0123456789abcdef/0/cc", new string('c', 64), 4, 1, 1, 2)]);

        Assert.Equal(BlockIndexPage.Serialize(page), BlockIndexPage.Serialize(page));
    }

    [Fact]
    public void AMalformedPageIsRefusedRatherThanPartlyRead()
    {
        var malformed = Encoding.UTF8.GetBytes("""{"partitionerVersion":1,"blocks":[{"blockIndex":0}]}""");

        Assert.Throws<System.Text.Json.JsonException>(() => BlockIndexPage.Parse(malformed));
    }

    [Fact]
    public void APageWithNoBlockParsesAsAnEmptyList()
    {
        var page = new BlockIndexPage(SingleBlockPartitioner.PartitionerVersion, []);

        var json = BlockIndexPage.Serialize(page);
        var parsed = BlockIndexPage.Parse(json);

        Assert.Empty(parsed.Blocks);
    }
}
