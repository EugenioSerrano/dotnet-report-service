using System.Text.Json;
using System.Text.Json.Nodes;

namespace ReportService.PdfOracle;

/// <summary>One detail record of the fixture, as the declaration's columns read it.</summary>
/// <param name="Ordinal">The record's ordinal in the snapshot, from 1.</param>
/// <param name="Key">The value of the column the declaration names as the row key.</param>
/// <param name="Values">Every declared column's value for this record, as the printed text holds it.</param>
internal sealed record FixtureRecord(int Ordinal, string Key, IReadOnlyDictionary<string, string> Values)
{
    /// <summary>The record's value of one declared column.</summary>
    /// <param name="column">The column's name, which is the record member it prints.</param>
    public string Member(string column) => Values[column];
}

/// <summary>
/// The data a document was printed from, read the way the data contract holds it:
/// one header object and the detail records in document order. A record is read
/// through the columns the declaration names — each column's name is the record
/// member it prints — so the judge reads a package's own records instead of a fixed
/// four. The judge reads the file itself instead of the code that parsed it, so an
/// expectation never arrives through the code under test.
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

    /// <summary>Reads the snapshot written at <paramref name="path"/> through <paramref name="expectation"/>'s columns.</summary>
    /// <param name="path">The file holding the snapshot.</param>
    /// <param name="expectation">The declaration whose columns name the record members.</param>
    public static FixtureSource Load(string path, ExpectationDocument expectation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(expectation);

        return Parse(File.ReadAllText(path), expectation);
    }

    /// <summary>Reads a snapshot from its text through <paramref name="expectation"/>'s columns.</summary>
    /// <param name="text">The snapshot.</param>
    /// <param name="expectation">The declaration whose columns name the record members.</param>
    public static FixtureSource Parse(string text, ExpectationDocument expectation)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(expectation);

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
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var column in expectation.Columns)
            {
                values[column.Column] = item[column.Column] is JsonValue value
                    ? Printable(value)
                    : throw new InvalidDataException(
                        $"A record holds no '{column.Column}', the member its declaration's column prints.");
            }

            records.Add(new FixtureRecord(index + 1, values[expectation.RowKey], values));
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
}
