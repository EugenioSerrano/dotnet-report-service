using ReportService.Data;

namespace ReportService.Data.Tests;

/// <summary>
/// The admission invariant of this version, and the instruction that ends it: a PDF job
/// in this increment yields exactly one block, and a snapshot beyond the effective limit
/// is refused. A version that can print more than one block removes the limit, its
/// setting and this test in the same change.
/// </summary>
public sealed class SingleBlockLimitIsTemporaryTests
{
    private const string RemovalInstruction =
        "This service version prints one block per PDF report. A version that can print more than one block removes this limit, its setting and this test in the same change.";

    [Theory]
    [InlineData(1_000)]
    [InlineData(2_000)]
    public async Task EveryAdmittedSnapshotYieldsExactlyOneBlock(int recordCount)
    {
        var summary = await PartitionAsync(SnapshotBytes.Records(recordCount), new SingleBlockLimit(recordCount));

        Assert.True(
            summary.BlockCount == 1,
            RemovalInstruction + " The admitted snapshot yielded " + summary.BlockCount + " blocks.");
        Assert.Equal(recordCount, summary.RecordCount);
    }

    [Fact]
    public async Task ARefusedSnapshotStopsAtTheFirstRecordBeyondTheLimitAndNamesIt()
    {
        var violation = await Assert.ThrowsAsync<DataContractViolation>(
            () => PartitionAsync(SnapshotBytes.Records(50_000), new SingleBlockLimit(1_000)));

        Assert.Equal(DataContractViolation.QuotaExceeded, violation.Code);
        Assert.Equal("block", violation.Limit);
        Assert.Equal(1_001, violation.Ordinal);
        Assert.Equal("dimension=records applied=1000", violation.Reason);
        Assert.True(
            violation.Message.Contains("limit=block", StringComparison.Ordinal),
            RemovalInstruction);
    }

    private static async Task<PartitionSummary> PartitionAsync(string[] records, SingleBlockLimit limit)
    {
        var snapshot = SnapshotBytes.Document(records: records);
        await using var source = new MemoryStream(snapshot, writable: false);
        using var block = new MemoryStream();
        return await SingleBlockPartitioner.PartitionAsync(
            HeaderItemsReader.ReadAsync(source, TestContext.Current.CancellationToken),
            block,
            TestContext.Current.CancellationToken,
            limit: limit);
    }
}
