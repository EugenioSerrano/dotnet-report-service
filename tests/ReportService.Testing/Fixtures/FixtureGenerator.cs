using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ReportService.Testing.Fixtures;

/// <summary>
/// The fixture library: the core fixtures of the rendering suite, generated from
/// the pilot dataset as snapshots of the data contract. Every fixture is a
/// function of the dataset and its id, so generating twice gives byte-identical
/// files, and the same id gives the same bytes on every machine.
/// </summary>
public sealed class FixtureGenerator
{
    /// <summary>The data contract version every generated snapshot declares.</summary>
    public const string ContractVersion = "1.0";

    /// <summary>The record whose description a tall-row fixture replaces.</summary>
    public const int TallRowSequence = 50;

    /// <summary>The measured length of the tall-row anchor of the native experiment.</summary>
    public const int TallRowLength = 15_930;

    /// <summary>The row bound: a record of this size is admissible, one byte more is not.</summary>
    public const int RowBoundBytes = 262_144;

    /// <summary>The measured length of the free-text anchor of the native experiment.</summary>
    public const int FreeTextLength = 26_312;

    /// <summary>The first record of the group-edge fixture, as its sequence number.</summary>
    public const int GroupEdgeFirstSequence = 401;

    /// <summary>The number of lines the group-edge fixture's first row prints on.</summary>
    public const int GroupEdgeLineCount = 30;

    private const int GroupEdgeStart = 400;
    private const int GroupEdgeCount = 250;
    private const int RecordSizeTolerance = 64;

    private static readonly JsonSerializerOptions Writing = new()
    {
        // The snapshot is read by a JSON reader, never embedded in a page, so
        // accented letters stay the characters they are: a record's measured
        // size is then the size of its text in UTF-8.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly UTF8Encoding Utf8WithoutBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly FixtureDataset dataset;

    /// <summary>Prepares the generator over a loaded dataset.</summary>
    public FixtureGenerator(FixtureDataset dataset)
    {
        ArgumentNullException.ThrowIfNull(dataset);
        this.dataset = dataset;
    }

    /// <summary>The name of the sums file the generator writes beside the fixtures.</summary>
    public const string SumsFileName = "SHA256SUMS";

    /// <summary>Every fixture id of the library, in ordinal order.</summary>
    public static IReadOnlyList<string> Ids { get; } =
    [
        "A1-esc",
        "B100",
        "B1000",
        "F26K-a",
        "GE30",
        "K1",
        "K0",
        "T300u",
        "X256K",
        "X256K-one-over",
        "X64K",
    ];

    /// <summary>The size, in UTF-8 bytes, of a record as a compact JSON item.</summary>
    public static int CompactItemBytes(JsonObject item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return Encoding.UTF8.GetByteCount(item.ToJsonString(Writing));
    }

    /// <summary>Builds the fixture of <paramref name="id"/>.</summary>
    public SnapshotFixture Build(string id)
    {
        var (header, items) = id switch
        {
            "A1-esc" => (Header(string.Empty), Records(0, 3, item => Sequence(item) == 1 ? EscapedText() : null)),
            "B100" => (Header(string.Empty), Records(0, 100, _ => null)),
            "B1000" => (Header(string.Empty), Records(0, 1_000, _ => null)),
            "F26K-a" => (Header(UniqueText.OfLength(id, "freeText", FreeTextLength)), Records(0, 20, _ => null)),
            "GE30" => (Header(string.Empty), Records(
                GroupEdgeStart,
                GroupEdgeCount,
                item => Sequence(item) == GroupEdgeFirstSequence ? GroupEdgeLines() : null)),
            "K1" => (Header(string.Empty), Records(CalibrationIndex(), 1, _ => null)),
            "K0" => (EmptyHeader(), new List<JsonObject>()),
            "T300u" => (Header(string.Empty), Records(
                0,
                100,
                item => Sequence(item) == TallRowSequence ? UniqueText.OfLength(id, "description", TallRowLength) : null)),
            "X256K" => (Header(string.Empty), Sized(id, RowBoundBytes)),
            "X256K-one-over" => (Header(string.Empty), SizedExactly(id, RowBoundBytes + 1)),
            "X64K" => (Header(string.Empty), Sized(id, 65_536)),
            _ => throw new ArgumentException($"Unknown fixture id '{id}'.", nameof(id)),
        };

        return Snapshot(id, header, items);
    }

    /// <summary>Builds every fixture of the library.</summary>
    public IReadOnlyList<SnapshotFixture> BuildAll() => [.. Ids.Select(Build)];

    /// <summary>Writes every fixture and the sums file into <paramref name="directory"/>.</summary>
    public void WriteFolder(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        Directory.CreateDirectory(directory);
        var sums = new StringBuilder();
        foreach (var fixture in BuildAll())
        {
            fixture.WriteTo(directory);
            sums.Append(fixture.Sha256).Append("  ").Append(fixture.FileName).Append('\n');
        }

        File.WriteAllText(Path.Combine(directory, SumsFileName), sums.ToString(), Utf8WithoutBom);
    }

    /// <summary>The record number of a snapshot record.</summary>
    public static int Sequence(JsonObject item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return item["sequence"]?.GetValue<int>()
            ?? throw new InvalidDataException("A record holds no sequence.");
    }

    private static SnapshotFixture Snapshot(string id, JsonObject header, List<JsonObject> items)
    {
        var document = new JsonObject
        {
            ["contractVersion"] = ContractVersion,
            ["header"] = header,
            ["items"] = new JsonArray(items.Select(item => (JsonNode?)item).ToArray()),
        };

        var bytes = Utf8WithoutBom.GetBytes(document.ToJsonString(Writing));
        return new SnapshotFixture(id, bytes, Convert.ToHexStringLower(SHA256.HashData(bytes)), items.Count);
    }

    private JsonObject Header(string freeText)
    {
        var header = (JsonObject)dataset.Header.DeepClone();
        header["freeText"] = freeText;
        return header;
    }

    private JsonObject EmptyHeader()
    {
        var header = Header(string.Empty);
        var totals = header["totals"] as JsonObject
            ?? throw new InvalidDataException("The dataset's header holds no totals object.");
        totals["recordCount"] = 0;
        return header;
    }

    private List<JsonObject> Records(int start, int count, Func<JsonObject, string?> description)
    {
        var items = new List<JsonObject>(count);
        for (var offset = 0; offset < count; offset++)
        {
            var item = (JsonObject)dataset.Items[start + offset].DeepClone();
            if (description(item) is { } replacement)
            {
                item["description"] = replacement;
            }

            items.Add(item);
        }

        return items;
    }

    private List<JsonObject> Sized(string id, int maximum) => Records(
        0,
        100,
        item => Sequence(item) != TallRowSequence
            ? null
            : UniqueText.OfSize(
                id,
                "description",
                maximum - RecordSizeTolerance,
                maximum,
                text =>
                {
                    item["description"] = text;
                    return CompactItemBytes(item);
                }));

    private List<JsonObject> SizedExactly(string id, int size) => Records(
        0,
        100,
        item => Sequence(item) != TallRowSequence
            ? null
            : UniqueText.OfExactSize(
                id,
                "description",
                size,
                text =>
                {
                    item["description"] = text;
                    return CompactItemBytes(item);
                }));

    private int CalibrationIndex()
    {
        var index = 0;
        var longest = Description(dataset.Items[index]).Length;
        for (var candidate = 1; candidate < 1_000; candidate++)
        {
            var length = Description(dataset.Items[candidate]).Length;
            if (length > longest)
            {
                index = candidate;
                longest = length;
            }
        }

        return index;
    }

    private static string Description(JsonObject item) => item["description"]?.GetValue<string>() ?? string.Empty;

    private static string GroupEdgeLines() =>
        string.Join('\n', Enumerable.Range(1, GroupEdgeLineCount).Select(line => "edgeline" + line.ToString("D2", CultureInfo.InvariantCulture)));

    private static string EscapedText()
    {
        var builder = new StringBuilder("a<b & c\"d ");
        for (var value = 0x00A0; value <= 0x00FF; value++)
        {
            // The soft hyphen is left out on purpose: a character Chromium does
            // not paint, so a fixture that carried it could never be verified.
            if (value != 0x00AD)
            {
                builder.Append((char)value);
            }
        }

        return builder.ToString();
    }
}
