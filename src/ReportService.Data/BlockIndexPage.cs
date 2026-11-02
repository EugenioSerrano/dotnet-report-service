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
public sealed record BlockIndexEntry(
    int BlockIndex,
    string Key,
    string Sha256,
    long SizeBytes,
    int FirstRecordOrdinal,
    long RecordCount,
    long RecordBytes);

/// <summary>
/// The index page of a partition: the list of its blocks with the interval and the
/// content each one holds. It is the reader's map of a partition — the workflow and
/// the render step resolve a block from here instead of carrying the blocks
/// themselves — so it is written once with the partition and read back exactly.
/// </summary>
/// <param name="PartitionerVersion">The version of the rules the partition was made with.</param>
/// <param name="Blocks">The blocks, in index order.</param>
public sealed record BlockIndexPage(int PartitionerVersion, IReadOnlyList<BlockIndexEntry> Blocks)
{
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
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return buffer.ToArray();
    }

    /// <summary>Reads a page back, refusing anything that is not one.</summary>
    /// <param name="json">The bytes of the page.</param>
    public static BlockIndexPage Parse(ReadOnlySpan<byte> json)
    {
        using var document = JsonDocument.Parse(json.ToArray());
        var root = document.RootElement;
        var version = Required(root, "partitionerVersion").GetInt32();
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
                Required(block, "recordBytes").GetInt64()));
        }

        return new BlockIndexPage(version, blocks);
    }

    private static JsonElement Required(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
            ? value
            : throw new JsonException($"the index page has no '{name}'");

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
