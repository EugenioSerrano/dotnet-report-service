using System.Diagnostics;
using System.Globalization;
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
            $"a record of {wide.Length:N0} characters of short members was read in {elapsed.Elapsed.TotalMilliseconds.ToString("F0", CultureInfo.InvariantCulture)} ms");
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

/// <summary>
/// The collection the two cost measurements run in. A measurement of the process's own time
/// and heap cannot share the process with another class's work: a full collection and a timed
/// read are properties of the whole process, so these cases run alone.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ReaderCostCollection
{
    /// <summary>The name every cost case is collected under.</summary>
    public const string Name = "reader cost";
}

/// <summary>
/// What the reader costs while it walks a document the profile admits: a tiny value that
/// follows a wide one is charged for its own names and never for the wide one, and a pass
/// keeps only the values it has open rather than everything it has read. The first is
/// measured as a difference of two documents of the same byte length, so what they share —
/// the input buffer, the captured value, the large-object heap and the collection they
/// provoke — cancels instead of being charged to the tiny values.
/// </summary>
[Collection(ReaderCostCollection.Name)]
public sealed class ReaderCostTests
{
    private const int Siblings = 5_000;

    private const int WarmUpRounds = 3;

    private const int MeasuredRounds = 9;

    private const int TrivialRecords = 400;

    private const long RetainedHeapBoundBytes = 8L * 1024 * 1024;

    // 5 µs per open over the 5 000 opens of the measured document, as the round's total.
    private const double MedianDifferenceBoundMilliseconds = 25.0;

    // The bytes the baseline spends before and after the one string value it carries in place
    // of the 5 000 members: ,"pad":" and the closing quote. Bounded with the value below, the
    // two documents are the same number of bytes.
    private const string PaddingPrefix = ",\"pad\":\"";

    private static readonly string[] TinyMembers =
        [.. Enumerable.Range(1, Siblings).Select(index => "\"t" + index + "\":{\"x\":0}")];

    [Fact]
    public async Task ATinyValueAfterAWideOneIsChargedForItsOwnNames()
    {
        var (measured, baseline, observation) = PerOpenDocuments();

        // The two operands differ by the 5 000 one-member objects and by nothing else:
        // they are the same number of bytes, they carry the same wide value with the same
        // names, and they are read by the same code in the same round.
        Assert.Equal(measured.Length, baseline.Length);

        await AssertAcceptedAsync(measured, expected: 2);
        await AssertAcceptedAsync(baseline, expected: 2);
        await AssertAcceptedAsync(observation, expected: 2);

        // Three rounds pay what the runtime pays once — the first full read of the parsing
        // loop is instrumented, and tiering never goes back — and are discarded. Their
        // differences are printed beside the measured ones so the transition is visible
        // outside the sample.
        var warmUps = new double[WarmUpRounds];
        for (var round = 0; round < WarmUpRounds; round++)
        {
            warmUps[round] = await ReadMillisecondsAsync(measured) - await ReadMillisecondsAsync(baseline);
        }

        // Nine measured rounds, each reading both operands once and draining what they
        // yield, with the order alternating so neither operand is always the one that pays
        // for a tier or a cache the other warmed.
        var differences = new double[MeasuredRounds];
        var measuredTotals = new double[MeasuredRounds];
        var baselineTotals = new double[MeasuredRounds];
        for (var round = 0; round < MeasuredRounds; round++)
        {
            var measuredFirst = round % 2 == 0;
            var first = await ReadMillisecondsAsync(measuredFirst ? measured : baseline);
            var second = await ReadMillisecondsAsync(measuredFirst ? baseline : measured);
            measuredTotals[round] = measuredFirst ? first : second;
            baselineTotals[round] = measuredFirst ? second : first;
            differences[round] = measuredTotals[round] - baselineTotals[round];
        }

        // The same 5 000 members with no wide value: recorded as an observation, and never
        // an operand of the bound.
        var observationMilliseconds = await ReadMillisecondsAsync(observation);

        var medianDifference = Median(differences);
        var perOpen = medianDifference / Siblings * 1_000;
        var measuredMedian = Median(measuredTotals);
        var baselineMedian = Median(baselineTotals);
        var summary =
            $"a tiny value after a wide one: the median of the nine round differences is {Number(medianDifference)} ms"
            + $" over {Siblings.ToString("N0", CultureInfo.InvariantCulture)} opens, {Number(perOpen)} µs per open"
            + $" (the bound is {Number(MedianDifferenceBoundMilliseconds)} ms);"
            + $" rounds {Numbers(differences)} ms; warm-ups {Numbers(warmUps)} ms;"
            + $" operand medians {Number(measuredMedian)} / {Number(baselineMedian)} ms,"
            + $" observation {Number(observationMilliseconds)} ms";
        Console.WriteLine(summary);
        TestContext.Current.TestOutputHelper?.WriteLine(summary);

        Assert.True(
            medianDifference <= MedianDifferenceBoundMilliseconds,
            $"a tiny value after a wide one costs {Number(perOpen)} µs per open: the median of the nine round"
            + $" differences is {Number(medianDifference)} ms over {Siblings.ToString("N0", CultureInfo.InvariantCulture)} opens"
            + $" against the {Number(MedianDifferenceBoundMilliseconds)} ms bound; rounds {Numbers(differences)} ms,"
            + $" warm-ups {Numbers(warmUps)} ms, operand medians {Number(measuredMedian)} / {Number(baselineMedian)} ms");
    }

    [Fact]
    public async Task AWholePassKeepsOnlyTheValuesItHasOpen()
    {
        var wideHeader = "{\"level1\":{\"level2\":" + SnapshotBytes.WideObject(512 * 1024) + "}}";
        var trivial = SnapshotBytes.Records(TrivialRecords);
        var wideRecords = WideRecords();
        var document = SnapshotBytes.Document(wideHeader, [.. trivial, .. wideRecords]);
        var control = SnapshotBytes.Document(records: trivial);

        await AssertAcceptedAsync(document, expected: 1 + TrivialRecords + wideRecords.Length);
        await AssertAcceptedAsync(control, expected: 1 + TrivialRecords);

        // What one trivial record costs, on this document and on the paired one that has no
        // wide value at all: an observation beside the bound, not a bound of its own — the
        // rejected delivery's ratio was 1.02, so this half tells the two apart in nothing.
        var tail = await RecordMeanMillisecondsAsync(document, TrivialRecords);
        var flat = await RecordMeanMillisecondsAsync(control, TrivialRecords);

        // A second read of the same document, drained and discarded, sampled after the wide
        // header value, after each record that reaches a depth not yet seen (ten of the
        // twelve) and after the last value: the heap before the pass is the baseline, so the
        // document itself is never in the delta, and the largest delta is what the bound is
        // asserted on.
        var checkpoints = new HashSet<int> { 0, 1 + TrivialRecords + wideRecords.Length - 1 };
        for (var depth = 0; depth < wideRecords.Length - 2; depth++)
        {
            checkpoints.Add(1 + TrivialRecords + depth);
        }

        var retained = await LargestRetainedBytesAsync(document, checkpoints);
        var summary =
            $"a whole pass: it retained {Number(retained / (1024.0 * 1024.0))} MiB at its largest checkpoint"
            + $" of {checkpoints.Count.ToString(CultureInfo.InvariantCulture)};"
            + $" a trivial record costs {Number(tail * 1_000)} µs, the paired control's mean is {Number(flat * 1_000)} µs";
        Console.WriteLine(summary);
        TestContext.Current.TestOutputHelper?.WriteLine(summary);

        Assert.True(
            retained <= RetainedHeapBoundBytes,
            $"the pass retained {Number(retained / (1024.0 * 1024.0))} MiB at its largest checkpoint"
            + $" against the {(RetainedHeapBoundBytes / (1024.0 * 1024.0)).ToString("F0", CultureInfo.InvariantCulture)} MiB bound"
            + $" (a trivial record costs {Number(tail * 1_000)} µs, the paired control's mean is {Number(flat * 1_000)} µs)");
    }

    // The two operands of the per-open measurement and the observation. The measured
    // document carries the wide value and then 5 000 one-member objects at the same depth;
    // the baseline carries the same wide value and, in place of those objects, one string
    // value long enough to make the two documents the same number of bytes.
    private static (byte[] Measured, byte[] Baseline, byte[] Observation) PerOpenDocuments()
    {
        var wide = SnapshotBytes.WideObject(512 * 1024);
        var members = string.Join(',', TinyMembers);
        var padding = new string('p', members.Length - PaddingPrefix.Length);
        var measured = SnapshotBytes.Document(records: ["{\"wide\":" + wide + "," + members + "}"]);
        var baseline = SnapshotBytes.Document(records: ["{\"wide\":" + wide + PaddingPrefix + padding + "\"}"]);
        var observation = SnapshotBytes.Document(records: ["{" + members + "}"]);
        return (measured, baseline, observation);
    }

    // Ten depths from three to twelve, and two of them read twice: twelve records that put a
    // wide value at one depth each.
    private static string[] WideRecords()
    {
        var depths = new List<int>();
        for (var depth = 3; depth <= 12; depth++)
        {
            depths.Add(depth);
        }

        depths.Add(3);
        depths.Add(12);
        return [.. depths.Select(WideRecordAtDepth)];
    }

    // The record's object sits at depth 2, so the wide value sits at `depth` after
    // `depth - 3` wrapper objects.
    private static string WideRecordAtDepth(int depth)
    {
        var value = SnapshotBytes.WideObject(900 * 1024);
        for (var level = 3; level < depth; level++)
        {
            value = "{\"level" + level + "\":" + value + "}";
        }

        return "{\"sequence\":" + depth + ",\"wide\":" + value + "}";
    }

    private static async Task AssertAcceptedAsync(byte[] document, int expected)
    {
        var plain = await SnapshotBytes.ReadAllAsync(document, TestContext.Current.CancellationToken);
        Assert.Equal(expected, plain.Count);

        await using var compressed = new GZipStream(
            new MemoryStream(SnapshotBytes.Gzip(document), writable: false),
            CompressionMode.Decompress);
        var read = new List<RawRecord>();
        await foreach (var value in HeaderItemsReader.ReadAsync(compressed, TestContext.Current.CancellationToken))
        {
            read.Add(value);
        }

        Assert.Equal(
            plain.Select(record => SnapshotBytes.Text(record)),
            read.Select(record => SnapshotBytes.Text(record)));
    }

    // One read of the whole document with every value drained and discarded, timed.
    private static async Task<double> ReadMillisecondsAsync(byte[] document)
    {
        var started = Stopwatch.GetTimestamp();
        await SnapshotBytes.ReadAllAsync(document, TestContext.Current.CancellationToken);
        return Stopwatch.GetElapsedTime(started).TotalMilliseconds;
    }

    // The mean of each document's records after its first value, read once.
    private static async Task<double> RecordMeanMillisecondsAsync(byte[] document, int count)
    {
        await using var source = new MemoryStream(document, writable: false);
        var index = 0;
        var started = 0L;
        var elapsed = 0.0;
        await foreach (var _ in HeaderItemsReader.ReadAsync(source, TestContext.Current.CancellationToken))
        {
            if (index == 0)
            {
                started = Stopwatch.GetTimestamp();
            }
            else if (index == count)
            {
                elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                break;
            }

            index++;
        }

        return elapsed / count;
    }

    private static double Median(double[] values)
    {
        var sorted = (double[])values.Clone();
        Array.Sort(sorted);
        return sorted[sorted.Length / 2];
    }

    // A second read of the same document with every yielded value drained and discarded: the
    // heap is measured against the heap before the pass at each checkpoint, and the largest
    // delta is what the bound is asserted on.
    private static async Task<long> LargestRetainedBytesAsync(byte[] document, HashSet<int> checkpointsAfter)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var baseline = GC.GetTotalMemory(forceFullCollection: true);

        var largest = 0L;
        var index = 0;
        await using var source = new MemoryStream(document, writable: false);
        await foreach (var _ in HeaderItemsReader.ReadAsync(source, TestContext.Current.CancellationToken))
        {
            if (checkpointsAfter.Contains(index))
            {
                largest = Math.Max(largest, GC.GetTotalMemory(forceFullCollection: true) - baseline);
            }

            index++;
        }

        return largest;
    }

    private static string Number(double value) => value.ToString("F2", CultureInfo.InvariantCulture);

    private static string Numbers(double[] values) =>
        string.Join(", ", values.Select(Number));
}
