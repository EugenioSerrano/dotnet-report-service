using System.Text.Json;
using System.Text.Json.Nodes;

namespace ReportService.PdfOracle;

/// <summary>One detail record of the fixture, as the printed row must hold it.</summary>
/// <param name="Ordinal">The record's ordinal in the snapshot, from 1.</param>
/// <param name="Sequence">The record's own number, printed in the first column.</param>
/// <param name="Date">The date cell's text.</param>
/// <param name="Description">The description cell's text.</param>
/// <param name="Amount">The amount cell's text.</param>
internal sealed record FixtureRecord(int Ordinal, int Sequence, string Date, string Description, string Amount);

/// <summary>
/// The data a document was printed from, read the way the data contract holds it:
/// one header object and the detail records in document order. The judge reads the
/// file itself instead of the code that parsed it, so an expectation never arrives
/// through the code under test.
/// </summary>
internal sealed class FixtureSource
{
    private readonly JsonObject root;

    private FixtureSource(JsonObject root, JsonObject header, IReadOnlyList<FixtureRecord> records)
    {
        this.root = root;
        Header = header;
        Records = records;
    }

    /// <summary>The header as the snapshot holds it.</summary>
    public JsonObject Header { get; }

    /// <summary>The detail records, in document order.</summary>
    public IReadOnlyList<FixtureRecord> Records { get; }

    /// <summary>The contract version every snapshot declares.</summary>
    public string ContractVersion => Text(root, "contractVersion");

    /// <summary>Reads the snapshot written at <paramref name="path"/>.</summary>
    public static FixtureSource Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        return Parse(File.ReadAllText(path));
    }

    /// <summary>Reads a snapshot from its text.</summary>
    public static FixtureSource Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var root = JsonNode.Parse(text) as JsonObject
            ?? throw new InvalidDataException("The snapshot is not a JSON object.");
        if (!string.Equals(Text(root, "contractVersion"), "1.0", StringComparison.Ordinal))
        {
            throw new InvalidDataException("The snapshot declares another data contract version.");
        }

        var header = root["header"] as JsonObject
            ?? throw new InvalidDataException("The snapshot holds no header.");
        var items = root["items"] as JsonArray
            ?? throw new InvalidDataException("The snapshot holds no records.");

        var records = new List<FixtureRecord>(items.Count);
        for (var index = 0; index < items.Count; index++)
        {
            var item = items[index] as JsonObject
                ?? throw new InvalidDataException("A record is not a JSON object.");
            records.Add(new FixtureRecord(
                index + 1,
                (int)Number(item, "sequence"),
                Text(item, "date"),
                Text(item, "description"),
                Text(item, "amount")));
        }

        return new FixtureSource(root, header, records);
    }

    /// <summary>
    /// The header value a member path names, as the printed text holds it: a string
    /// as it is, a number as the text of its exact decimal, a boolean as its word,
    /// and nothing for a value the page cannot print. Paths are written from the
    /// header, as the packages declare them: <c>$.a.b</c>.
    /// </summary>
    public string ValueOf(string memberPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(memberPath);

        if (!memberPath.StartsWith("$.", StringComparison.Ordinal))
        {
            throw new InvalidDataException($"'{memberPath}' is not a member path of the header.");
        }

        JsonNode? node = Header;
        foreach (var member in memberPath[2..].Split('.'))
        {
            node = node?[member];
        }

        return node is JsonValue value ? Printable(value) : string.Empty;
    }

    private static string Printable(JsonValue value)
    {
        var element = value.GetValue<JsonElement>();
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString() ?? string.Empty,
            JsonValueKind.Number => element.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => string.Empty,
        };
    }

    private static string Text(JsonObject node, string name) =>
        node[name] is JsonValue value
            ? Printable(value)
            : throw new InvalidDataException($"The snapshot holds no '{name}'.");

    private static decimal Number(JsonObject node, string name) =>
        node[name] is JsonValue value && value.TryGetValue<decimal>(out var number)
            ? number
            : throw new InvalidDataException($"The snapshot holds no '{name}'.");
}
