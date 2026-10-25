using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace PaginationFixtures;

/// <summary>
/// Reads and writes fixture files: one UTF-8 JSON file per fixture, written
/// byte-for-byte deterministically (fixed property order, LF line ends,
/// non-ASCII letters kept as UTF-8).
/// </summary>
public static class FixtureFile
{
    public const string SumsFileName = "SHA256SUMS";

    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Indented = true,
        NewLine = "\n",
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string PathOf(string directory, string id) => Path.Combine(directory, id + ".json");

    public static byte[] Serialize(Fixture fixture)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteString("id", fixture.Id);
            writer.WriteString("title", fixture.Title);
            writer.WriteString("geometry", fixture.Geometry);
            writer.WriteString("amountAlign", fixture.AmountAlign);
            writer.WriteString("whiteSpace", fixture.WhiteSpace);
            WriteStrings(writer, "freeTextBefore", fixture.FreeTextBefore);
            WriteStrings(writer, "freeTextAfter", fixture.FreeTextAfter);
            writer.WriteStartArray("rows");
            foreach (var row in fixture.Rows)
            {
                writer.WriteStartObject();
                switch (row.Kind)
                {
                    case RowKind.GroupStart:
                        writer.WriteString("kind", "groupStart");
                        writer.WriteString("groupId", row.GroupId);
                        writer.WriteString("groupName", row.GroupName);
                        break;
                    case RowKind.GroupEnd:
                        writer.WriteString("kind", "groupEnd");
                        writer.WriteString("groupId", row.GroupId);
                        writer.WriteString("total", row.Total);
                        break;
                    case RowKind.Item:
                        writer.WriteString("kind", "item");
                        writer.WriteNumber("sequence", row.Sequence!.Value);
                        writer.WriteString("date", row.Date);
                        writer.WriteString("description", row.Description);
                        if (row.Note is not null)
                        {
                            writer.WriteString("note", row.Note);
                        }

                        writer.WriteString("amount", row.Amount);
                        break;
                    default:
                        throw new InvalidOperationException($"Unknown row kind {row.Kind}.");
                }

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteString("blockTotal", fixture.BlockTotal);
            writer.WriteEndObject();
        }

        buffer.WriteByte((byte)'\n');
        return buffer.ToArray();
    }

    public static void Write(string directory, Fixture fixture) => File.WriteAllBytes(PathOf(directory, fixture.Id), Serialize(fixture));

    public static Fixture Read(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        var root = document.RootElement;
        var rows = new List<FixtureRow>();
        foreach (var row in root.GetProperty("rows").EnumerateArray())
        {
            var kind = row.GetProperty("kind").GetString();
            rows.Add(kind switch
            {
                "groupStart" => new FixtureRow { Kind = RowKind.GroupStart, GroupId = Text(row, "groupId"), GroupName = Text(row, "groupName") },
                "groupEnd" => new FixtureRow { Kind = RowKind.GroupEnd, GroupId = Text(row, "groupId"), Total = Text(row, "total") },
                "item" => new FixtureRow
                {
                    Kind = RowKind.Item,
                    Sequence = row.GetProperty("sequence").GetInt32(),
                    Date = Text(row, "date"),
                    Description = Text(row, "description"),
                    Note = row.TryGetProperty("note", out var note) ? note.GetString() : null,
                    Amount = Text(row, "amount"),
                },
                _ => throw new InvalidDataException($"Unknown row kind '{kind}' in {path}."),
            });
        }

        return new Fixture
        {
            Id = Text(root, "id"),
            Title = Text(root, "title"),
            Geometry = Text(root, "geometry"),
            AmountAlign = Text(root, "amountAlign"),
            WhiteSpace = Text(root, "whiteSpace"),
            FreeTextBefore = Strings(root, "freeTextBefore"),
            FreeTextAfter = Strings(root, "freeTextAfter"),
            Rows = rows,
            BlockTotal = Text(root, "blockTotal"),
        };
    }

    /// <summary>Reads a fixture after checking its file against the folder's SHA256SUMS.</summary>
    public static Fixture ReadVerified(string directory, string id, IReadOnlyDictionary<string, string> sums)
    {
        var path = PathOf(directory, id);
        var name = Path.GetFileName(path);
        if (!sums.TryGetValue(name, out var expected))
        {
            throw new InvalidDataException($"{name} is not listed in {SumsFileName}.");
        }

        var actual = Sums.HashFile(path);
        return actual == expected ? Read(path) : throw new InvalidDataException($"SHA-256 of {name} is {actual}, expected {expected}.");
    }

    /// <summary>UTF-8 size of a record serialised as a compact JSON object in the dataset's item shape.</summary>
    public static int CompactItemBytes(DatasetRecord record, string description)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = false, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("sequence", record.Sequence);
            writer.WriteString("groupId", record.GroupId);
            writer.WriteString("groupName", record.GroupName);
            writer.WriteString("date", record.Date);
            writer.WriteString("description", description);
            writer.WriteNumber("amount", record.Amount);
            writer.WriteEndObject();
        }

        return (int)buffer.Length;
    }

    public static int Utf8Bytes(string text) => Encoding.UTF8.GetByteCount(text);

    private static void WriteStrings(Utf8JsonWriter writer, string name, IReadOnlyList<string> values)
    {
        writer.WriteStartArray(name);
        foreach (var value in values)
        {
            writer.WriteStringValue(value);
        }

        writer.WriteEndArray();
    }

    private static string Text(JsonElement element, string name) => element.GetProperty(name).GetString() ?? string.Empty;

    private static List<string> Strings(JsonElement element, string name) =>
        [.. element.GetProperty(name).EnumerateArray().Select(e => e.GetString() ?? string.Empty)];
}
