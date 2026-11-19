using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace ReportService.Testing.Limits;

/// <summary>
/// The snapshots that sit at and beside the single block's two bounds, generated from the
/// pilot template's own sample so every document is one the data profile admits: the record
/// counts at and around the bound, and the byte families whose records are a fixed number of
/// bytes each. Each family is generated twice by its test and must be identical, and every
/// family's SHA-256 is recorded beside this type, so a document that drifts from the one the
/// measurement was taken on is refused before it is used.
/// <para>
/// A byte family is built by padding one record's description until the record's own JSON is
/// exactly the number of bytes the family names: the partitioner counts the bytes of the
/// record as the snapshot holds them, so the family must be measured the same way.
/// </para>
/// </summary>
public static class LimitSnapshots
{
    /// <summary>The file that records the SHA-256 of every family.</summary>
    public const string SumsFileName = "SHA256SUMS";

    /// <summary>The template whose sample every family is built from.</summary>
    public const string Template = "pilot-simple";

    /// <summary>The record of a byte family: the bytes one record of it occupies.</summary>
    public const int BoundRecordBytes = 16_384;

    /// <summary>The record one byte over the family's own size, which takes the snapshot past the bound.</summary>
    public const int OverBoundRecordBytes = 16_385;

    /// <summary>The record of the family that crosses the byte bound at record 573.</summary>
    public const int CrossingRecordBytes = 1_833;

    private const int BoundRecordCount = 1_000;

    private const int OverBoundRecordCount = 1_001;

    private const int MaximumRecordCount = 2_000;

    private const int OverMaximumRecordCount = 2_001;

    private const int BeyondBothRecordCount = 50_000;

    private const int CrossingRecordCount = 600;

    /// <summary>
    /// The record-count family: the pilot sample's own records repeated to
    /// <paramref name="records"/>, each with its position, and the header's count set to it.
    /// </summary>
    /// <param name="records">How many records the snapshot holds.</param>
    public static byte[] Records(int records) => Snapshot(records);

    /// <summary>The record-count family at the configured bound: 1 000 records.</summary>
    public static byte[] RecordsAtBound() => Records(BoundRecordCount);

    /// <summary>The record-count family one record over the configured bound: 1 001 records.</summary>
    public static byte[] RecordsOverTheBound() => Records(OverBoundRecordCount);

    /// <summary>The record-count family at the block's hard maximum: 2 000 records.</summary>
    public static byte[] RecordsAtTheMaximum() => Records(MaximumRecordCount);

    /// <summary>The record-count family one record over the block's hard maximum: 2 001 records.</summary>
    public static byte[] RecordsOverTheMaximum() => Records(OverMaximumRecordCount);

    /// <summary>The record-count family far beyond either bound: 50 000 records.</summary>
    public static byte[] RecordsBeyondBoth() => Records(BeyondBothRecordCount);

    /// <summary>
    /// A snapshot of 1 001 records whose first record is over the per-record byte limit: the
    /// reader refuses that record before the block admits anything, so this document ends with
    /// the one-record code and not with the single block's limit.
    /// </summary>
    public static byte[] RecordsWithOneOverTheRecordLimit()
    {
        var sample = JsonNode.Parse(File.ReadAllText(Path.Combine(
            RepositoryPaths.Root, "samples", "templates", Template, "sample.json")))!;
        var source = sample["items"]!.AsArray();
        var built = new JsonArray { FixedRecord(1, 1_048_577) };
        for (var index = 0; index < BoundRecordCount; index++)
        {
            var item = JsonNode.Parse(source[index % source.Count]!.ToJsonString())!;
            item["sequence"] = index + 2;
            built.Add(item);
        }

        sample["items"] = built;
        sample["header"]!["totals"]!["recordCount"] = BoundRecordCount + 1;
        return Encoding.UTF8.GetBytes(sample.ToJsonString());
    }

    /// <summary>The byte family of exactly 1 048 576 bytes: 64 records of 16 384 bytes each.</summary>
    public static byte[] BytesAtTheBound() => FixedRecords([.. Enumerable.Repeat(BoundRecordBytes, 64)]);

    /// <summary>
    /// The byte family of exactly 1 048 577 bytes: 63 records of 16 384 bytes and one of
    /// 16 385, so the snapshot is one byte over the bound at its sixty-fourth record.
    /// </summary>
    public static byte[] BytesOneOverTheBound() =>
        FixedRecords([.. Enumerable.Repeat(BoundRecordBytes, 63), OverBoundRecordBytes]);

    /// <summary>
    /// The family of 600 records of 1 833 bytes each: the snapshot crosses the byte bound at
    /// its 573rd record (572 records are 1 048 476 bytes, and the next one does not fit).
    /// </summary>
    public static byte[] BytesCrossingAtRecord573() =>
        FixedRecords([.. Enumerable.Repeat(CrossingRecordBytes, CrossingRecordCount)]);

    /// <summary>Every family this type generates, by the name its sum is recorded under.</summary>
    public static IReadOnlyDictionary<string, byte[]> Families() => new Dictionary<string, byte[]>(StringComparer.Ordinal)
    {
        ["records-1000"] = RecordsAtBound(),
        ["records-1001"] = RecordsOverTheBound(),
        ["records-2000"] = RecordsAtTheMaximum(),
        ["records-2001"] = RecordsOverTheMaximum(),
        ["records-50000"] = RecordsBeyondBoth(),
        ["bytes-1048576"] = BytesAtTheBound(),
        ["bytes-1048577"] = BytesOneOverTheBound(),
        ["bytes-600x1833"] = BytesCrossingAtRecord573(),
    };

    /// <summary>The SHA-256 of one document, in the lower-case hexadecimal form the sums file records.</summary>
    /// <param name="document">The document's bytes.</param>
    public static string Sha256(byte[] document) => Convert.ToHexStringLower(SHA256.HashData(document));

    /// <summary>The bytes of each record of <paramref name="document"/>, as the pass counts them.</summary>
    /// <param name="document">A snapshot of the record-count or byte family.</param>
    public static IReadOnlyList<long> RecordBytes(byte[] document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var root = JsonNode.Parse(document)!.AsObject();
        return [.. root["items"]!.AsArray().Select(record => (long)Encoding.UTF8.GetByteCount(record!.ToJsonString()))];
    }

    /// <summary>Reads a sums file into the hash of each family name.</summary>
    /// <param name="path">The sums file's path.</param>
    public static IReadOnlyDictionary<string, string> ReadSums(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var sums = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in File.ReadAllLines(path))
        {
            if (line.Length == 0)
            {
                continue;
            }

            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2)
            {
                sums[parts[1]] = parts[0];
            }
        }

        return sums;
    }

    // The pilot sample's document with its records repeated to the case's count: the same
    // bytes a job of that size carries, so a case that prints one prints this document.
    private static byte[] Snapshot(int records)
    {
        var sample = JsonNode.Parse(File.ReadAllText(Path.Combine(
            RepositoryPaths.Root, "samples", "templates", Template, "sample.json")))!;
        var items = sample["items"]!.AsArray();
        var built = new JsonArray();
        for (var index = 0; index < records; index++)
        {
            var item = JsonNode.Parse(items[index % items.Count]!.ToJsonString())!;
            item["sequence"] = index + 1;
            built.Add(item);
        }

        sample["items"] = built;
        sample["header"]!["totals"]!["recordCount"] = records;
        return Encoding.UTF8.GetBytes(sample.ToJsonString());
    }

    // One document whose records occupy exactly the bytes the family names: each record is
    // a complete record of the profile, and its description carries as many bytes as the
    // difference between the record's own size and what the family asks for.
    private static byte[] FixedRecords(IReadOnlyList<int> recordBytes)
    {
        var sample = JsonNode.Parse(File.ReadAllText(Path.Combine(
            RepositoryPaths.Root, "samples", "templates", Template, "sample.json")))!;
        var built = new JsonArray();
        for (var index = 0; index < recordBytes.Count; index++)
        {
            built.Add(FixedRecord(index + 1, recordBytes[index]));
        }

        sample["items"] = built;
        sample["header"]!["totals"]!["recordCount"] = recordBytes.Count;
        return Encoding.UTF8.GetBytes(sample.ToJsonString());
    }

    private static JsonObject FixedRecord(int sequence, int wireBytes)
    {
        var record = new JsonObject
        {
            ["sequence"] = sequence,
            ["description"] = string.Empty,
            ["quantity"] = 1,
            ["unitPrice"] = 1.00,
            ["amount"] = 1.00,
        };
        var empty = record.ToJsonString();
        var padding = wireBytes - Encoding.UTF8.GetByteCount(empty);
        if (padding < 0)
        {
            throw new InvalidOperationException(string.Create(
                CultureInfo.InvariantCulture,
                $"a record of this profile cannot be {wireBytes} bytes long: its shortest form is {Encoding.UTF8.GetByteCount(empty)}"));
        }

        record["description"] = new string('x', padding);
        var built = record.ToJsonString();
        var actual = Encoding.UTF8.GetByteCount(built);
        if (actual != wireBytes)
        {
            throw new InvalidOperationException(string.Create(
                CultureInfo.InvariantCulture,
                $"a record of {wireBytes} bytes was built as {actual} bytes"));
        }

        return record;
    }
}
