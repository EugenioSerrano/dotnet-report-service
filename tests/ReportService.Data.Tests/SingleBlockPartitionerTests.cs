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

    [Fact]
    public async Task AConfiguredThousandAdmitsAThousandRecordsAndRefusesTheNextOne()
    {
        var limit = new SingleBlockLimit(1_000);

        var (summary, _) = await PartitionAsync(SnapshotBytes.Records(1_000), limit);
        Assert.Equal(1, summary.BlockCount);
        Assert.Equal(1_000, summary.RecordCount);

        var violation = await Assert.ThrowsAsync<DataContractViolation>(
            () => PartitionAsync(SnapshotBytes.Records(1_001), limit));

        Assert.Equal(DataContractViolation.QuotaExceeded, violation.Code);
        Assert.Equal("block", violation.Limit);
        Assert.Equal(1_001, violation.Ordinal);
        Assert.Equal("dimension=records applied=1000", violation.Reason);
    }

    [Fact]
    public async Task AConfiguredFiveThousandBehavesAsTheHardMaximum()
    {
        var limit = new SingleBlockLimit(5_000);

        var (summary, _) = await PartitionAsync(SnapshotBytes.Records(2_000), limit);
        Assert.Equal(2_000, summary.RecordCount);

        var violation = await Assert.ThrowsAsync<DataContractViolation>(
            () => PartitionAsync(SnapshotBytes.Records(2_001), limit));

        Assert.Equal("dimension=records applied=2000", violation.Reason);
        Assert.Equal(2_001, violation.Ordinal);
    }

    [Fact]
    public async Task TheByteBoundaryPairIsAdmittedAtTheBoundAndRefusedOneByteBeyondIt()
    {
        // Sixty-four records of exactly 16 384 bytes are exactly the block's byte maximum;
        // the sixty-fifth crosses it with one record more.
        var record = SizedRecord(16_384);
        var family = Enumerable.Repeat(record, 64).ToArray();
        var overByOne = Enumerable.Repeat(record, 65).ToArray();
        var limit = new SingleBlockLimit(1_000);

        var (summary, _) = await PartitionAsync(family, limit);
        Assert.Equal(64, summary.RecordCount);
        Assert.Equal(SingleBlockLimit.HardMaxRecordBytes, summary.RecordBytes);

        var violation = await Assert.ThrowsAsync<DataContractViolation>(() => PartitionAsync(overByOne, limit));

        Assert.Equal("dimension=bytes applied=1048576", violation.Reason);
        Assert.Equal(65, violation.Ordinal);
    }

    [Fact]
    public async Task TheRecordCountIsEvaluatedBeforeTheByteBound()
    {
        var limit = new SingleBlockLimit(1_000);
        var records = SnapshotBytes.Records(1_000).Append(SnapshotBytes.TallRecord(1, 1_048_000)).ToArray();

        var violation = await Assert.ThrowsAsync<DataContractViolation>(() => PartitionAsync(records, limit));

        Assert.Equal("dimension=records applied=1000", violation.Reason);
        Assert.Equal(1_001, violation.Ordinal);
    }

    // A record whose wire length is exactly <paramref name="bytes"/>: the ordinary record
    // with a description long enough to reach the length the case needs.
    private static string SizedRecord(int bytes)
    {
        var ordinary = Encoding.UTF8.GetByteCount(SnapshotBytes.TallRecord(1, 0));
        return SnapshotBytes.TallRecord(1, bytes - ordinary);
    }

    private static async Task<(PartitionSummary Summary, byte[] Document)> PartitionAsync(
        string[] records,
        SingleBlockLimit? limit = null)
    {
        var snapshot = SnapshotBytes.Document(records: records);
        await using var source = new MemoryStream(snapshot, writable: false);
        using var block = new MemoryStream();
        var summary = await SingleBlockPartitioner.PartitionAsync(
            HeaderItemsReader.ReadAsync(source, TestContext.Current.CancellationToken),
            block,
            TestContext.Current.CancellationToken,
            limit: limit);
        return (summary, block.ToArray());
    }
}
