using System.Text;
using ReportService.Data;

namespace ReportService.Data.Tests;

/// <summary>
/// The forward-only reader of a snapshot: what it yields, in what order, and how it
/// fails. Every case names the document it hands the reader and the single answer
/// it expects, so a change of the reader's contract shows up as a failing case
/// rather than as a surprise on a job.
/// </summary>
public sealed class HeaderItemsReaderTests
{
    [Fact]
    public async Task TheHeaderComesFirstAndRecordsFollowInReadOrder()
    {
        var document = SnapshotBytes.Document(records: [SnapshotBytes.Record(1), SnapshotBytes.Record(2)]);

        var read = await SnapshotBytes.ReadAllAsync(document, TestContext.Current.CancellationToken);

        Assert.Equal(3, read.Count);
        Assert.Equal(0, read[0].Ordinal);
        Assert.Equal(SnapshotBytes.ValidHeader, SnapshotBytes.Text(read[0]));
        Assert.Equal(1, read[1].Ordinal);
        Assert.Equal(SnapshotBytes.Record(1), SnapshotBytes.Text(read[1]));
        Assert.Equal(2, read[2].Ordinal);
        Assert.Equal(SnapshotBytes.Record(2), SnapshotBytes.Text(read[2]));
    }

    [Fact]
    public async Task EveryRecordReportsTheLengthOfTheBytesItHolds()
    {
        var record = SnapshotBytes.Record(7);
        var document = SnapshotBytes.Document(records: [record]);

        var read = await SnapshotBytes.ReadAllAsync(document, TestContext.Current.CancellationToken);

        Assert.Equal((long)record.Length, read[1].ByteLength);
        Assert.Equal(record.Length, read[1].Json.Length);
        Assert.Equal((long)SnapshotBytes.ValidHeader.Length, read[0].ByteLength);
    }

    [Fact]
    public async Task ASingleLeadingByteOrderMarkIsSkipped()
    {
        var document = SnapshotBytes.Document(records: [SnapshotBytes.Record(1)]);
        var withMark = new byte[document.Length + 3];
        withMark[0] = 0xEF;
        withMark[1] = 0xBB;
        withMark[2] = 0xBF;
        document.CopyTo(withMark, 3);

        var read = await SnapshotBytes.ReadAllAsync(withMark, TestContext.Current.CancellationToken);

        Assert.Equal(2, read.Count);
        Assert.Equal(SnapshotBytes.Record(1), SnapshotBytes.Text(read[1]));
    }

    [Fact]
    public async Task ADocumentWithNoRecordIsAdmitted()
    {
        var read = await SnapshotBytes.ReadAllAsync(
            SnapshotBytes.Document(), TestContext.Current.CancellationToken);

        var header = Assert.Single(read);
        Assert.Equal(0, header.Ordinal);
        Assert.Equal(SnapshotBytes.ValidHeader, SnapshotBytes.Text(header));
    }

    [Fact]
    public async Task ManyRecordsCrossTheReadBufferWithoutLosingOne()
    {
        var records = Enumerable.Range(1, 2_000).Select(SnapshotBytes.Record).ToArray();

        var read = await SnapshotBytes.ReadAllAsync(
            SnapshotBytes.Document(records: records), TestContext.Current.CancellationToken);

        Assert.Equal(2_001, read.Count);
        for (var index = 1; index < read.Count; index++)
        {
            Assert.Equal(index, read[index].Ordinal);
            Assert.Equal(records[index - 1], SnapshotBytes.Text(read[index]));
        }
    }

    [Fact]
    public async Task ARecordNearTheRecordLimitIsReadWhole()
    {
        var record = SnapshotBytes.TallRecord(1, 600_000);

        var read = await SnapshotBytes.ReadAllAsync(
            SnapshotBytes.Document(records: [record]), TestContext.Current.CancellationToken);

        Assert.Equal(record, SnapshotBytes.Text(read[1]));
        Assert.Equal((long)record.Length, read[1].ByteLength);
    }

    [Fact]
    public async Task NumbersAreCopiedExactlyAsTheSnapshotHeldThem()
    {
        var record = """{"a":1.2300,"b":1E+3,"c":12345678901234567890123456789012,"d":-0.000}""";

        var read = await SnapshotBytes.ReadAllAsync(
            SnapshotBytes.Document(records: [record]), TestContext.Current.CancellationToken);

        Assert.Equal(record, SnapshotBytes.Text(read[1]));
    }

    [Fact]
    public async Task EscapedCharactersAreCopiedExactlyAsTheSnapshotHeldThem()
    {
        var record = """{"text":"caf\u00e9 \/ \u00A0","nested":{"list":[true,false,null]}}""";

        var read = await SnapshotBytes.ReadAllAsync(
            SnapshotBytes.Document(records: [record]), TestContext.Current.CancellationToken);

        Assert.Equal(record, SnapshotBytes.Text(read[1]));
    }

    [Fact]
    public async Task AContractVersionOtherThanOneZeroFailsAsContractVersion()
    {
        var document = SnapshotBytes.Utf8("""{"contractVersion":"2.0","header":{},"items":[]}""");

        var violation = await Assert.ThrowsAsync<DataContractViolation>(
            () => SnapshotBytes.ReadAllAsync(document, TestContext.Current.CancellationToken));

        Assert.Equal(DataContractViolation.DataInvalid, violation.Code);
        Assert.Equal("contract-version", violation.Reason);
    }

    [Fact]
    public async Task ADocumentCutShortFailsAsTruncated()
    {
        var document = SnapshotBytes.Document(records: [SnapshotBytes.Record(1)]);
        var cut = document[..(document.Length - 5)];

        var violation = await Assert.ThrowsAsync<DataContractViolation>(
            () => SnapshotBytes.ReadAllAsync(cut, TestContext.Current.CancellationToken));

        Assert.Equal("truncated", violation.Reason);
        Assert.Equal(1, violation.Ordinal);
    }

    [Fact]
    public async Task ContentAfterTheDocumentFailsAsTrailingContent()
    {
        var document = SnapshotBytes.Document(records: [SnapshotBytes.Record(1)]);
        var withTail = SnapshotBytes.Utf8(Encoding.UTF8.GetString(document) + " {}");

        var violation = await Assert.ThrowsAsync<DataContractViolation>(
            () => SnapshotBytes.ReadAllAsync(withTail, TestContext.Current.CancellationToken));

        Assert.Equal("trailing-content", violation.Reason);
    }

    [Fact]
    public async Task ADocumentWithoutItemsFailsAsProfileViolation()
    {
        var document = SnapshotBytes.Utf8("""{"contractVersion":"1.0","header":{}}""");

        var violation = await Assert.ThrowsAsync<DataContractViolation>(
            () => SnapshotBytes.ReadAllAsync(document, TestContext.Current.CancellationToken));

        Assert.Equal("profile-violation", violation.Reason);
    }

    [Fact]
    public async Task RootPropertiesOutOfOrderFailAsProfileViolation()
    {
        var document = SnapshotBytes.Utf8("""{"header":{},"contractVersion":"1.0","items":[]}""");

        var violation = await Assert.ThrowsAsync<DataContractViolation>(
            () => SnapshotBytes.ReadAllAsync(document, TestContext.Current.CancellationToken));

        Assert.Equal("profile-violation", violation.Reason);
    }

    [Fact]
    public async Task AnElementsThatIsNotAnObjectFailsAsProfileViolation()
    {
        var document = SnapshotBytes.Utf8("""{"contractVersion":"1.0","header":{},"items":[1]}""");

        var violation = await Assert.ThrowsAsync<DataContractViolation>(
            () => SnapshotBytes.ReadAllAsync(document, TestContext.Current.CancellationToken));

        Assert.Equal("profile-violation", violation.Reason);
        Assert.Equal(1, violation.Ordinal);
    }

    [Fact]
    public async Task ARootPropertyRepeatedFailsAsDuplicateProperty()
    {
        var document = SnapshotBytes.Utf8("""{"contractVersion":"1.0","header":{},"header":{},"items":[]}""");

        var violation = await Assert.ThrowsAsync<DataContractViolation>(
            () => SnapshotBytes.ReadAllAsync(document, TestContext.Current.CancellationToken));

        Assert.Equal("duplicate-property", violation.Reason);
    }

    [Fact]
    public async Task InvalidJsonFailsAsMalformed()
    {
        var document = SnapshotBytes.Utf8("""{"contractVersion":"1.0",,}""");

        var violation = await Assert.ThrowsAsync<DataContractViolation>(
            () => SnapshotBytes.ReadAllAsync(document, TestContext.Current.CancellationToken));

        Assert.Equal("malformed", violation.Reason);
    }

    [Fact]
    public async Task ARecordOverTheRecordLimitFailsAsRecordTooLarge()
    {
        var record = SnapshotBytes.WideObject(1_100_000);

        var violation = await Assert.ThrowsAsync<DataContractViolation>(
            () => SnapshotBytes.ReadAllAsync(
                SnapshotBytes.Document(records: [record]), TestContext.Current.CancellationToken));

        Assert.Equal(DataContractViolation.RecordTooLarge, violation.Code);
        Assert.Equal("record", violation.Limit);
        Assert.Equal(1, violation.Ordinal);
    }

    [Fact]
    public async Task AHeaderOverTheHeaderLimitFailsAsAHeaderQuota()
    {
        var header = SnapshotBytes.WideObject(1_100_000);

        var violation = await Assert.ThrowsAsync<DataContractViolation>(
            () => SnapshotBytes.ReadAllAsync(
                SnapshotBytes.Document(header: header), TestContext.Current.CancellationToken));

        Assert.Equal(DataContractViolation.QuotaExceeded, violation.Code);
        Assert.Equal("header", violation.Limit);
    }

    [Fact]
    public async Task ADocumentDeeperThanTheDepthLimitFailsAsADepthQuota()
    {
        var deep = new StringBuilder("{\"a\":");
        for (var level = 0; level < DataContractLimits.Depth; level++)
        {
            deep.Append("{\"a\":");
        }

        deep.Append('1');
        for (var level = 0; level <= DataContractLimits.Depth; level++)
        {
            deep.Append('}');
        }

        var violation = await Assert.ThrowsAsync<DataContractViolation>(
            () => SnapshotBytes.ReadAllAsync(
                SnapshotBytes.Document(records: [deep.ToString()]), TestContext.Current.CancellationToken));

        Assert.Equal(DataContractViolation.QuotaExceeded, violation.Code);
        Assert.Equal("depth", violation.Limit);
    }

    [Fact]
    public async Task ASingleTokenOverTheBufferCapFailsAsATokenQuota()
    {
        var record = $$"""{"description":"{{new string('t', 1_200_000)}}"}""";

        var violation = await Assert.ThrowsAsync<DataContractViolation>(
            () => SnapshotBytes.ReadAllAsync(
                SnapshotBytes.Document(records: [record]), TestContext.Current.CancellationToken));

        Assert.Equal(DataContractViolation.QuotaExceeded, violation.Code);
        Assert.Equal("token", violation.Limit);
    }

    [Fact]
    public async Task AViolationNamesNoValueOfTheDocument()
    {
        var secret = "secret-value-that-must-not-be-reported";
        var document = SnapshotBytes.Utf8(
            $$"""{"contractVersion":"2.0","header":{"v":"{{secret}}"},"items":[]}""");

        var violation = await Assert.ThrowsAsync<DataContractViolation>(
            () => SnapshotBytes.ReadAllAsync(document, TestContext.Current.CancellationToken));

        Assert.DoesNotContain(secret, violation.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, violation.ToString(), StringComparison.Ordinal);
    }
}
