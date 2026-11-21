using System.Globalization;
using System.IO.Compression;
using System.Text;
using ReportService.Data;

namespace ReportService.Data.Tests;

/// <summary>
/// The hostile-input matrix of the profile, row by row: every fault the first
/// increment can meet, classified as the delivered reader classifies it — the reason,
/// the ordinal of the value the pass stopped at, the byte of the uncompressed document
/// it points at and the values read before it — with the document built by the case
/// that asserts it, so the expected offset is computed from the case's own bytes and
/// never written as a constant. Each row is read twice, plain and inside a complete
/// gzip stream, and the two readings must answer identically. A row asserts that the
/// pass stopped where it refused: the values it delivered are exactly the ones before
/// the refusal, in order, and none follows it.
/// </summary>
public sealed class DataContractMatrixTests
{
    /// <summary>The header of the records of an ordinary case, with markers of its own.</summary>
    private const string Header =
        """{"title":"PLANTED-VALUE-header","nested":{"PLANTED-NAME-header":1}}""";

    /// <summary>One ordinary record, numbered and carrying markers.</summary>
    private static string Record(int ordinal) =>
        "{\"sequence\":" + ordinal.ToString(CultureInfo.InvariantCulture)
        + ",\"description\":\"PLANTED-VALUE-" + ordinal.ToString(CultureInfo.InvariantCulture)
        + "\",\"nested\":{\"PLANTED-NAME-" + ordinal.ToString(CultureInfo.InvariantCulture) + "\":1}}";

    /// <summary>The number of records the end-of-snapshot rows stream before their fault.</summary>
    private const int BlockRecords = 2_000;

    /// <summary>Every row of the matrix, by the identifier the row is read by.</summary>
    public static TheoryData<string> Rows { get; } =
        new(
            "B1", "B2", "B2b", "B3", "B4", "B5", "B6", "B7", "B8", "B9", "B10", "B11", "B11n",
            "B12", "B12b", "B13", "B13b", "B13c", "B13d", "B14", "B15", "B15b", "B16",
            "B4-2000", "B5-2000", "B7-2000", "B8-2000", "B12-2000");

    [Theory]
    [MemberData(nameof(Rows))]
    public async Task EveryRowIsClassifiedTheWayTheDeliveredReaderClassifiesIt(string row)
    {
        var matrix = Matrix(row);
        var document = matrix.Document();

        for (var compressed = 0; compressed < 2; compressed++)
        {
            var reading = await ReadUntilRefusedAsync(document, compressed == 1);

            var violation = reading.Violation;
            Assert.True(violation is not null, $"{row}: the document has to be refused with reason={matrix.Reason}");

            // The code, the reason and the ordinal of the value the pass stopped at.
            Assert.Equal(DataContractViolation.DataInvalid, violation!.Code);
            Assert.Equal(matrix.Reason, violation.Reason);
            Assert.Equal(matrix.Ordinal, violation.Ordinal);

            // The byte of the uncompressed document the refusal points at, computed from the
            // case's own document, and the values the pass read before it.
            Assert.Equal(matrix.Offset(document), violation.Offset);
            Assert.Equal(matrix.ValuesRead, reading.Values.Count);

            // The pass stopped where it refused: the values delivered are the ones before it,
            // in reading order, and nothing after it was delivered.
            Assert.Equal(
                Enumerable.Range(0, matrix.ValuesRead).Select(ordinal => (long)ordinal).ToArray(),
                reading.Values.Select(value => (long)value.Ordinal).ToArray());

            // No marker of the case's own document reaches the diagnostic.
            AssertNoMarker(violation, "PLANTED-");
        }
    }

    [Fact]
    public async Task AnItemsThatIsNotAnArrayDeliversTheHeaderAndNoRecord()
    {
        // The reader read the header before it reached the member it could not use, and no
        // object was delivered as a record: the whole of what it read is that one value.
        var reading = await ReadUntilRefusedAsync(Matrix("B15b").Document(), compressed: false);

        Assert.NotNull(reading.Violation);
        Assert.Equal("type-mismatch", reading.Violation!.Reason);
        var header = Assert.Single(reading.Values);
        Assert.Equal(0, header.Ordinal);
    }

    [Theory]
    [InlineData("B1", "header-not-first")]
    [InlineData("B2", "header-not-first")]
    [InlineData("B2b", "header-not-first")]
    public async Task ARefusalThatReadsNoRecordDeliversNoValueAtAll(string row, string reason)
    {
        var matrix = Matrix(row);
        var reading = await ReadUntilRefusedAsync(matrix.Document(), compressed: false);

        Assert.NotNull(reading.Violation);
        Assert.Equal(reason, reading.Violation!.Reason);
        Assert.Empty(reading.Values);
    }

    [Fact]
    public async Task AValidSnapshotAtTheTwoThousandRecordBoundIsAdmitted()
    {
        var document = SnapshotBytes.Document(Header, SnapshotBytes.Records(BlockRecords));

        var read = await SnapshotBytes.ReadAllAsync(document, TestContext.Current.CancellationToken);

        Assert.Equal(BlockRecords + 1, read.Count);
        Assert.Equal(BlockRecords, read[^1].Ordinal);
    }

    [Fact]
    public async Task AnEmptyItemsArrayIsAdmittedAndADuplicatedNameIsNot()
    {
        var empty = await SnapshotBytes.ReadAllAsync(SnapshotBytes.Document(), TestContext.Current.CancellationToken);
        var header = Assert.Single(empty);
        Assert.Equal(0, header.Ordinal);

        var duplicated = SnapshotBytes.Document(header: SnapshotBytes.Duplicated("""{"title":"x"}""", "m"));
        var reading = await ReadUntilRefusedAsync(duplicated, compressed: false);
        Assert.Equal("duplicate-property", reading.Violation!.Reason);
        Assert.Empty(reading.Values);
    }

    // One row of the matrix: the document, what the delivered reader answers for it, and
    // where in its own bytes the refusal points.
    private static MatrixRow Matrix(string row) => row switch
    {
        // A header after the items, and items before the header with the version last.
        "B1" => Row(
            () => SnapshotBytes.DocumentOf(
                "\"contractVersion\":\"1.0\"",
                "\"items\":[" + Record(1) + "]",
                "\"header\":" + Header),
            "header-not-first",
            ordinal: 0,
            valuesRead: 0,
            offset: document => OffsetOf(document, "\"items\"")),
        "B2" => Row(
            () => SnapshotBytes.DocumentOf(
                "\"items\":[" + Record(1) + "]",
                "\"header\":" + Header,
                "\"contractVersion\":\"1.0\""),
            "header-not-first",
            ordinal: 0,
            valuesRead: 0,
            offset: document => OffsetOf(document, "\"items\"")),

        // Items with no header before it and none after it: the delivered reason of a
        // document that carries no header at all.
        "B2b" => Row(
            () => SnapshotBytes.DocumentOf(
                "\"contractVersion\":\"1.0\"",
                "\"items\":[" + Record(1) + "]"),
            "header-not-first",
            ordinal: 0,
            valuesRead: 0,
            offset: document => OffsetOf(document, "\"items\"")),

        // A document cut inside a middle record: the header and the first record complete,
        // the cut inside the second. The ordinal is the value the pass stopped at.
        "B3" => Row(
            () => SnapshotBytes.Utf8(
                "{\"contractVersion\":\"1.0\",\"header\":" + Header + ",\"items\":[" + Record(1)
                + ",{\"sequence\":2,\"description\":\"PLANTED-VALUE-2\",\"nested\":{\"PLANTED-NAME-2\""),
            "truncated",
            ordinal: 2,
            valuesRead: 2,
            offset: document => document.Length),

        // A document cut after the last record's brace, before the array's own closing.
        "B4" => Row(
            () => SnapshotBytes.Utf8(Encoding.UTF8.GetString(SnapshotBytes.Document(Header, Record(1)))[..^2]),
            "truncated",
            ordinal: 1,
            valuesRead: 2,
            offset: document => document.Length),

        // A document cut after the array, before the root object's closing.
        "B5" => Row(
            () => SnapshotBytes.Utf8(Encoding.UTF8.GetString(SnapshotBytes.Document(Header, Record(1), Record(2)))[..^1]),
            "truncated",
            ordinal: 2,
            valuesRead: 3,
            offset: document => document.Length),

        // A document cut inside its header.
        "B6" => Row(
            () => SnapshotBytes.Utf8("""{"contractVersion":"1.0","header":{"title":"PLANTED-VALUE-cut"""),
            "truncated",
            ordinal: 0,
            valuesRead: 0,
            offset: document => OffsetOf(document, "\"PLANTED-VALUE-cut")),

        // A byte after the root object that is not whitespace, and a second value after it.
        "B7" => Row(
            () => SnapshotBytes.Utf8(Encoding.UTF8.GetString(SnapshotBytes.Document(Header, Record(1))) + "x"),
            "trailing-content",
            ordinal: 1,
            valuesRead: 2,
            offset: document => OffsetOf(document, "x")),
        "B8" => Row(
            () => SnapshotBytes.Utf8(Encoding.UTF8.GetString(SnapshotBytes.Document(Header, Record(1))) + """{"second":1}"""),
            "trailing-content",
            ordinal: 1,
            valuesRead: 2,
            offset: document => OffsetOf(document, "{\"second\"")),

        // A member repeated at the root: one reason for one fault, at the header's ordinal.
        "B9" => Row(
            () => SnapshotBytes.Utf8("""{"contractVersion":"1.0","header":{"title":"PLANTED-VALUE-x"},"items":[],"items":[]}"""),
            "duplicate-property",
            ordinal: 0,
            valuesRead: 1,
            offset: document => OffsetOf(document, "\"items\"", occurrence: 2)),

        // A member repeated inside the header, inside a middle record and inside a nested
        // object of a record: the ordinal is the value the repetition belongs to.
        "B10" => Row(
            () => SnapshotBytes.Document(
                header: SnapshotBytes.Duplicated("""{"title":"PLANTED-VALUE-y"}""", "PLANTED-NAME-y")),
            "duplicate-property",
            ordinal: 0,
            valuesRead: 0,
            offset: document => OffsetOf(document, "\"PLANTED-NAME-y\"", occurrence: 2)),
        "B11" => Row(
            () => SnapshotBytes.Document(
                records:
                [
                    Record(1),
                    SnapshotBytes.Duplicated("""{"sequence":2,"description":"PLANTED-VALUE-z"}""", "PLANTED-NAME-z"),
                    Record(3),
                ]),
            "duplicate-property",
            ordinal: 2,
            valuesRead: 2,
            offset: document => OffsetOf(document, "\"PLANTED-NAME-z\"", occurrence: 2)),
        "B11n" => Row(
            () => SnapshotBytes.Document(
                records:
                [
                    Record(1),
                    "{\"sequence\":2,\"description\":\"PLANTED-VALUE-n\",\"outer\":{\"middle\":"
                        + SnapshotBytes.Duplicated("""{"x":1}""", "PLANTED-NAME-n") + "}}",
                    Record(3),
                ]),
            "duplicate-property",
            ordinal: 2,
            valuesRead: 2,
            offset: document => OffsetOf(document, "\"PLANTED-NAME-n\"", occurrence: 2)),

        // A member repeated in the last record, and two spellings of one name in one object.
        "B12" => Row(
            () => SnapshotBytes.Document(
                records:
                [
                    Record(1),
                    Record(2),
                    SnapshotBytes.Duplicated("""{"sequence":3,"description":"PLANTED-VALUE-last"}""", "PLANTED-NAME-last"),
                ]),
            "duplicate-property",
            ordinal: 3,
            valuesRead: 3,
            offset: document => OffsetOf(document, "\"PLANTED-NAME-last\"", occurrence: 2)),
        "B12b" => Row(
            () => SnapshotBytes.Document(records: ["""{"name":"PLANTED-VALUE-two","na\u006de":"PLANTED-VALUE-other"}"""]),
            "duplicate-property",
            ordinal: 1,
            valuesRead: 1,
            offset: document => OffsetOf(document, "\"na\\u006de\"")),

        // The profile's own rules: a fourth root member, a header before the version, an
        // items element that is not an object, and the two members a document must carry.
        "B13" => Row(
            () => SnapshotBytes.Utf8("""{"contractVersion":"1.0","header":{"title":"PLANTED-VALUE-f"},"items":[],"extra":1}"""),
            "profile-violation",
            ordinal: 0,
            valuesRead: 1,
            offset: document => OffsetOf(document, "\"extra\"")),
        "B13b" => Row(
            () => SnapshotBytes.Utf8("""{"contractVersion":"1.0","header":{"title":"PLANTED-VALUE-g"},"items":["PLANTED-VALUE-g1"]}"""),
            "profile-violation",
            ordinal: 1,
            valuesRead: 1,
            offset: document => OffsetOf(document, "\"PLANTED-VALUE-g1\"")),
        "B13c" => Row(
            () => SnapshotBytes.Utf8("""{"contractVersion":"1.0","header":{"title":"PLANTED-VALUE-h"}}"""),
            "profile-violation",
            ordinal: 0,
            valuesRead: 1,
            offset: document => document.Length - 1),
        "B13d" => Row(
            () => SnapshotBytes.Utf8("""{"header":{"title":"PLANTED-VALUE-i"},"items":[]}"""),
            "profile-violation",
            ordinal: 0,
            valuesRead: 0,
            offset: document => OffsetOf(document, "\"header\"")),

        // The contract version the profile does not admit.
        "B14" => Row(
            () => SnapshotBytes.Utf8("""{"contractVersion":"2.0","header":{"title":"PLANTED-VALUE-j"},"items":[]}"""),
            "contract-version",
            ordinal: 0,
            valuesRead: 0,
            offset: document => OffsetOf(document, "\"2.0\"")),

        // A header that is not an object, and an items that is not an array: the second
        // read the header before it refused, and delivered no object as a record.
        "B15" => Row(
            () => SnapshotBytes.DocumentOf(
                "\"contractVersion\":\"1.0\"",
                "\"header\":[{\"title\":\"PLANTED-VALUE-k\"}]",
                "\"items\":[]"),
            "type-mismatch",
            ordinal: 0,
            valuesRead: 0,
            offset: document => OffsetAfter(document, "\"header\":")),
        "B15b" => Row(
            () => SnapshotBytes.DocumentOf(
                "\"contractVersion\":\"1.0\"",
                "\"header\":" + Header,
                "\"items\":" + Record(1)),
            "type-mismatch",
            ordinal: 0,
            valuesRead: 1,
            offset: document => OffsetAfter(document, "\"items\":")),

        // A document that is not text of the profile at all.
        "B16" => Row(
            () => InvalidUtf8(),
            "malformed",
            ordinal: 0,
            valuesRead: 0,
            offset: document => OffsetOf(document, "\"a\":") + 4),

        // The end-of-snapshot rows, on the 2 000-record block the first increment admits:
        // the fault is met after a whole block was streamed.
        "B4-2000" => Row(
            () => SnapshotBytes.Utf8(Encoding.UTF8.GetString(SnapshotBytes.Document(Header, SnapshotBytes.Records(BlockRecords)))[..^2]),
            "truncated",
            ordinal: BlockRecords,
            valuesRead: BlockRecords + 1,
            offset: document => document.Length),
        "B5-2000" => Row(
            () => SnapshotBytes.Utf8(Encoding.UTF8.GetString(SnapshotBytes.Document(Header, SnapshotBytes.Records(BlockRecords)))[..^1]),
            "truncated",
            ordinal: BlockRecords,
            valuesRead: BlockRecords + 1,
            offset: document => document.Length),
        "B7-2000" => Row(
            () => SnapshotBytes.Utf8(Encoding.UTF8.GetString(SnapshotBytes.Document(Header, SnapshotBytes.Records(BlockRecords))) + "x"),
            "trailing-content",
            ordinal: BlockRecords,
            valuesRead: BlockRecords + 1,
            offset: document => document.Length - 1),
        "B8-2000" => Row(
            () => SnapshotBytes.Utf8(Encoding.UTF8.GetString(SnapshotBytes.Document(Header, SnapshotBytes.Records(BlockRecords))) + """{"second":1}"""),
            "trailing-content",
            ordinal: BlockRecords,
            valuesRead: BlockRecords + 1,
            offset: document => OffsetOf(document, "{\"second\"")),
        "B12-2000" => Row(
            () =>
            {
                var records = SnapshotBytes.Records(BlockRecords - 1);
                var last = SnapshotBytes.Duplicated(
                    $$"""{"sequence":{{BlockRecords}},"description":"PLANTED-VALUE-last"}""",
                    "PLANTED-NAME-last");
                return SnapshotBytes.Document(Header, [.. records, last]);
            },
            "duplicate-property",
            ordinal: BlockRecords,
            valuesRead: BlockRecords,
            offset: document => OffsetOf(document, "\"PLANTED-NAME-last\"", occurrence: 2)),

        _ => throw new ArgumentOutOfRangeException(nameof(row), row, "the matrix has no such row"),
    };

    private static MatrixRow Row(
        Func<byte[]> document,
        string reason,
        int ordinal,
        int valuesRead,
        Func<byte[], long> offset) => new(document, reason, ordinal, valuesRead, offset);

    // A value that is not valid UTF-8 where a value must start: the document is not text
    // of the profile at all.
    private static byte[] InvalidUtf8()
    {
        var prefix = SnapshotBytes.Utf8("{\"contractVersion\":\"1.0\",\"header\":{\"a\":");
        var suffix = SnapshotBytes.Utf8("},\"items\":[]}");
        var document = new byte[prefix.Length + 1 + suffix.Length];
        prefix.CopyTo(document, 0);
        document[prefix.Length] = 0xFF;
        suffix.CopyTo(document, prefix.Length + 1);
        return document;
    }

    private static async Task<(List<RawRecord> Values, DataContractViolation? Violation)> ReadUntilRefusedAsync(
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

    private static Stream Open(byte[] document, bool compressed)
    {
        var plain = new MemoryStream(compressed ? SnapshotBytes.Gzip(document) : document, writable: false);
        return compressed ? new GZipStream(plain, CompressionMode.Decompress) : plain;
    }

    private static void AssertNoMarker(DataContractViolation violation, string marker)
    {
        Assert.DoesNotContain(marker, violation.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(marker, violation.ToString(), StringComparison.Ordinal);
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
            Assert.True(
                index >= 0,
                string.Create(CultureInfo.InvariantCulture, $"the document does not carry {text} {occurrence} time(s)"));
            index += from;
            from = index + needle.Length;
        }

        return index;
    }

    /// <summary>The byte index just after the first <paramref name="prefix"/> of the document.</summary>
    private static long OffsetAfter(byte[] document, string prefix) =>
        OffsetOf(document, prefix) + Encoding.UTF8.GetByteCount(prefix);

    /// <summary>The byte past the document's last one: where a pass that ran out of bytes stopped.</summary>
    private static long End(byte[] document) => document.Length;

    // One row: the document it asserts, what the delivered reader answers for it, and where
    // in the case's own bytes the refusal points.
    private sealed record MatrixRow(
        Func<byte[]> Document,
        string Reason,
        int Ordinal,
        int ValuesRead,
        Func<byte[], long> Offset);
}
