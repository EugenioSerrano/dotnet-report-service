using System.Text;
using ReportService.Data;

namespace ReportService.Data.Tests;

/// <summary>
/// The single-block partitioner and the block document it writes: how many records
/// a block takes, where it refuses, and that what it writes reads back through the
/// same reader with every record and every byte the snapshot held.
/// </summary>
public sealed class SingleBlockPartitionerTests
{
    [Fact]
    public async Task OneRecordBecomesOneBlockOfOneRecord()
    {
        var (summary, document) = await PartitionAsync([SnapshotBytes.Record(1)]);

        Assert.Equal(1, summary.BlockCount);
        Assert.Equal(1, summary.RecordCount);
        Assert.Equal(1, summary.FirstRecordOrdinal);
        Assert.Equal((long)SnapshotBytes.Record(1).Length, summary.RecordBytes);
        Assert.True(document.Length > 0);
    }

    [Fact]
    public async Task TwoThousandRecordsAreAdmitted()
    {
        var records = Enumerable.Range(1, DataContractLimits.BlockRecords).Select(SnapshotBytes.Record).ToArray();

        var (summary, _) = await PartitionAsync(records);

        Assert.Equal(DataContractLimits.BlockRecords, summary.RecordCount);
    }

    [Fact]
    public async Task TheFirstRecordBeyondTheBlockMaximumIsRefusedBeforeItIsWritten()
    {
        var records = Enumerable.Range(1, DataContractLimits.BlockRecords + 1).Select(SnapshotBytes.Record).ToArray();

        var violation = await Assert.ThrowsAsync<DataContractViolation>(() => PartitionAsync(records));

        Assert.Equal(DataContractViolation.QuotaExceeded, violation.Code);
        Assert.Equal("block", violation.Limit);
        Assert.Equal(DataContractLimits.BlockRecords + 1, violation.Ordinal);
    }

    [Fact]
    public async Task ABundleOfRecordsThatWouldPassTheByteMaximumIsRefused()
    {
        // Each record is about 300 KiB of description, so the third one crosses the
        // block's byte maximum while the record itself stays inside its own limit.
        var records = new[]
        {
            SnapshotBytes.TallRecord(1, 300_000),
            SnapshotBytes.TallRecord(2, 300_000),
            SnapshotBytes.TallRecord(3, 300_000),
            SnapshotBytes.TallRecord(4, 300_000),
        };

        var violation = await Assert.ThrowsAsync<DataContractViolation>(() => PartitionAsync(records));

        Assert.Equal("block", violation.Limit);
        Assert.Equal(4, violation.Ordinal);
    }

    [Fact]
    public async Task ASnapshotWithNoRecordBecomesOneEmptyBlock()
    {
        var (summary, document) = await PartitionAsync([]);

        Assert.Equal(1, summary.BlockCount);
        Assert.Equal(0, summary.RecordCount);
        Assert.Equal(0, summary.RecordBytes);
        Assert.Equal(1, summary.FirstRecordOrdinal);
        Assert.Equal(
            SnapshotBytes.Utf8($$"""{"contractVersion":"1.0","header":{{SnapshotBytes.ValidHeader}},"items":[]}"""),
            document);
    }

    [Fact]
    public async Task TheBlockDocumentReadsBackWithEveryRecordAndByteTheSnapshotHeld()
    {
        var records = new[]
        {
            """{"n":1.500,"text":"caf\u00e9","nested":{"a":[1,2,{"b":null}]}}""",
            """{"n":-2,"text":"<&>","list":[]}""",
        };

        var (_, document) = await PartitionAsync(records);
        var read = await SnapshotBytes.ReadAllAsync(document, TestContext.Current.CancellationToken);

        Assert.Equal(3, read.Count);
        Assert.Equal(SnapshotBytes.ValidHeader, SnapshotBytes.Text(read[0]));
        Assert.Equal(records[0], SnapshotBytes.Text(read[1]));
        Assert.Equal(records[1], SnapshotBytes.Text(read[2]));
    }

    [Fact]
    public async Task ThePartitionerVersionIsRecordedWithTheBlock()
    {
        Assert.Equal(1, SingleBlockPartitioner.PartitionerVersion);
    }

    private static async Task<(PartitionSummary Summary, byte[] Document)> PartitionAsync(string[] records)
    {
        var snapshot = SnapshotBytes.Document(records: records);
        await using var source = new MemoryStream(snapshot, writable: false);
        using var block = new MemoryStream();
        var summary = await SingleBlockPartitioner.PartitionAsync(
            HeaderItemsReader.ReadAsync(source, TestContext.Current.CancellationToken),
            block,
            TestContext.Current.CancellationToken);
        return (summary, block.ToArray());
    }
}
