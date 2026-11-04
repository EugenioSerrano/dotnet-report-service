using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using ReportService.Data;

namespace ReportService.Data.Tests;

/// <summary>
/// How the reader classifies each fault it refuses: the reason word the data profile
/// names, the ordinal the case belongs to, the byte of the uncompressed document the
/// refusal points at and the values read before it — and never a value or a member
/// name of the document inside the diagnostic. Every document is read twice, plain and
/// inside a complete gzip stream, and the two readings must answer identically.
/// </summary>
public sealed class RefusalClassificationTests
{
    [Fact]
    public async Task AHeaderAfterTheItemsFailsAsHeaderNotFirst()
    {
        var header = """{"title":"PLANTED-VALUE-r1","nested":{"PLANTED-NAME-r1":1}}""";
        var record = """{"sequence":1,"description":"PLANTED-VALUE-r1-row","nested":{"PLANTED-NAME-r1-row":1}}""";
        var document = SnapshotBytes.DocumentOf(
            "\"contractVersion\":\"1.0\"",
            "\"items\":[" + record + "]",
            "\"header\":" + header);

        await AssertClassifiedAsync(
            document,
            "header-not-first",
            0,
            OffsetOf(document, "\"items\""),
            0,
            "PLANTED-VALUE-r1",
            "PLANTED-NAME-r1",
            "PLANTED-VALUE-r1-row",
            "PLANTED-NAME-r1-row");
    }

    [Fact]
    public async Task ItemsBeforeTheHeaderWithTheVersionLastFailAsHeaderNotFirst()
    {
        var header = """{"title":"PLANTED-VALUE-r2","nested":{"PLANTED-NAME-r2":1}}""";
        var record = """{"sequence":1,"description":"PLANTED-VALUE-r2-row","nested":{"PLANTED-NAME-r2-row":1}}""";
        var document = SnapshotBytes.DocumentOf(
            "\"items\":[" + record + "]",
            "\"header\":" + header,
            "\"contractVersion\":\"1.0\"");

        await AssertClassifiedAsync(
            document,
            "header-not-first",
            0,
            OffsetOf(document, "\"items\""),
            0,
            "PLANTED-VALUE-r2",
            "PLANTED-NAME-r2",
            "PLANTED-VALUE-r2-row",
            "PLANTED-NAME-r2-row");
    }

    [Fact]
    public async Task AHeaderThatIsNotAnObjectFailsAsTypeMismatch()
    {
        var document = SnapshotBytes.DocumentOf(
            "\"contractVersion\":\"1.0\"",
            "\"header\":[{\"title\":\"PLANTED-VALUE-r3a\",\"nested\":{\"PLANTED-NAME-r3a\":1}}]",
            "\"items\":[]");

        await AssertClassifiedAsync(
            document,
            "type-mismatch",
            0,
            OffsetAfter(document, "\"header\":"),
            0,
            "PLANTED-VALUE-r3a",
            "PLANTED-NAME-r3a");
    }

    [Fact]
    public async Task ItemsThatAreNotAnArrayFailAsTypeMismatch()
    {
        var header = """{"title":"PLANTED-VALUE-r3b","nested":{"PLANTED-NAME-r3b":1}}""";
        var items = """{"sequence":1,"description":"PLANTED-VALUE-r3b-row","nested":{"PLANTED-NAME-r3b-row":1}}""";
        var document = SnapshotBytes.DocumentOf(
            "\"contractVersion\":\"1.0\"",
            "\"header\":" + header,
            "\"items\":" + items);

        await AssertClassifiedAsync(
            document,
            "type-mismatch",
            0,
            OffsetAfter(document, "\"items\":"),
            1,
            "PLANTED-VALUE-r3b",
            "PLANTED-NAME-r3b",
            "PLANTED-VALUE-r3b-row",
            "PLANTED-NAME-r3b-row");
    }

    [Fact]
    public async Task AMemberRepeatedInTheHeaderFailsAsDuplicateProperty()
    {
        var header = SnapshotBytes.Duplicated("""{"title":"PLANTED-VALUE-r4a"}""", "PLANTED-NAME-r4a");
        var document = SnapshotBytes.Document(header: header);

        await AssertClassifiedAsync(
            document,
            "duplicate-property",
            0,
            OffsetOf(document, "\"PLANTED-NAME-r4a\"", 2),
            0,
            "PLANTED-VALUE-r4a",
            "PLANTED-NAME-r4a");
    }

    [Fact]
    public async Task AMemberRepeatedInAMiddleRecordFailsAsDuplicateProperty()
    {
        var second = SnapshotBytes.Duplicated("""{"sequence":2,"description":"PLANTED-VALUE-r4b"}""", "PLANTED-NAME-r4b");
        var document = SnapshotBytes.Document(records: [SnapshotBytes.Record(1), second, SnapshotBytes.Record(3)]);

        await AssertClassifiedAsync(
            document,
            "duplicate-property",
            2,
            OffsetOf(document, "\"PLANTED-NAME-r4b\"", 2),
            2,
            "PLANTED-VALUE-r4b",
            "PLANTED-NAME-r4b");
    }

    [Fact]
    public async Task AMemberRepeatedInANestedObjectOfARecordFailsAsDuplicateProperty()
    {
        var innermost = SnapshotBytes.Duplicated("""{"x":1}""", "PLANTED-NAME-r4c");
        var second = "{\"sequence\":2,\"description\":\"PLANTED-VALUE-r4c\",\"outer\":{\"middle\":"
            + innermost + "}}";
        var document = SnapshotBytes.Document(records: [SnapshotBytes.Record(1), second, SnapshotBytes.Record(3)]);

        await AssertClassifiedAsync(
            document,
            "duplicate-property",
            2,
            OffsetOf(document, "\"PLANTED-NAME-r4c\"", 2),
            2,
            "PLANTED-VALUE-r4c",
            "PLANTED-NAME-r4c");
    }

    [Fact]
    public async Task AMemberRepeatedInTheLastOfThreeRecordsFailsAsDuplicateProperty()
    {
        var third = SnapshotBytes.Duplicated("""{"sequence":3,"description":"PLANTED-VALUE-r4d"}""", "PLANTED-NAME-r4d");
        var document = SnapshotBytes.Document(records: [SnapshotBytes.Record(1), SnapshotBytes.Record(2), third]);

        await AssertClassifiedAsync(
            document,
            "duplicate-property",
            3,
            OffsetOf(document, "\"PLANTED-NAME-r4d\"", 2),
            3,
            "PLANTED-VALUE-r4d",
            "PLANTED-NAME-r4d");
    }

    [Fact]
    public async Task AMemberRepeatedInTheLastOfTwoThousandRecordsFailsAsDuplicateProperty()
    {
        var last = SnapshotBytes.Duplicated("""{"sequence":2000,"description":"PLANTED-VALUE-r4e"}""", "PLANTED-NAME-r4e");
        var records = SnapshotBytes.Records(1999);
        var document = SnapshotBytes.Document(records: [.. records, last]);

        await AssertClassifiedAsync(
            document,
            "duplicate-property",
            2000,
            OffsetOf(document, "\"PLANTED-NAME-r4e\"", 2),
            2000,
            "PLANTED-VALUE-r4e",
            "PLANTED-NAME-r4e");
    }

    [Fact]
    public async Task TwoSpellingsOfOneNameInOneObjectAreOneMember()
    {
        var record = """{"name":1,"na\u006de":2}""";
        var document = SnapshotBytes.Document(records: [record]);

        await AssertClassifiedAsync(
            document,
            "duplicate-property",
            1,
            OffsetOf(document, "\"na\\u006de\""),
            1);
    }

    [Fact]
    public async Task TwoDifferentNamesThatDifferByAnEscapeAreTwoMembers()
    {
        var record = """{"name":1,"n\u0061me2":2}""";
        var document = SnapshotBytes.Document(records: [record]);

        var read = await AssertAcceptedAsync(document);

        Assert.Equal(2, read.Count);
        Assert.Equal(record, SnapshotBytes.Text(read[1]));
    }

    [Fact]
    public async Task TheSameNameInTwoDifferentObjectsOfOneRecordIsAdmitted()
    {
        var record = """{"a":{"x":1},"b":{"x":2}}""";
        var document = SnapshotBytes.Document(records: [record]);

        var read = await AssertAcceptedAsync(document);

        Assert.Equal(2, read.Count);
        Assert.Equal(record, SnapshotBytes.Text(read[1]));
    }

    [Fact]
    public async Task ARepeatedNameWhoseEscapeDoesNotDecodeStillIsOneMember()
    {
        var record = """{"\uD800":1,"\uD800":2}""";
        var document = SnapshotBytes.Document(records: [record]);

        await AssertClassifiedAsync(
            document,
            "duplicate-property",
            1,
            OffsetOf(document, "\"\\uD800\"", 2),
            1);
    }

    [Fact]
    public async Task ANameWhoseEscapeDoesNotDecodeIsStillAdmitted()
    {
        var record = """{"\uD800":1,"other":2}""";
        var document = SnapshotBytes.Document(records: [record]);

        var read = await AssertAcceptedAsync(document);

        Assert.Equal(2, read.Count);
        Assert.Equal(record, SnapshotBytes.Text(read[1]));
    }

    [Fact]
    public async Task AMissingHeaderFailsAsHeaderNotFirst()
    {
        var record = """{"sequence":1,"description":"PLANTED-VALUE-headerless","nested":{"PLANTED-NAME-headerless":1}}""";
        var document = SnapshotBytes.DocumentOf(
            "\"contractVersion\":\"1.0\"",
            "\"items\":[" + record + "]");

        await AssertClassifiedAsync(
            document,
            "header-not-first",
            0,
            OffsetOf(document, "\"items\""),
            0,
            "PLANTED-VALUE-headerless",
            "PLANTED-NAME-headerless");
    }

    [Fact]
    public async Task ARepeatedContractVersionAtTheRootFailsAsDuplicateProperty()
    {
        var document = SnapshotBytes.Utf8(
            """{"contractVersion":"1.0","contractVersion":"1.0","header":{},"items":[]}""");

        await AssertRefusedAsync(document, "duplicate-property", 0);
    }

    [Fact]
    public async Task ARepeatedHeaderAtTheRootFailsAsDuplicateProperty()
    {
        var document = SnapshotBytes.Utf8(
            """{"contractVersion":"1.0","header":{},"header":{},"items":[]}""");

        await AssertRefusedAsync(document, "duplicate-property", 0);
    }

    [Fact]
    public async Task ARepeatedItemsAtTheRootFailsAsDuplicateProperty()
    {
        var document = SnapshotBytes.Utf8(
            """{"contractVersion":"1.0","header":{},"items":[],"items":[]}""");

        await AssertRefusedAsync(document, "duplicate-property", 0);
    }

    [Fact]
    public async Task AFourthRootMemberFailsAsProfileViolation()
    {
        var document = SnapshotBytes.Utf8(
            """{"contractVersion":"1.0","header":{},"items":[],"extra":1}""");

        await AssertRefusedAsync(document, "profile-violation", 0);
    }

    [Fact]
    public async Task AHeaderBeforeTheContractVersionStaysProfileViolation()
    {
        var document = SnapshotBytes.Utf8("""{"header":{},"contractVersion":"1.0","items":[]}""");

        await AssertRefusedAsync(document, "profile-violation", 0);
    }

    [Fact]
    public async Task ARootWithoutAContractVersionStaysProfileViolation()
    {
        // The profile admits no root property before the contract version, so a document
        // that carries none refuses at the first property it does carry.
        var document = SnapshotBytes.Utf8("""{"header":{},"items":[]}""");

        await AssertRefusedAsync(document, "profile-violation", 0);
    }

    [Fact]
    public async Task AMissingItemsStaysProfileViolation()
    {
        var document = SnapshotBytes.Utf8("""{"contractVersion":"1.0","header":{}}""");

        await AssertRefusedAsync(document, "profile-violation", 0);
    }

    [Fact]
    public async Task AVersionOtherThanOneZeroStaysContractVersion()
    {
        var document = SnapshotBytes.Utf8("""{"contractVersion":"2.0","header":{},"items":[]}""");

        await AssertRefusedAsync(document, "contract-version", 0);
    }

    [Fact]
    public async Task AVersionThatIsNotTextStaysContractVersion()
    {
        var document = SnapshotBytes.Utf8("""{"contractVersion":1,"header":{},"items":[]}""");

        await AssertRefusedAsync(document, "contract-version", 0);
    }

    [Fact]
    public async Task AHeaderCutShortStaysTruncated()
    {
        var document = SnapshotBytes.Utf8("""{"contractVersion":"1.0","header":{"title":"cut""");

        await AssertRefusedAsync(document, "truncated", 0);
    }

    [Fact]
    public async Task AMiddleRecordCutShortStaysTruncated()
    {
        var document = SnapshotBytes.Utf8(
            """{"contractVersion":"1.0","header":{},"items":[{"sequence":1,"description":"row 1"},{"sequence":2,"descrip""");

        await AssertRefusedAsync(document, "truncated", 2);
    }

    [Fact]
    public async Task ADocumentCutAfterTheLastRecordStaysTruncated()
    {
        var document = SnapshotBytes.Utf8(
            """{"contractVersion":"1.0","header":{},"items":[{"sequence":1,"description":"row 1"}""");

        await AssertRefusedAsync(document, "truncated", 1);
    }

    [Fact]
    public async Task ADocumentCutAfterTheItemsArrayStaysTruncated()
    {
        var document = SnapshotBytes.Utf8("""{"contractVersion":"1.0","header":{},"items":[]""");

        await AssertRefusedAsync(document, "truncated", 0);
    }

    [Fact]
    public async Task ANonWhitespaceByteAfterTheRootStaysTrailingContent()
    {
        var document = SnapshotBytes.Utf8(
            """{"contractVersion":"1.0","header":{},"items":[]}x""");

        await AssertRefusedAsync(document, "trailing-content", 0);
    }

    [Fact]
    public async Task ASecondValueAfterTheRootStaysTrailingContent()
    {
        var document = SnapshotBytes.Utf8(
            """{"contractVersion":"1.0","header":{},"items":[]} {}""");

        await AssertRefusedAsync(document, "trailing-content", 0);
    }

    [Fact]
    public async Task InvalidUtf8StaysMalformed()
    {
        // A byte that is not valid UTF-8 where a value must start: the document is not
        // text of the profile at all.
        var prefix = SnapshotBytes.Utf8("{\"contractVersion\":\"1.0\",\"header\":{\"a\":");
        var suffix = SnapshotBytes.Utf8("},\"items\":[]}");
        var document = new byte[prefix.Length + 1 + suffix.Length];
        prefix.CopyTo(document, 0);
        document[prefix.Length] = 0xFF;
        suffix.CopyTo(document, prefix.Length + 1);

        await AssertRefusedAsync(document, "malformed", 0);
    }

    [Fact]
    public async Task AnInvalidTokenStaysMalformed()
    {
        var document = SnapshotBytes.Utf8(
            """{"contractVersion":"1.0","header":{"a":undefined},"items":[]}""");

        await AssertRefusedAsync(document, "malformed", 0);
    }

    [Fact]
    public async Task AnItemsElementThatIsNotAnObjectStaysProfileViolation()
    {
        var document = SnapshotBytes.Utf8("""{"contractVersion":"1.0","header":{},"items":[1]}""");

        await AssertRefusedAsync(document, "profile-violation", 1);
    }

    [Fact]
    public async Task AValidSnapshotIsStillAdmitted()
    {
        var header = """{"title":"PLANTED-VALUE-c1","PLANTED-NAME-c1":1}""";
        var records = new[]
        {
            """{"sequence":1,"description":"PLANTED-VALUE-c1-1","nested":{"PLANTED-NAME-c1-1":1}}""",
            """{"sequence":2,"description":"PLANTED-VALUE-c1-2","nested":{"PLANTED-NAME-c1-2":1}}""",
            """{"sequence":3,"description":"PLANTED-VALUE-c1-3","nested":{"PLANTED-NAME-c1-3":1}}""",
            """{"sequence":4,"description":"PLANTED-VALUE-c1-4","nested":{"PLANTED-NAME-c1-4":1}}""",
        };
        var document = SnapshotBytes.Document(header, records);

        var read = await AssertAcceptedAsync(document);

        Assert.Equal(5, read.Count);
        Assert.Equal(header, SnapshotBytes.Text(read[0]));
        Assert.Equal(4, read[4].Ordinal);
    }

    [Fact]
    public async Task AnEmptyItemsArrayIsStillAdmitted()
    {
        var read = await AssertAcceptedAsync(SnapshotBytes.Document());

        var header = Assert.Single(read);
        Assert.Equal(0, header.Ordinal);
    }

    [Fact]
    public async Task ASingleLeadingByteOrderMarkIsStillAdmitted()
    {
        var document = SnapshotBytes.Document(records: [SnapshotBytes.Record(1)]);
        var withMark = new byte[document.Length + 3];
        withMark[0] = 0xEF;
        withMark[1] = 0xBB;
        withMark[2] = 0xBF;
        document.CopyTo(withMark, 3);

        var read = await AssertAcceptedAsync(withMark);

        Assert.Equal(2, read.Count);
    }

    [Fact]
    public async Task TrailingWhitespaceIsStillAdmitted()
    {
        var document = SnapshotBytes.Document(records: [SnapshotBytes.Record(1)]);
        var withTail = SnapshotBytes.Utf8(Encoding.UTF8.GetString(document) + " \n\t");

        var read = await AssertAcceptedAsync(withTail);

        Assert.Equal(2, read.Count);
    }

    [Fact]
    public async Task ASecondByteOrderMarkStaysMalformed()
    {
        var document = SnapshotBytes.Document(records: [SnapshotBytes.Record(1)]);
        var withMarks = new byte[document.Length + 6];
        withMarks[0] = 0xEF;
        withMarks[1] = 0xBB;
        withMarks[2] = 0xBF;
        withMarks[3] = 0xEF;
        withMarks[4] = 0xBB;
        withMarks[5] = 0xBF;
        document.CopyTo(withMarks, 6);

        await AssertRefusedAsync(withMarks, "malformed", 0);
    }

    [Fact]
    public async Task ARecordOfManyShortMembersIsStillAdmitted()
    {
        var wide = SnapshotBytes.WideObject(512 * 1024);
        var document = SnapshotBytes.Document(records: [wide]);

        var elapsed = Stopwatch.StartNew();
        var read = await AssertAcceptedAsync(document);
        elapsed.Stop();

        Assert.Equal(2, read.Count);
        Assert.Equal(wide, SnapshotBytes.Text(read[1]));
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"a record of {wide.Length:N0} characters of short members was read in {elapsed.Elapsed.TotalMilliseconds:F0} ms");
    }

    // A matrix case: the code, the reason, the ordinal, the byte of the uncompressed document,
    // the values read before the refusal, and no marker of the case's own document in the
    // message — both the value marker and the member-name marker.
    private static async Task AssertClassifiedAsync(
        byte[] document,
        string reason,
        int ordinal,
        long offset,
        int valuesBefore,
        params string[] markers)
    {
        for (var compressed = 0; compressed < 2; compressed++)
        {
            var (read, violation) = await ReadUntilRefusedAsync(document, compressed == 1);
            Assert.True(violation is not null, $"the document has to be refused with {reason}");
            Assert.Equal(DataContractViolation.DataInvalid, violation!.Code);
            Assert.Equal(reason, violation.Reason);
            Assert.Equal(ordinal, violation.Ordinal);
            Assert.Equal(offset, violation.Offset);
            Assert.Equal(valuesBefore, read.Count);
            AssertNoMarker(violation, markers);
        }
    }

    // A case whose document cannot fix the offset of its own refusal: the reason and the
    // ordinal are what it asserts, read the same way twice.
    private static async Task AssertRefusedAsync(
        byte[] document,
        string reason,
        int ordinal,
        params string[] markers)
    {
        for (var compressed = 0; compressed < 2; compressed++)
        {
            var (_, violation) = await ReadUntilRefusedAsync(document, compressed == 1);
            Assert.True(violation is not null, $"the document has to be refused with {reason}");
            Assert.Equal(DataContractViolation.DataInvalid, violation!.Code);
            Assert.Equal(reason, violation.Reason);
            Assert.Equal(ordinal, violation.Ordinal);
            AssertNoMarker(violation, markers);
        }
    }

    private static async Task<List<RawRecord>> AssertAcceptedAsync(byte[] document)
    {
        var plain = await ReadAllAsync(document, compressed: false);
        var gzip = await ReadAllAsync(document, compressed: true);
        Assert.Equal(
            plain.Select(record => SnapshotBytes.Text(record)),
            gzip.Select(record => SnapshotBytes.Text(record)));
        return plain;
    }

    private static void AssertNoMarker(DataContractViolation violation, string[] markers)
    {
        foreach (var marker in markers)
        {
            Assert.DoesNotContain(marker, violation.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(marker, violation.ToString(), StringComparison.Ordinal);
        }
    }

    private static async Task<(List<RawRecord> Read, DataContractViolation? Violation)> ReadUntilRefusedAsync(
        byte[] document,
        bool compressed)
    {
        var read = new List<RawRecord>();
        DataContractViolation? violation = null;
        try
        {
            await using var source = Open(document, compressed);
            await foreach (var value in HeaderItemsReader.ReadAsync(source, TestContext.Current.CancellationToken))
            {
                read.Add(value);
            }
        }
        catch (DataContractViolation refused)
        {
            violation = refused;
        }

        return (read, violation);
    }

    private static async Task<List<RawRecord>> ReadAllAsync(byte[] document, bool compressed)
    {
        await using var source = Open(document, compressed);
        var read = new List<RawRecord>();
        await foreach (var value in HeaderItemsReader.ReadAsync(source, TestContext.Current.CancellationToken))
        {
            read.Add(value);
        }

        return read;
    }

    private static Stream Open(byte[] document, bool compressed)
    {
        var plain = new MemoryStream(compressed ? SnapshotBytes.Gzip(document) : document, writable: false);
        return compressed ? new GZipStream(plain, CompressionMode.Decompress) : plain;
    }

    /// <summary>The byte index of the <paramref name="occurrence"/>-th <paramref name="text"/> of the document.</summary>
    private static long OffsetOf(byte[] document, string text, int occurrence = 1)
    {
        var needle = Encoding.UTF8.GetBytes(text);
        var from = 0;
        var index = -1;
        for (var found = 0; found < occurrence; found++)
        {
            index = document.AsSpan(from).IndexOf(needle);
            Assert.True(index >= 0, $"the document does not carry {text} {occurrence} time(s)");
            index += from;
            from = index + needle.Length;
        }

        return index;
    }

    /// <summary>The byte index just after the first <paramref name="prefix"/> of the document.</summary>
    private static long OffsetAfter(byte[] document, string prefix) =>
        OffsetOf(document, prefix) + Encoding.UTF8.GetByteCount(prefix);
}
