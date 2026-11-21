using System.Text;
using System.Text.Json;
using ReportService.Data;

namespace ReportService.Data.Tests;

/// <summary>
/// The refusals of the reader and of the index page at their edges: the shapes that
/// are not a document of the profile, and the index pages that are not a page. Each
/// case asserts the single rule it breaks, so a refusal can never widen into a
/// different one unnoticed.
/// </summary>
public sealed class DocumentRefusalTests
{
    [Fact]
    public async Task ADocumentThatIsNotAnObjectFailsAsProfileViolation()
    {
        var violation = await Assert.ThrowsAsync<DataContractViolation>(
            () => SnapshotBytes.ReadAllAsync(SnapshotBytes.Utf8("[1,2]"), TestContext.Current.CancellationToken));

        Assert.Equal("profile-violation", violation.Reason);
    }

    [Fact]
    public async Task AContractVersionThatIsNotTextFailsAsContractVersion()
    {
        var violation = await Assert.ThrowsAsync<DataContractViolation>(
            () => SnapshotBytes.ReadAllAsync(
                SnapshotBytes.Utf8("""{"contractVersion":1,"header":{},"items":[]}"""),
                TestContext.Current.CancellationToken));

        Assert.Equal("contract-version", violation.Reason);
    }

    [Fact]
    public async Task ItemsBeforeTheHeaderFailAsHeaderNotFirst()
    {
        var violation = await Assert.ThrowsAsync<DataContractViolation>(
            () => SnapshotBytes.ReadAllAsync(
                SnapshotBytes.Utf8("""{"contractVersion":"1.0","items":[],"header":{}}"""),
                TestContext.Current.CancellationToken));

        Assert.Equal("header-not-first", violation.Reason);
    }

    [Fact]
    public async Task AnUnknownRootPropertyFailsAsProfileViolation()
    {
        var violation = await Assert.ThrowsAsync<DataContractViolation>(
            () => SnapshotBytes.ReadAllAsync(
                SnapshotBytes.Utf8("""{"contractVersion":"1.0","header":{},"items":[],"extra":1}"""),
                TestContext.Current.CancellationToken));

        Assert.Equal("profile-violation", violation.Reason);
    }

    [Fact]
    public async Task AHeaderThatIsNotAnObjectFailsAsTypeMismatch()
    {
        var violation = await Assert.ThrowsAsync<DataContractViolation>(
            () => SnapshotBytes.ReadAllAsync(
                SnapshotBytes.Utf8("""{"contractVersion":"1.0","header":[],"items":[]}"""),
                TestContext.Current.CancellationToken));

        Assert.Equal("type-mismatch", violation.Reason);
    }

    [Fact]
    public async Task ASequenceWithNoHeaderIsRefusedByThePartitioner()
    {
        using var block = new MemoryStream();

        var violation = await Assert.ThrowsAsync<DataContractViolation>(
            () => SingleBlockPartitioner.PartitionAsync(Empty(), block, TestContext.Current.CancellationToken));

        Assert.Equal("profile-violation", violation.Reason);
    }

    [Fact]
    public void APageWhoseKeyIsNotTextIsRefused()
    {
        var json = Encoding.UTF8.GetBytes(
            """{"partitionerVersion":1,"blocks":[{"blockIndex":0,"key":1,"sha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","sizeBytes":1,"firstRecordOrdinal":1,"recordCount":0,"recordBytes":0}]}""");

        Assert.Throws<JsonException>(() => BlockIndexPage.Parse(json));
    }

    [Fact]
    public void APageWhoseDigestIsNotHexadecimalIsRefused()
    {
        var json = Encoding.UTF8.GetBytes(
            """{"partitionerVersion":1,"blocks":[{"blockIndex":0,"key":"block/a/0/b","sha256":"================================================================","sizeBytes":1,"firstRecordOrdinal":1,"recordCount":0,"recordBytes":0}]}""");

        Assert.Throws<JsonException>(() => BlockIndexPage.Parse(json));
    }

    [Fact]
    public void APageWhoseDigestIsTooShortIsRefused()
    {
        var json = Encoding.UTF8.GetBytes(
            """{"partitionerVersion":1,"blocks":[{"blockIndex":0,"key":"block/a/0/b","sha256":"abc","sizeBytes":1,"firstRecordOrdinal":1,"recordCount":0,"recordBytes":0}]}""");

        Assert.Throws<JsonException>(() => BlockIndexPage.Parse(json));
    }

    [Fact]
    public void APageWithoutBlocksIsRefused()
    {
        Assert.Throws<JsonException>(() => BlockIndexPage.Parse(Encoding.UTF8.GetBytes("""{"partitionerVersion":1}""")));
    }

    [Fact]
    public void AViolationOfALimitNamesTheLimitAndNothingElse()
    {
        var violation = new DataContractViolation(
            DataContractViolation.QuotaExceeded, null, "block", 4, 512);

        Assert.Equal("QUOTA_EXCEEDED", violation.Code);
        Assert.Null(violation.Reason);
        Assert.Equal("block", violation.Limit);
        Assert.Equal(4, violation.Ordinal);
        Assert.Equal(512, violation.Offset);
        Assert.Contains("limit=block", violation.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("reason=", violation.Message, StringComparison.Ordinal);
    }

    private static async IAsyncEnumerable<RawRecord> Empty()
    {
        await Task.CompletedTask;
        yield break;
    }
}
