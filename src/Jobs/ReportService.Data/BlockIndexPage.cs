using System.Text.Json;

namespace ReportService.Data;

/// <summary>One block's entry in the index page: where the block document lives and what it holds.</summary>
/// <param name="BlockIndex">The block's position in the partition, from 0.</param>
/// <param name="Key">The key the block document was written under.</param>
/// <param name="Sha256">The SHA-256 of the block document, lower-case hexadecimal.</param>
/// <param name="SizeBytes">The size of the block document.</param>
/// <param name="FirstRecordOrdinal">The ordinal of the block's first record.</param>
/// <param name="RecordCount">How many records the block holds; 0 is the empty block of an empty snapshot.</param>
/// <param name="RecordBytes">How many bytes of record content the block holds.</param>
/// <param name="ContinuesPreviousGroup">Whether the block's first record continues a group carried in from the previous block.</param>
/// <param name="ContinuesNextGroup">Whether the block's last group continues into the next block.</param>
/// <param name="OpeningGroupKey">The group key the block opens with when it continues a group; null otherwise.</param>
/// <param name="OpeningCarriedForward">The running sum of the amounts of every record in earlier blocks; null when no total is carried.</param>
/// <param name="BlockAmountTotal">The sum of the amounts of the block's own records; null when no total is carried.</param>
/// <param name="OpeningGroupAccumulated">The running sum of the open group's amounts in earlier blocks when the block continues that group; null otherwise.</param>
public sealed record BlockIndexEntry(
    int BlockIndex,
    string Key,
    string Sha256,
    long SizeBytes,
    int FirstRecordOrdinal,
    long RecordCount,
    long RecordBytes,
    bool ContinuesPreviousGroup = false,
    bool ContinuesNextGroup = false,
    string? OpeningGroupKey = null,
    decimal? OpeningCarriedForward = null,
    decimal? BlockAmountTotal = null,
    decimal? OpeningGroupAccumulated = null);

/// <summary>
/// The index page of a partition: the list of its blocks with the interval, the content
/// and the group-continuity and carried-forward each one holds, and the chunk layout the
/// blocks are laid out under. It is the reader's map of a partition — the workflow and
/// the render step resolve a block from here instead of carrying the blocks themselves —
/// so it is written once with the partition and read back exactly. Contiguity is checked
/// on read: block indices run 0..N with no gap and the record ordinals are contiguous
/// across the entries.
/// </summary>
/// <param name="PartitionerVersion">The version of the rules the partition was made with.</param>
/// <param name="Blocks">The blocks, in index order.</param>
/// <param name="ChunkLayout">The chunk layout the blocks are laid out under; page-boundary by default.</param>
public sealed record BlockIndexPage(
    int PartitionerVersion,
    IReadOnlyList<BlockIndexEntry> Blocks,
    string ChunkLayout = "page-boundary")
{
    /// <summary>The page-boundary chunk layout: each block starts on a new page.</summary>
    public const string PageBoundaryLayout = "page-boundary";

    /// <summary>The page as the bytes an object holds.</summary>
    /// <param name="page">The page to write.</param>
    public static byte[] Serialize(BlockIndexPage page)
    {
        ArgumentNullException.ThrowIfNull(page);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("partitionerVersion", page.PartitionerVersion);
            writer.WriteString("chunkLayout", page.ChunkLayout);
            writer.WriteStartArray("blocks");
            foreach (var block in page.Blocks)
            {
                writer.WriteStartObject();
                writer.WriteNumber("blockIndex", block.BlockIndex);
                writer.WriteString("key", block.Key);
                writer.WriteString("sha256", block.Sha256);
                writer.WriteNumber("sizeBytes", block.SizeBytes);
                writer.WriteNumber("firstRecordOrdinal", block.FirstRecordOrdinal);
                writer.WriteNumber("recordCount", block.RecordCount);
                writer.WriteNumber("recordBytes", block.RecordBytes);
                writer.WriteBoolean("continuesPreviousGroup", block.ContinuesPreviousGroup);
                writer.WriteBoolean("continuesNextGroup", block.ContinuesNextGroup);
                WriteNullableString(writer, "openingGroupKey", block.OpeningGroupKey);
                WriteNullableNumber(writer, "openingCarriedForward", block.OpeningCarriedForward);
                WriteNullableNumber(writer, "blockAmountTotal", block.BlockAmountTotal);
                WriteNullableNumber(writer, "openingGroupAccumulated", block.OpeningGroupAccumulated);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return buffer.ToArray();
    }

    /// <summary>Reads a page back, refusing anything that is not one or whose blocks are not contiguous.</summary>
    /// <param name="json">The bytes of the page.</param>
    public static BlockIndexPage Parse(ReadOnlySpan<byte> json)
    {
        using var document = JsonDocument.Parse(json.ToArray());
        var root = document.RootElement;
        var version = Required(root, "partitionerVersion").GetInt32();
        var layout = OptionalString(root, "chunkLayout") ?? PageBoundaryLayout;
        var blocks = new List<BlockIndexEntry>();
        foreach (var block in Required(root, "blocks").EnumerateArray())
        {
            blocks.Add(new BlockIndexEntry(
                Required(block, "blockIndex").GetInt32(),
                Text(Required(block, "key"), "key"),
                Digest(Required(block, "sha256"), "sha256"),
                Required(block, "sizeBytes").GetInt64(),
                Required(block, "firstRecordOrdinal").GetInt32(),
                Required(block, "recordCount").GetInt64(),
                Required(block, "recordBytes").GetInt64(),
                OptionalBoolean(block, "continuesPreviousGroup"),
                OptionalBoolean(block, "continuesNextGroup"),
                OptionalString(block, "openingGroupKey"),
                OptionalDecimal(block, "openingCarriedForward"),
                OptionalDecimal(block, "blockAmountTotal"),
                OptionalDecimal(block, "openingGroupAccumulated")));
        }

        ValidateContiguity(blocks);
        return new BlockIndexPage(version, blocks, layout);
    }

    private static void WriteNullableString(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is null)
        {
            writer.WriteNull(name);
        }
        else
        {
            writer.WriteString(name, value);
        }
    }

    private static void WriteNullableNumber(Utf8JsonWriter writer, string name, decimal? value)
    {
        if (value is { } number)
        {
            writer.WriteNumber(name, number);
        }
        else
        {
            writer.WriteNull(name);
        }
    }

    // The blocks are a contiguous cover of the records: block indices run 0..N with no gap,
    // and each block's first ordinal is the previous block's first ordinal plus its count, so
    // no record is lost, duplicated or reordered by the partition the page describes.
    private static void ValidateContiguity(IReadOnlyList<BlockIndexEntry> blocks)
    {
        var expectedOrdinal = 1;
        for (var index = 0; index < blocks.Count; index++)
        {
            var block = blocks[index];
            if (block.BlockIndex != index)
            {
                throw new JsonException($"the index page's block indices are not contiguous at position {index}");
            }

            if (block.FirstRecordOrdinal != expectedOrdinal)
            {
                throw new JsonException($"the index page's record ordinals are not contiguous at block {index}");
            }

            expectedOrdinal += checked((int)block.RecordCount);
        }
    }

    private static JsonElement Required(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
            ? value
            : throw new JsonException($"the index page has no '{name}'");

    private static bool OptionalBoolean(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.True;

    private static string? OptionalString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static decimal? OptionalDecimal(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number
            ? value.GetDecimal()
            : null;

    private static string Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.String && element.GetString() is { Length: > 0 } text
            ? text
            : throw new JsonException($"the index page's '{name}' is not a value");

    private static string Digest(JsonElement element, string name)
    {
        var text = Text(element, name);
        foreach (var character in text)
        {
            if (character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))
            {
                throw new JsonException($"the index page's '{name}' is not a digest");
            }
        }

        return text.Length == 64 ? text : throw new JsonException($"the index page's '{name}' is not a digest");
    }
}
