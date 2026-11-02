using System.Globalization;
using System.Text.Json;
using ReportService.Data;

namespace ReportService.Templates;

/// <summary>
/// Builds the render context from raw JSON: the header, one record, the group of
/// the current record, the block, the (empty) accumulators and the fixed
/// render values of the job.
/// <para>
/// A number is given as the invariant text of its exact decimal value; a number
/// with no exact decimal is given as its raw token text, never rounded. Null and
/// missing values print as empty.
/// </para>
/// </summary>
public static class RenderContextFactory
{
    /// <summary>The maximum depth a record or header may reach; the reader refuses deeper documents first.</summary>
    public const int MaximumDepth = 64;

    /// <summary>Reads a header document into plain values.</summary>
    public static IReadOnlyDictionary<string, object?> Header(ReadOnlySpan<byte> json) => Object(Parse(json), "the header");

    /// <summary>Reads one record into plain values.</summary>
    public static IReadOnlyDictionary<string, object?> Item(RawRecord record) => Object(Parse(record.Json.Span), $"record {record.Ordinal}");

    /// <summary>Builds the block member.</summary>
    /// <param name="index">The index of the block within the job.</param>
    /// <param name="recordCount">The number of records the block holds.</param>
    /// <param name="firstRecordOrdinal">The ordinal of the block's first record.</param>
    /// <param name="isFirst">Whether this is the first block of the job.</param>
    /// <param name="isLast">Whether this is the last block of the job.</param>
    /// <param name="recordOrdinal">The ordinal of the current record in a row part.</param>
    public static IReadOnlyDictionary<string, object?> Block(
        int index,
        long recordCount,
        long firstRecordOrdinal,
        bool isFirst,
        bool isLast,
        long? recordOrdinal = null)
    {
        var block = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["index"] = index.ToString(CultureInfo.InvariantCulture),
            ["recordCount"] = recordCount.ToString(CultureInfo.InvariantCulture),
            ["firstRecordOrdinal"] = firstRecordOrdinal.ToString(CultureInfo.InvariantCulture),
            ["isFirst"] = isFirst,
            ["isLast"] = isLast,
        };
        if (recordOrdinal is { } ordinal)
        {
            block["recordOrdinal"] = ordinal.ToString(CultureInfo.InvariantCulture);
        }

        return block;
    }

    /// <summary>Builds the group member of the current record.</summary>
    /// <param name="key">The group key as it arrived.</param>
    /// <param name="first">Whether the record is the first of its group within the block.</param>
    /// <param name="continuesFromPreviousBlock">Whether the group began in an earlier block.</param>
    /// <param name="continuesInNextBlock">Whether the group continues in a later block.</param>
    public static IReadOnlyDictionary<string, object?> Group(
        object? key,
        bool first,
        bool continuesFromPreviousBlock,
        bool continuesInNextBlock) =>
        new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["key"] = key,
            ["first"] = first,
            ["continuesFromPreviousBlock"] = continuesFromPreviousBlock,
            ["continuesInNextBlock"] = continuesInNextBlock,
        };

    /// <summary>Builds the render member: the culture, the time zone and the job's fixed clock.</summary>
    public static IReadOnlyDictionary<string, object?> Render(string? culture, string? timeZone, DateTimeOffset clock) =>
        new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["culture"] = culture,
            ["timeZone"] = timeZone,
            ["clock"] = clock.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
        };

    /// <summary>An empty accumulator set: this increment declares none.</summary>
    public static IReadOnlyDictionary<string, object?> NoAggregates { get; } = new Dictionary<string, object?>(StringComparer.Ordinal);

    /// <summary>
    /// Reads the value at a member path (for example <c>customer.name</c>) from a
    /// context object; returns <see langword="false"/> when the path is absent or
    /// reaches anything other than an object.
    /// </summary>
    public static bool TryResolve(IReadOnlyDictionary<string, object?>? source, string path, out object? value)
    {
        value = null;
        var current = source;
        var segments = path.Split('.', StringSplitOptions.None);
        for (var i = 0; i < segments.Length; i++)
        {
            if (current is null || !current.TryGetValue(segments[i], out var next))
            {
                return false;
            }

            if (i == segments.Length - 1)
            {
                value = next;
                return true;
            }

            current = next as IReadOnlyDictionary<string, object?>;
        }

        return false;
    }

    private static JsonDocument Parse(ReadOnlySpan<byte> json)
    {
        var document = JsonDocument.Parse(json.ToArray(), new JsonDocumentOptions { MaxDepth = MaximumDepth });
        return document;
    }

    private static IReadOnlyDictionary<string, object?> Object(JsonDocument document, string what)
    {
        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new TemplateInputException($"{what} is not an object.");
            }

            return (IReadOnlyDictionary<string, object?>)Value(document.RootElement)!;
        }
    }

    private static object? Value(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => Members(element),
        JsonValueKind.Array => Items(element),
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number => Number(element.GetRawText()),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null,
    };

    private static Dictionary<string, object?> Members(JsonElement element)
    {
        var members = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            members[property.Name] = Value(property.Value);
        }

        return members;
    }

    private static List<object?> Items(JsonElement element)
    {
        var items = new List<object?>(element.GetArrayLength());
        foreach (var item in element.EnumerateArray())
        {
            items.Add(Value(item));
        }

        return items;
    }

    /// <summary>
    /// The invariant text of the number's exact decimal value, or its raw token
    /// text when it has none. The text is never parsed into a floating-point value.
    /// </summary>
    private static string Number(string rawToken) =>
        decimal.TryParse(rawToken, NumberStyles.Float, CultureInfo.InvariantCulture, out var exact)
            ? exact.ToString(CultureInfo.InvariantCulture)
            : rawToken;
}

/// <summary>A header or a record that cannot be read into the render context.</summary>
public sealed class TemplateInputException(string message) : Exception(message);
