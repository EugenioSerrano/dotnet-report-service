using System.IO.Compression;
using ReportService.Data;

namespace ReportService.Data.Tests;

/// <summary>
/// What the reader says about a document it refuses: the reason the accepted sources name
/// for the fault the document carries, the ordinal it belongs to and the offset of the
/// bytes that carry it, together with the values it had already yielded. Every case is
/// read twice — the document as it is, and the same bytes inside one complete gzip stream —
/// and every case asserts the diagnostic carries no value of its own. The reasons the
/// reader already reported are asserted beside the new ones, so a change of classification
/// shows up here rather than on a job.
/// </summary>
public sealed class RefusalClassificationTests
{
    private const string Marker = "PLANTED-";

    [Fact]
    public async Task AHeaderAfterTheItemsFailsAsHeaderNotFirst()
    {
        var document = SnapshotBytes.DocumentOf(
            Version(),
            Root("items", RawRecord(1)),
            Root("header", HeaderObject()));

        await RefusedBothWaysAsync(document, "header-not-first", 0, SnapshotBytes.OffsetOf(document, "\"items\""), 0);
    }

    [Fact]
    public async Task ItemsBeforeTheHeaderWithTheVersionLastFailAsHeaderNotFirst()
    {
        var document = SnapshotBytes.DocumentOf(
            Root("items", RawRecord(1)),
            Root("header", HeaderObject()),
            Version());

        await RefusedBothWaysAsync(document, "header-not-first", 0, SnapshotBytes.OffsetOf(document, "\"items\""), 0);
    }

    [Fact]
    public async Task AMissingHeaderFailsAsHeaderNotFirst()
    {
        var document = SnapshotBytes.DocumentOf(Version(), Root("items", "[]"));

        await RefusedBothWaysAsync(document, "header-not-first", 0, SnapshotBytes.OffsetOf(document, "\"items\""), 0);
    }

    [Fact]
    public async Task AHeaderThatIsNotAnObjectFailsAsTypeMismatch()
    {
        var document = SnapshotBytes.DocumentOf(
            Version(),
            Root("header", HeaderArray()),
            Root("items", "[]"));

        await RefusedBothWaysAsync(document, "type-mismatch", 0, SnapshotBytes.OffsetOf(document, HeaderArray()), 0);
    }

    [Fact]
    public async Task ItemsThatAreNotAnArrayFailAsTypeMismatch()
    {
        var document = SnapshotBytes.DocumentOf(
            Version(),
            Root("header", HeaderObject()),
            Root("items", RawRecord(1)));

        await RefusedBothWaysAsync(document, "type-mismatch", 0, SnapshotBytes.OffsetOf(document, RawRecord(1)), 1);
    }

    [Fact]
    public async Task AMemberRepeatedInTheHeaderFailsAsDuplicateProperty()
    {
        var header = SnapshotBytes.Duplicated(HeaderObject(), "title", "\"" + Marker + "again\"");
        var document = SnapshotBytes.DocumentOf(Version(), Root("header", header), Root("items", "[]"));

        await RefusedBothWaysAsync(document, "duplicate-property", 0, SnapshotBytes.OffsetOf(document, "\"title\"", 2), 0);
    }

    [Fact]
    public async Task AMemberRepeatedInAMiddleRecordFailsAsDuplicateProperty()
    {
        var repeated = SnapshotBytes.Duplicated(RawRecord(2, "\"middletag\":\"" + Marker + "middle\""), "middletag", "\"" + Marker + "again\"");
        var document = SnapshotBytes.DocumentOf(
            Version(),
            Root("header", HeaderObject()),
            Root("items", "[" + string.Join(',', RawRecord(1), repeated, RawRecord(3)) + "]"));

        await RefusedBothWaysAsync(document, "duplicate-property", 2, SnapshotBytes.OffsetOf(document, "\"middletag\"", 2), 2);
    }

    [Fact]
    public async Task AMemberRepeatedInANestedObjectOfARecordFailsAsDuplicateProperty()
    {
        var inner = SnapshotBytes.Duplicated(
            "{\"deeptag\":\"" + Marker + "deep\"}",
            "deeptag",
            "\"" + Marker + "again\"");
        var repeated = "{\"sequence\":2,\"outer\":{\"inner\":" + inner + "}}";
        var document = SnapshotBytes.DocumentOf(
            Version(),
            Root("header", HeaderObject()),
            Root("items", "[" + string.Join(',', RawRecord(1), repeated, RawRecord(3)) + "]"));

        await RefusedBothWaysAsync(document, "duplicate-property", 2, SnapshotBytes.OffsetOf(document, "\"deeptag\"", 2), 2);
    }

    [Fact]
    public async Task AMemberRepeatedInTheLastOfThreeRecordsFailsAsDuplicateProperty()
    {
        var repeated = SnapshotBytes.Duplicated(RawRecord(3, "\"lasttag\":\"" + Marker + "last\""), "lasttag", "\"" + Marker + "again\"");
        var document = SnapshotBytes.DocumentOf(
            Version(),
            Root("header", HeaderObject()),
            Root("items", "[" + string.Join(',', RawRecord(1), RawRecord(2), repeated) + "]"));

        await RefusedBothWaysAsync(document, "duplicate-property", 3, SnapshotBytes.OffsetOf(document, "\"lasttag\"", 2), 3);
    }

    [Fact]
    public async Task AMemberRepeatedInTheLastOfTwoThousandRecordsFailsAsDuplicateProperty()
    {
        var records = SnapshotBytes.Records(2_000);
        records[1_999] = SnapshotBytes.Duplicated(
            RawRecord(2_000, "\"finaltag\":\"" + Marker + "last\""),
            "finaltag",
            "\"" + Marker + "again\"");
        var document = SnapshotBytes.DocumentOf(
            Version(),
            Root("header", HeaderObject()),
            Root("items", "[" + string.Join(',', records) + "]"));

        await RefusedBothWaysAsync(document, "duplicate-property", 2_000, SnapshotBytes.OffsetOf(document, "\"finaltag\"", 2), 2_000);
    }

    [Fact]
    public async Task TwoSpellingsOfOneNameInOneObjectAreOneMember()
    {
        var record = """{"name":"PLANTED-one","na\u006de":"PLANTED-two"}""";
        var document = SnapshotBytes.DocumentOf(Version(), Root("header", HeaderObject()), Root("items", "[" + record + "]"));

        await RefusedBothWaysAsync(document, "duplicate-property", 1, SnapshotBytes.OffsetOf(document, "\"na\\u006de\""), 1);
    }

    [Fact]
    public async Task TwoDifferentNamesOneOfThemEscapedInOneObjectAreAdmitted()
    {
        var record = """{"name":"PLANTED-one","o\u0074her":"PLANTED-two"}""";
        var document = SnapshotBytes.DocumentOf(Version(), Root("header", HeaderObject()), Root("items", "[" + record + "]"));

        await AcceptedBothWaysAsync(document, 2);
    }

    [Fact]
    public async Task TheSameNameInTwoDifferentObjectsOfOneRecordIsAdmitted()
    {
        var record = """{"left":{"x":1},"right":{"x":2}}""";
        var document = SnapshotBytes.DocumentOf(Version(), Root("header", HeaderObject()), Root("items", "[" + record + "]"));

        await AcceptedBothWaysAsync(document, 2);
    }

    [Fact]
    public async Task ARepeatedNameWhoseEscapeDoesNotDecodeStillIsOneMember()
    {
        var record = """{"\uD800":"PLANTED-one","\uD800":"PLANTED-two"}""";
        var document = SnapshotBytes.DocumentOf(Version(), Root("header", HeaderObject()), Root("items", "[" + record + "]"));

        await RefusedBothWaysAsync(document, "duplicate-property", 1, SnapshotBytes.OffsetOf(document, "\"\\uD800\"", 2), 1);
    }

    [Fact]
    public async Task ANameWhoseEscapeDoesNotDecodeIsReadAsItArrives()
    {
        var record = """{"\uD800":"PLANTED-one"}""";
        var document = SnapshotBytes.DocumentOf(Version(), Root("header", HeaderObject()), Root("items", "[" + record + "]"));

        var read = await AcceptedBothWaysAsync(document, 2);

        Assert.Equal(record, SnapshotBytes.Text(read[1]));
    }

    [Fact]
    public async Task ARepeatedVersionFailsAsDuplicateProperty()
    {
        var document = SnapshotBytes.DocumentOf(Version(), Version(), Root("header", "{}"), Root("items", "[]"));

        await RefusedBothWaysAsync(document, "duplicate-property", 0);
    }

    [Fact]
    public async Task ARepeatedHeaderFailsAsDuplicateProperty()
    {
        var document = SnapshotBytes.DocumentOf(Version(), Root("header", "{}"), Root("header", "{}"), Root("items", "[]"));

        await RefusedBothWaysAsync(document, "duplicate-property", 0);
    }

    [Fact]
    public async Task ARepeatedItemsPropertyFailsAsDuplicateProperty()
    {
        var document = SnapshotBytes.DocumentOf(Version(), Root("header", "{}"), Root("items", "[]"), Root("items", "[]"));

        await RefusedBothWaysAsync(document, "duplicate-property", 0);
    }

    [Fact]
    public async Task AFourthRootPropertyFailsAsProfileViolation()
    {
        var document = SnapshotBytes.DocumentOf(Version(), Root("header", "{}"), Root("items", "[]"), "\"extra\":1");

        await RefusedBothWaysAsync(document, "profile-violation", 0);
    }

    [Fact]
    public async Task AHeaderBeforeTheVersionFailsAsProfileViolation()
    {
        var document = SnapshotBytes.DocumentOf(Root("header", "{}"), Version(), Root("items", "[]"));

        await RefusedBothWaysAsync(document, "profile-violation", 0);
    }

    [Fact]
    public async Task AMissingVersionFailsAsProfileViolation()
    {
        var document = SnapshotBytes.DocumentOf(Root("header", "{}"), Root("items", "[]"));

        await RefusedBothWaysAsync(document, "profile-violation", 0);
    }

    [Fact]
    public async Task AMissingItemsFailsAsProfileViolation()
    {
        var document = SnapshotBytes.DocumentOf(Version(), Root("header", "{}"));

        await RefusedBothWaysAsync(document, "profile-violation", 0);
    }

    [Fact]
    public async Task AVersionOtherThanOneZeroFailsAsContractVersion()
    {
        var document = SnapshotBytes.DocumentOf(Version("\"2.0\""), Root("header", "{}"), Root("items", "[]"));

        await RefusedBothWaysAsync(document, "contract-version", 0);
    }

    [Fact]
    public async Task AVersionThatIsANumberFailsAsContractVersion()
    {
        var document = SnapshotBytes.DocumentOf(Version("1"), Root("header", "{}"), Root("items", "[]"));

        await RefusedBothWaysAsync(document, "contract-version", 0);
    }

    [Fact]
    public async Task ADocumentCutInsideTheHeaderFailsAsTruncated()
    {
        var document = SnapshotBytes.DocumentOf(Version(), Root("header", HeaderObject()), Root("items", "[]"));
        var cut = document[..(int)SnapshotBytes.OffsetOf(document, "\"title\"")];

        await RefusedBothWaysAsync(cut, "truncated", 0);
    }

    [Fact]
    public async Task ADocumentCutInsideAMiddleRecordFailsAsTruncated()
    {
        var document = SnapshotBytes.Document(records: SnapshotBytes.Records(3));
        var cut = document[..(int)SnapshotBytes.OffsetOf(document, "\"description\":\"row 2\"")];

        await RefusedBothWaysAsync(cut, "truncated", 2);
    }

    [Fact]
    public async Task ADocumentCutAfterTheLastRecordFailsAsTruncated()
    {
        var document = SnapshotBytes.Document(records: SnapshotBytes.Records(3));
        var cut = document[..(document.Length - 2)];

        await RefusedBothWaysAsync(cut, "truncated", 3);
    }

    [Fact]
    public async Task ADocumentCutAfterTheItemsArrayFailsAsTruncated()
    {
        var document = SnapshotBytes.Document(records: SnapshotBytes.Records(3));
        var cut = document[..(document.Length - 1)];

        await RefusedBothWaysAsync(cut, "truncated", 3);
    }

    [Fact]
    public async Task AByteAfterTheRootFailsAsTrailingContent()
    {
        var document = SnapshotBytes.Document(records: [SnapshotBytes.Record(1)]);
        var withTail = SnapshotBytes.Utf8(SnapshotBytes.Text(document) + "x");

        await RefusedBothWaysAsync(withTail, "trailing-content", 1);
    }

    [Fact]
    public async Task ASecondValueAfterTheRootFailsAsTrailingContent()
    {
        var document = SnapshotBytes.Document(records: [SnapshotBytes.Record(1)]);
        var withTail = SnapshotBytes.Utf8(SnapshotBytes.Text(document) + " {}");

        await RefusedBothWaysAsync(withTail, "trailing-content", 1);
    }

    [Fact]
    public async Task AnInvalidUtf8ByteFailsAsMalformed()
    {
        var document = SnapshotBytes.Document();
        var invalid = new byte[document.Length + 1];
        invalid[0] = document[0];
        invalid[1] = 0xFF;
        document.AsSpan(1).CopyTo(invalid.AsSpan(2));

        await RefusedBothWaysAsync(invalid, "malformed", 0);
    }

    [Fact]
    public async Task AnInvalidTokenFailsAsMalformed()
    {
        var document = SnapshotBytes.Utf8("""{"contractVersion":"1.0",,}""");

        await RefusedBothWaysAsync(document, "malformed", 0);
    }

    [Fact]
    public async Task AnItemsElementThatIsNotAnObjectFailsAsProfileViolation()
    {
        var document = SnapshotBytes.DocumentOf(Version(), Root("header", "{}"), Root("items", "[1]"));

        await RefusedBothWaysAsync(document, "profile-violation", 1);
    }

    [Fact]
    public async Task AValidSnapshotIsAdmitted()
    {
        var document = SnapshotBytes.Document(records:
            [
                RawRecord(1),
                RawRecord(2),
                RawRecord(3),
                RawRecord(4),
            ]);

        await AcceptedBothWaysAsync(document, 5);
    }

    [Fact]
    public async Task AnEmptyItemsArrayIsAdmitted()
    {
        var document = SnapshotBytes.Document();

        await AcceptedBothWaysAsync(document, 1);
    }

    [Fact]
    public async Task ASingleLeadingByteOrderMarkIsSkipped()
    {
        var document = SnapshotBytes.Document(records: [RawRecord(1)]);
        var withMark = new byte[document.Length + 3];
        withMark[0] = 0xEF;
        withMark[1] = 0xBB;
        withMark[2] = 0xBF;
        document.CopyTo(withMark, 3);

        await AcceptedBothWaysAsync(withMark, 2);
    }

    [Fact]
    public async Task WhitespaceAfterTheRootIsAdmitted()
    {
        var document = SnapshotBytes.Document(records: [RawRecord(1)]);
        var withWhitespace = SnapshotBytes.Utf8(SnapshotBytes.Text(document) + " \r\n\t ");

        await AcceptedBothWaysAsync(withWhitespace, 2);
    }

    [Fact]
    public async Task ASecondByteOrderMarkFailsAsMalformed()
    {
        var document = SnapshotBytes.Document();
        var withMarks = new byte[document.Length + 6];
        withMarks[0] = 0xEF;
        withMarks[1] = 0xBB;
        withMarks[2] = 0xBF;
        withMarks[3] = 0xEF;
        withMarks[4] = 0xBB;
        withMarks[5] = 0xBF;
        document.CopyTo(withMarks, 6);

        await RefusedBothWaysAsync(withMarks, "malformed", 0);
    }

    [Fact]
    public async Task AHeaderOfHalfAMegabyteIsAdmitted()
    {
        var header = SnapshotBytes.WideObject(512 * 1024);

        var read = await AcceptedBothWaysAsync(SnapshotBytes.Document(header: header), 1);

        Assert.Equal(header, SnapshotBytes.Text(read[0]));
    }

    private static string Version(string rawValue = "\"1.0\"") => "\"contractVersion\":" + rawValue;

    private static string Root(string name, string rawValue) => "\"" + name + "\":" + rawValue;

    private static string HeaderObject() => "{\"title\":\"" + Marker + "title\"}";

    private static string HeaderArray() => "[{\"title\":\"" + Marker + "title\"}]";

    private static string RawRecord(int ordinal, string extra = "")
    {
        var body = "\"sequence\":" + ordinal + ",\"description\":\"" + Marker + "row " + ordinal + "\",\"amount\":" + ordinal + ".25";
        return "{" + body + (extra.Length == 0 ? string.Empty : "," + extra) + "}";
    }

    private static async Task RefusedBothWaysAsync(byte[] document, string reason, int ordinal) =>
        await RefusedBothWaysAsync(document, reason, ordinal, null, null);

    private static async Task RefusedBothWaysAsync(byte[] document, string reason, int ordinal, long offset) =>
        await RefusedBothWaysAsync(document, reason, ordinal, offset, null);

    private static async Task RefusedBothWaysAsync(byte[] document, string reason, int ordinal, long? offset, int? valuesRead)
    {
        await RefusedAsync(new MemoryStream(document, writable: false), reason, ordinal, offset, valuesRead);
        await RefusedAsync(Compressed(document), reason, ordinal, offset, valuesRead);
    }

    private static async Task<DataContractViolation> RefusedAsync(Stream source, string reason, int ordinal, long? offset, int? valuesRead)
    {
        await using (source)
        {
            var read = new List<RawRecord>();
            var violation = await Assert.ThrowsAsync<DataContractViolation>(() => DrainAsync(source, read));

            Assert.Equal(DataContractViolation.DataInvalid, violation.Code);
            Assert.Equal(reason, violation.Reason);
            Assert.Null(violation.Limit);
            Assert.Equal(ordinal, violation.Ordinal);
            if (offset is not null)
            {
                Assert.Equal(offset.Value, violation.Offset);
            }

            if (valuesRead is not null)
            {
                Assert.Equal(valuesRead.Value, read.Count);
            }

            Assert.DoesNotContain(Marker, violation.Message, StringComparison.Ordinal);
            return violation;
        }
    }

    private static async Task<List<RawRecord>> AcceptedBothWaysAsync(byte[] document, int values)
    {
        var plain = await AcceptedAsync(new MemoryStream(document, writable: false));
        var compressed = await AcceptedAsync(Compressed(document));

        Assert.Equal(values, plain.Count);
        Assert.Equal(values, compressed.Count);
        Assert.Equal(string.Join('\n', plain.Select(SnapshotBytes.Text)), string.Join('\n', compressed.Select(SnapshotBytes.Text)));
        return plain;
    }

    private static async Task<List<RawRecord>> AcceptedAsync(Stream source)
    {
        await using (source)
        {
            var read = new List<RawRecord>();
            await DrainAsync(source, read);
            return read;
        }
    }

    private static async Task DrainAsync(Stream source, List<RawRecord> read)
    {
        await foreach (var value in HeaderItemsReader.ReadAsync(source, TestContext.Current.CancellationToken))
        {
            read.Add(value);
        }
    }

    private static Stream Compressed(byte[] document) =>
        new GZipStream(new MemoryStream(SnapshotBytes.Gzip(document), writable: false), CompressionMode.Decompress);
}
