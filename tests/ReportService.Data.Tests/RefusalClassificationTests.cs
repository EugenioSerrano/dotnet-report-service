using System.IO.Compression;
using System.Text;
using ReportService.Data;

namespace ReportService.Data.Tests;

/// <summary>
/// What the reader says when it refuses a document: the reason the profile names for
/// the fault the document carries, the ordinal and the byte offset it stopped at, and
/// how many values it had yielded by then. Every case is read twice — the document as
/// it is and the same bytes inside one complete gzip stream — because a consumer's
/// snapshot may arrive either way and the classification must not depend on which.
/// The cases that are admitted pin the other side of the same contract: no document is
/// refused for a fault it does not carry.
/// </summary>
public sealed class RefusalClassificationTests
{
    [Fact]
    public async Task AHeaderAfterTheItemsFailsAsHeaderNotFirst()
    {
        var document = SnapshotBytes.DocumentOf(
            "\"contractVersion\":\"1.0\"",
            "\"items\":[" + SnapshotBytes.Record(1) + "]",
            "\"header\":{\"title\":\"PLANTED-header\"}");

        await AssertRefusedAsync(document, "header-not-first", 0, SnapshotBytes.OffsetOf(document, "\"items\""), 0);
    }

    [Fact]
    public async Task ItemsBeforeTheHeaderWithTheVersionLastFailAsHeaderNotFirst()
    {
        var document = SnapshotBytes.DocumentOf(
            "\"items\":[" + SnapshotBytes.Record(1) + "]",
            "\"header\":{\"title\":\"PLANTED-header\"}",
            "\"contractVersion\":\"1.0\"");

        await AssertRefusedAsync(document, "header-not-first", 0, SnapshotBytes.OffsetOf(document, "\"items\""), 0);
    }

    [Fact]
    public async Task AHeaderThatIsNotAnObjectFailsAsTypeMismatch()
    {
        var document = SnapshotBytes.DocumentOf(
            "\"contractVersion\":\"1.0\"",
            "\"header\":[" + """{"title":"PLANTED-header"}""" + "]",
            "\"items\":[]");

        var at = SnapshotBytes.OffsetOf(document, "\"header\":") + "\"header\":".Length;
        await AssertRefusedAsync(document, "type-mismatch", 0, at, 0);
    }

    [Fact]
    public async Task ItemsThatAreNotAnArrayFailAsTypeMismatch()
    {
        var document = SnapshotBytes.DocumentOf(
            "\"contractVersion\":\"1.0\"",
            "\"header\":{\"title\":\"PLANTED-header\"}",
            "\"items\":{\"sequence\":1,\"description\":\"PLANTED-record\"}");

        var at = SnapshotBytes.OffsetOf(document, "\"items\":") + "\"items\":".Length;
        await AssertRefusedAsync(document, "type-mismatch", 0, at, 1);
    }

    [Fact]
    public async Task AMemberRepeatedInTheHeaderFailsAsDuplicateProperty()
    {
        var document = SnapshotBytes.Document(
            header: SnapshotBytes.Duplicated("""{"title":"PLANTED-title","currency":"EUR"}""", "title"));

        await AssertRefusedAsync(document, "duplicate-property", 0, SnapshotBytes.OffsetOf(document, "\"title\"", 1), 0);
    }

    [Fact]
    public async Task AMemberRepeatedInAMiddleRecordFailsAsDuplicateProperty()
    {
        var second = SnapshotBytes.Duplicated("""{"sequence":2,"marker":"PLANTED-second"}""", "marker");
        var document = SnapshotBytes.Document(records:
            [SnapshotBytes.Record(1), second, SnapshotBytes.Record(3)]);

        await AssertRefusedAsync(document, "duplicate-property", 2, SnapshotBytes.OffsetOf(document, "\"marker\"", 1), 2);
    }

    [Fact]
    public async Task AMemberRepeatedInANestedObjectOfARecordFailsAsDuplicateProperty()
    {
        var inner = SnapshotBytes.Duplicated("""{"stamp":"PLANTED-nested"}""", "stamp");
        var second = "{\"sequence\":2,\"detail\":{\"inner\":" + inner + "}}";
        var document = SnapshotBytes.Document(records:
            [SnapshotBytes.Record(1), second, SnapshotBytes.Record(3)]);

        await AssertRefusedAsync(document, "duplicate-property", 2, SnapshotBytes.OffsetOf(document, "\"stamp\"", 1), 2);
    }

    [Fact]
    public async Task AMemberRepeatedInTheLastOfThreeRecordsFailsAsDuplicateProperty()
    {
        var third = SnapshotBytes.Duplicated("""{"sequence":3,"tail":"PLANTED-third"}""", "tail");
        var document = SnapshotBytes.Document(records:
            [SnapshotBytes.Record(1), SnapshotBytes.Record(2), third]);

        await AssertRefusedAsync(document, "duplicate-property", 3, SnapshotBytes.OffsetOf(document, "\"tail\"", 1), 3);
    }

    [Fact]
    public async Task AMemberRepeatedInTheLastOfTwoThousandRecordsFailsAsDuplicateProperty()
    {
        var last = SnapshotBytes.Duplicated("""{"sequence":2000,"final":"PLANTED-last"}""", "final");
        var records = SnapshotBytes.Records(1_999);
        var document = SnapshotBytes.Document(records: [.. records, last]);

        await AssertRefusedAsync(document, "duplicate-property", 2_000, SnapshotBytes.OffsetOf(document, "\"final\"", 1), 2_000);
    }

    [Fact]
    public async Task TwoSpellingsOfOneNameInOneObjectAreOneMember()
    {
        var record = """{"name":"PLANTED-one","na\u006de":"PLANTED-two"}""";
        var document = SnapshotBytes.Document(records: [record]);

        await AssertRefusedAsync(document, "duplicate-property", 1, SnapshotBytes.OffsetOf(document, "\"na\\u006de\""), 1);
    }

    [Fact]
    public async Task NamesThatDifferInAnEscapeAreNotOneMember()
    {
        var record = """{"name":"PLANTED-one","na\u006de2":"PLANTED-two"}""";
        var document = SnapshotBytes.Document(records: [record]);

        await AssertAcceptedAsync(document, 2);
    }

    [Fact]
    public async Task TheSameNameInTwoObjectsOfOneRecordIsAdmitted()
    {
        var record = """{"a":{"x":1},"b":{"x":2}}""";
        var document = SnapshotBytes.Document(records: [record]);

        await AssertAcceptedAsync(document, 2);
    }

    [Fact]
    public async Task ARepeatedNameWhoseEscapeDoesNotDecodeStillIsOneMember()
    {
        var objectText = SnapshotBytes.Duplicated("""{"a":1}""", "\\uD800");
        var record = SnapshotBytes.Duplicated(objectText, "\\uD800");
        var document = SnapshotBytes.Document(records: [record]);

        await AssertRefusedAsync(document, "duplicate-property", 1, SnapshotBytes.OffsetOf(document, "\"\\uD800\"", 1), 1);
    }

    [Fact]
    public async Task ANameWhoseEscapeDoesNotDecodeIsReadAsItArrives()
    {
        var record = SnapshotBytes.Duplicated("""{"a":1}""", "\\uD800");
        var document = SnapshotBytes.Document(records: [record]);

        var refusal = await ClassifyAsync(document, compressed: false, TestContext.Current.CancellationToken);

        AssertAccepted(refusal, 2);
        Assert.Contains("\\uD800", SnapshotBytes.Text(refusal.Values[1]), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARepeatedContractVersionFailsAsDuplicateProperty()
    {
        var document = SnapshotBytes.Utf8(
            """{"contractVersion":"1.0","contractVersion":"1.0","header":{},"items":[]}""");

        await AssertRefusedAsAsync(document, "duplicate-property", 0);
    }

    [Fact]
    public async Task ARepeatedHeaderFailsAsDuplicateProperty()
    {
        var document = SnapshotBytes.Utf8(
            """{"contractVersion":"1.0","header":{},"header":{},"items":[]}""");

        await AssertRefusedAsAsync(document, "duplicate-property", 0);
    }

    [Fact]
    public async Task ARepeatedItemsFailsAsDuplicateProperty()
    {
        var document = SnapshotBytes.Utf8(
            """{"contractVersion":"1.0","header":{},"items":[],"items":[]}""");

        await AssertRefusedAsAsync(document, "duplicate-property", 0);
    }

    [Fact]
    public async Task AFourthRootPropertyFailsAsProfileViolation()
    {
        var document = SnapshotBytes.Utf8(
            """{"contractVersion":"1.0","header":{},"items":[],"extra":1}""");

        await AssertRefusedAsAsync(document, "profile-violation", 0);
    }

    [Fact]
    public async Task AHeaderBeforeTheContractVersionFailsAsProfileViolation()
    {
        var document = SnapshotBytes.Utf8("""{"header":{},"contractVersion":"1.0","items":[]}""");

        await AssertRefusedAsAsync(document, "profile-violation", 0);
    }

    [Fact]
    public async Task AMissingContractVersionFailsAsProfileViolation()
    {
        var document = SnapshotBytes.Utf8("""{"header":{},"items":[]}""");

        await AssertRefusedAsAsync(document, "profile-violation", 0);
    }

    [Fact]
    public async Task AMissingHeaderFailsAsProfileViolation()
    {
        var document = SnapshotBytes.Utf8("""{"contractVersion":"1.0","items":[]}""");

        await AssertRefusedAsAsync(document, "profile-violation", 0);
    }

    [Fact]
    public async Task AMissingItemsFailsAsProfileViolation()
    {
        var document = SnapshotBytes.Utf8("""{"contractVersion":"1.0","header":{}}""");

        await AssertRefusedAsAsync(document, "profile-violation", 0);
    }

    [Fact]
    public async Task AContractVersionOtherThanOneZeroFailsAsContractVersion()
    {
        var document = SnapshotBytes.Utf8("""{"contractVersion":"2.0","header":{},"items":[]}""");

        await AssertRefusedAsAsync(document, "contract-version", 0);
    }

    [Fact]
    public async Task AContractVersionThatIsANumberFailsAsContractVersion()
    {
        var document = SnapshotBytes.Utf8("""{"contractVersion":1,"header":{},"items":[]}""");

        await AssertRefusedAsAsync(document, "contract-version", 0);
    }

    [Fact]
    public async Task ADocumentTruncatedInsideTheHeaderFailsAsTruncated()
    {
        var document = SnapshotBytes.Utf8("""{"contractVersion":"1.0","header":{"title":"PLANTED""");

        await AssertRefusedAsAsync(document, "truncated", 0);
    }

    [Fact]
    public async Task ADocumentTruncatedInsideAMiddleRecordFailsAsTruncated()
    {
        var document = SnapshotBytes.Document(records: [SnapshotBytes.Record(1), SnapshotBytes.Record(2)]);
        var cut = document[..(SnapshotBytes.OffsetOf(document, "\"description\":\"row 2") + 8)];

        await AssertRefusedAsAsync(cut, "truncated", 2);
    }

    [Fact]
    public async Task ADocumentTruncatedAfterTheLastRecordFailsAsTruncated()
    {
        var document = SnapshotBytes.Document(records: [SnapshotBytes.Record(1), SnapshotBytes.Record(2)]);
        var cut = document[..(document.Length - 1)];

        await AssertRefusedAsAsync(cut, "truncated", 2);
    }

    [Fact]
    public async Task ADocumentTruncatedAfterTheItemsArrayFailsAsTruncated()
    {
        var document = SnapshotBytes.Document(records: [SnapshotBytes.Record(1)]);
        var cut = document[..(document.Length - 1)];

        await AssertRefusedAsAsync(cut, "truncated", 1);
    }

    [Fact]
    public async Task ANonWhitespaceByteAfterTheRootFailsAsTrailingContent()
    {
        var document = SnapshotBytes.Utf8("""{"contractVersion":"1.0","header":{},"items":[]}x""");

        await AssertRefusedAsAsync(document, "trailing-content", 0);
    }

    [Fact]
    public async Task ASecondJsonValueAfterTheRootFailsAsTrailingContent()
    {
        var document = SnapshotBytes.Utf8("""{"contractVersion":"1.0","header":{},"items":[]} {}""");

        await AssertRefusedAsAsync(document, "trailing-content", 0);
    }

    [Fact]
    public async Task InvalidUtf8FailsAsMalformed()
    {
        var document = SnapshotBytes.Document();
        var at = SnapshotBytes.OffsetOf(document, "\"items\":");
        var invalid = new byte[document.Length + 1];
        document.AsSpan(0, at).CopyTo(invalid);
        invalid[at] = 0xFF;
        document.AsSpan(at).CopyTo(invalid.AsSpan(at + 1));

        await AssertRefusedAsAsync(invalid, "malformed", 0);
    }

    [Fact]
    public async Task AnInvalidTokenFailsAsMalformed()
    {
        var document = SnapshotBytes.Utf8("""{"contractVersion":"1.0",,}""");

        await AssertRefusedAsAsync(document, "malformed", 0);
    }

    [Fact]
    public async Task AnItemThatIsNotAnObjectFailsAsProfileViolation()
    {
        var document = SnapshotBytes.Utf8("""{"contractVersion":"1.0","header":{},"items":[1]}""");

        await AssertRefusedAsAsync(document, "profile-violation", 1);
    }

    [Fact]
    public async Task AValidSnapshotIsAdmitted()
    {
        var records = new[]
        {
            """{"sequence":1,"description":"PLANTED-one","amount":1.25}""",
            """{"sequence":2,"description":"PLANTED-two","amount":2.25}""",
            """{"sequence":3,"description":"PLANTED-three","amount":3.25}""",
            """{"sequence":4,"description":"PLANTED-four","amount":4.25}""",
        };
        var document = SnapshotBytes.Document(
            header: """{"title":"PLANTED-title","currency":"EUR"}""",
            records: records);

        await AssertAcceptedAsync(document, 5);
    }

    [Fact]
    public async Task AnEmptyItemsArrayIsAdmitted()
    {
        var document = SnapshotBytes.Document(header: """{"title":"PLANTED-title"}""");

        await AssertAcceptedAsync(document, 1);
    }

    [Fact]
    public async Task AHeaderOfManyShortMembersIsAdmitted()
    {
        var document = SnapshotBytes.Document(header: SnapshotBytes.WideObject(512 * 1024));

        await AssertAcceptedAsync(document, 1);
    }

    [Fact]
    public async Task ASingleLeadingByteOrderMarkIsAdmitted()
    {
        var document = SnapshotBytes.Document(records: [SnapshotBytes.Record(1)]);
        var withMark = new byte[document.Length + 3];
        withMark[0] = 0xEF;
        withMark[1] = 0xBB;
        withMark[2] = 0xBF;
        document.CopyTo(withMark, 3);

        await AssertAcceptedAsync(withMark, 2);
    }

    [Fact]
    public async Task WhitespaceAfterTheRootIsAdmitted()
    {
        var document = SnapshotBytes.Utf8(
            Encoding.UTF8.GetString(SnapshotBytes.Document(header: """{"title":"PLANTED-title"}""")) + " \n\t ");

        await AssertAcceptedAsync(document, 1);
    }

    [Fact]
    public async Task ASecondByteOrderMarkFailsAsMalformed()
    {
        var document = SnapshotBytes.Document();
        var withTwoMarks = new byte[document.Length + 6];
        for (var index = 0; index < 2; index++)
        {
            withTwoMarks[(index * 3) + 0] = 0xEF;
            withTwoMarks[(index * 3) + 1] = 0xBB;
            withTwoMarks[(index * 3) + 2] = 0xBF;
        }

        document.CopyTo(withTwoMarks, 6);

        await AssertRefusedAsAsync(withTwoMarks, "malformed", 0);
    }

    private static Task AssertRefusedAsync(byte[] document, string reason, int ordinal, long offset, int values) =>
        AssertBothAsync(
            document,
            refusal => AssertRefused(refusal, reason, ordinal, offset, values),
            refusal => AssertRefused(refusal, reason, ordinal, offset, values));

    private static Task AssertRefusedAsAsync(byte[] document, string reason, int ordinal) =>
        AssertBothAsync(
            document,
            refusal => AssertRefusedAs(refusal, reason, ordinal),
            refusal => AssertRefusedAs(refusal, reason, ordinal));

    private static Task AssertAcceptedAsync(byte[] document, int values) =>
        AssertBothAsync(
            document,
            refusal => AssertAccepted(refusal, values),
            refusal => AssertAccepted(refusal, values));

    private static async Task AssertBothAsync(byte[] document, Action<Refusal> plain, Action<Refusal> compressed)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        plain(await ClassifyAsync(document, compressed: false, cancellationToken));
        compressed(await ClassifyAsync(document, compressed: true, cancellationToken));
    }

    private static async Task<Refusal> ClassifyAsync(byte[] document, bool compressed, CancellationToken cancellationToken)
    {
        var bytes = compressed ? SnapshotBytes.Gzip(document) : document;
        await using var content = new MemoryStream(bytes, writable: false);
        await using Stream source = compressed
            ? new GZipStream(content, CompressionMode.Decompress)
            : content;

        var values = new List<RawRecord>();
        try
        {
            await foreach (var value in HeaderItemsReader.ReadAsync(source, cancellationToken))
            {
                values.Add(value);
            }
        }
        catch (DataContractViolation violation)
        {
            return new Refusal(values, violation);
        }

        return new Refusal(values, null);
    }

    private static void AssertRefused(Refusal refusal, string reason, int ordinal, long offset, int values)
    {
        AssertRefusedAs(refusal, reason, ordinal);

        Assert.Equal(offset, refusal.Violation!.Offset);
        Assert.Equal(values, refusal.Values.Count);
        Assert.Equal(
            $"the document cannot be used: code={DataContractViolation.DataInvalid} reason={reason} ordinal={ordinal} offset={offset}",
            refusal.Violation.Message);
    }

    private static void AssertRefusedAs(Refusal refusal, string reason, int ordinal)
    {
        if (refusal.Violation is null)
        {
            Assert.Fail(
                $"no violation was thrown: the document was accepted and {refusal.Values.Count} value(s) were read; "
                + $"reason={reason} ordinal={ordinal} was expected.");
        }

        Assert.Equal(DataContractViolation.DataInvalid, refusal.Violation.Code);
        Assert.Equal(reason, refusal.Violation.Reason);
        Assert.Equal(ordinal, refusal.Violation.Ordinal);
        Assert.DoesNotContain("PLANTED", refusal.Violation.Message, StringComparison.Ordinal);
    }

    private static void AssertAccepted(Refusal refusal, int values)
    {
        Assert.Null(refusal.Violation);
        Assert.Equal(values, refusal.Values.Count);
    }

    /// <summary>What one reading of one document produced: the values it yielded and how it stopped.</summary>
    private sealed record Refusal(IReadOnlyList<RawRecord> Values, DataContractViolation? Violation);
}
