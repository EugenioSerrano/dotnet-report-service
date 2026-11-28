using System.Text;
using ReportService.Data;
using ReportService.Data.Partitioning;

namespace ReportService.Data.Tests;

/// <summary>
/// The record and token bounds at two mebibytes, the header kept at one, and the placement of a
/// record above one mebibyte in a block of its own. Each bound is read at its edge - admitted at
/// the limit, refused one byte over with its stable code and limit - through the delivered
/// reader, and the partition runs on the production limits.
/// </summary>
public sealed class TwoMebibyteRecordBoundTests
{
    private const int Record = 2_097_152;
    private const int Header = 1_048_576;

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ARecordOfExactlyTwoMebibytesIsReadWithItsOneStringWhole()
    {
        var record = Padded(1, "a", Record);

        var read = await SnapshotBytes.ReadAllAsync(SnapshotBytes.Document(records: [record]), Cancellation);

        Assert.Equal(2, read.Count);
        Assert.Equal(1, read[1].Ordinal);
        Assert.Equal(Record, read[1].ByteLength);
        Assert.Equal(Record, read[1].Json.Length);
    }

    [Fact]
    public async Task ARecordOneByteOverTwoMebibytesFailsAsRecordTooLarge()
    {
        var record = SnapshotBytes.WideObject(Record + 1);
        Assert.True(Encoding.UTF8.GetByteCount(record) > Record);

        var violation = await Assert.ThrowsAsync<DataContractViolation>(
            () => SnapshotBytes.ReadAllAsync(SnapshotBytes.Document(records: [record]), Cancellation));

        Assert.Equal(DataContractViolation.RecordTooLarge, violation.Code);
        Assert.Equal("record", violation.Limit);
        Assert.Equal(1, violation.Ordinal);
    }

    [Fact]
    public async Task ARecordOfOneStringOneByteOverTwoMebibytesIsRefusedAndNeverAdmitted()
    {
        var record = Padded(1, "a", Record + 1);

        var violation = await Assert.ThrowsAsync<DataContractViolation>(
            () => SnapshotBytes.ReadAllAsync(SnapshotBytes.Document(records: [record]), Cancellation));

        Assert.Contains(violation.Limit, new[] { "record", "token" });
    }

    [Fact]
    public async Task ASingleTokenOverTwoMebibytesFailsAsATokenQuota()
    {
        var record = $$"""{"description":"{{new string('t', Record + 10)}}"}""";

        var violation = await Assert.ThrowsAsync<DataContractViolation>(
            () => SnapshotBytes.ReadAllAsync(SnapshotBytes.Document(records: [record]), Cancellation));

        Assert.Equal(DataContractViolation.QuotaExceeded, violation.Code);
        Assert.Equal("token", violation.Limit);
    }

    [Fact]
    public async Task AHeaderOfExactlyOneMebibyteIsReadAndOneByteMoreIsAHeaderQuota()
    {
        var atBound = HeaderOf(Header);
        var read = await SnapshotBytes.ReadAllAsync(SnapshotBytes.Document(header: atBound), Cancellation);
        Assert.Equal(0, read[0].Ordinal);
        Assert.Equal(Header, read[0].ByteLength);

        var violation = await Assert.ThrowsAsync<DataContractViolation>(
            () => SnapshotBytes.ReadAllAsync(SnapshotBytes.Document(header: HeaderOf(Header + 1)), Cancellation));

        Assert.Equal(DataContractViolation.QuotaExceeded, violation.Code);
        Assert.Equal("header", violation.Limit);
    }

    [Theory]
    [InlineData(1_500_000)]
    [InlineData(2_200_000)]
    public async Task ASingleTokenInTheHeaderLargerThanTheHeaderIsAHeaderQuotaNeverATokenOne(int tokenBytes)
    {
        var header = $$"""{"title":"{{new string('h', tokenBytes)}}"}""";

        var violation = await Assert.ThrowsAsync<DataContractViolation>(
            () => SnapshotBytes.ReadAllAsync(SnapshotBytes.Document(header: header), Cancellation));

        Assert.Equal(DataContractViolation.QuotaExceeded, violation.Code);
        Assert.Equal("header", violation.Limit);
    }

    [Fact]
    public async Task ARecordAboveOneMebibyteIsAloneInItsBlockAndTheNeighboursAreNotSplit()
    {
        var records = new[]
        {
            Padded(1, "a", 300 * 1024),
            Padded(2, "a", 1_500_000),
            Padded(3, "a", 300 * 1024),
        };

        var (partition, sink) = await PartitionFixtures.PartitionAsync(
            records, PartitionFixtures.GroupedSpec(PartitionLimits.Default), Cancellation);
        var blocks = partition.Index.Blocks;

        Assert.Equal(3, blocks.Count);
        Assert.Equal([1L, 1L, 1L], blocks.Select(block => (long)block.RecordCount));
        Assert.Equal(1_500_000, blocks[1].RecordBytes);

        // The large record falls inside group "a": the cut around it carries the group across.
        Assert.True(blocks[0].ContinuesNextGroup);
        Assert.True(blocks[1].ContinuesPreviousGroup);
        Assert.True(blocks[1].ContinuesNextGroup);
        Assert.True(blocks[2].ContinuesPreviousGroup);

        Assert.Equal(records, await PartitionFixtures.ReadAllRecordsAsync(sink, Cancellation));
    }

    [Fact]
    public async Task RecordsWithinOneMebibyteStillShareABlockBeforeAndAfterALargeOne()
    {
        var records = new[]
        {
            Padded(1, "a", 200_000),
            Padded(2, "a", 200_000),
            Padded(3, "b", Record),
            Padded(4, "c", 200_000),
            Padded(5, "c", 200_000),
        };

        var (partition, sink) = await PartitionFixtures.PartitionAsync(
            records, PartitionFixtures.GroupedSpec(PartitionLimits.Default), Cancellation);
        var blocks = partition.Index.Blocks;

        Assert.Equal(3, blocks.Count);
        Assert.Equal(2, blocks[0].RecordCount);
        Assert.Equal(1, blocks[1].RecordCount);
        Assert.Equal(Record, blocks[1].RecordBytes);
        Assert.Equal(2, blocks[2].RecordCount);
        Assert.All(
            blocks.Where(block => block.RecordCount > 1),
            block => Assert.True(block.RecordBytes <= DataContractLimits.BlockRecordBytes));
        Assert.Equal(records, await PartitionFixtures.ReadAllRecordsAsync(sink, Cancellation));
    }

    [Fact]
    public async Task ATwoMebibyteRecordFirstOrLastIsAloneAndNoBlockIsEmpty()
    {
        var records = new[] { Padded(1, "a", Record), Padded(2, "b", 100), Padded(3, "c", Record) };

        var (partition, _) = await PartitionFixtures.PartitionAsync(
            records, PartitionFixtures.GroupedSpec(PartitionLimits.Default), Cancellation);

        Assert.Equal(3, partition.Index.Blocks.Count);
        Assert.All(partition.Index.Blocks, block => Assert.Equal(1, block.RecordCount));
    }

    // A grouped record of exactly the given wire length, its padding one string.
    private static string Padded(int sequence, string group, int bytes)
    {
        var prefix = $$"""{"sequence":{{sequence}},"group":"{{group}}","amount":1.00,"pad":""";
        const string suffix = "\"}";
        return prefix + "\"" + new string('x', bytes - prefix.Length - 1 - suffix.Length) + suffix;
    }

    // A header object of exactly the given wire length.
    private static string HeaderOf(int bytes)
    {
        const string prefix = "{\"title\":\"";
        const string suffix = "\"}";
        return prefix + new string('h', bytes - prefix.Length - suffix.Length) + suffix;
    }
}
