using System.Text.Json;

namespace StreamingDataSpike.Parsing;

/// <summary>
/// Turns one captured JSON object (contiguous bytes) into the typed model:
/// string → <see cref="string"/>, number → <see cref="decimal"/> (scale kept),
/// true/false → <see cref="bool"/>, null → <see langword="null"/>,
/// object → <see cref="Dictionary{TKey, TValue}"/> with ordinal keys in
/// insertion order, array → <see cref="List{T}"/>.
/// </summary>
public static class JsonValueModel
{
    /// <summary>Reads one complete object.</summary>
    /// <param name="json">The object's bytes, from its opening to its closing brace.</param>
    /// <param name="options">Reader options (depth backstop).</param>
    /// <param name="recordOrdinal">Ordinal reported in errors.</param>
    /// <param name="baseOffset">Absolute offset of <paramref name="json"/> in the input.</param>
    public static Dictionary<string, object?> ReadObject(ReadOnlySpan<byte> json, JsonReaderOptions options, long recordOrdinal, long baseOffset)
    {
        var reader = new Utf8JsonReader(json, options);
        try
        {
            reader.Read();
            return ReadObject(ref reader, recordOrdinal, baseOffset);
        }
        catch (JsonException)
        {
            throw EnvelopeException.Invalid("malformed", recordOrdinal, baseOffset + reader.BytesConsumed);
        }
        catch (InvalidOperationException)
        {
            // Raised when a string cannot be transcoded (invalid UTF-8).
            throw EnvelopeException.Invalid("malformed", recordOrdinal, baseOffset + reader.TokenStartIndex);
        }
    }

    private static Dictionary<string, object?> ReadObject(ref Utf8JsonReader reader, long recordOrdinal, long baseOffset)
    {
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            var nameOffset = reader.TokenStartIndex;
            var name = reader.GetString()!;
            reader.Read();
            var value = ReadValue(ref reader, recordOrdinal, baseOffset);
            if (!values.TryAdd(name, value))
            {
                throw EnvelopeException.Invalid("duplicate-property", recordOrdinal, baseOffset + nameOffset);
            }
        }

        return values;
    }

    private static List<object?> ReadArray(ref Utf8JsonReader reader, long recordOrdinal, long baseOffset)
    {
        var values = new List<object?>();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            values.Add(ReadValue(ref reader, recordOrdinal, baseOffset));
        }

        return values;
    }

    private static object? ReadValue(ref Utf8JsonReader reader, long recordOrdinal, long baseOffset) =>
        reader.TokenType switch
        {
            JsonTokenType.String => reader.GetString(),
            JsonTokenType.Number => reader.TryGetDecimal(out var number)
                ? number
                : throw EnvelopeException.Invalid("number-range", recordOrdinal, baseOffset + reader.TokenStartIndex),
            JsonTokenType.True => true,
            JsonTokenType.False => false,
            JsonTokenType.Null => null,
            JsonTokenType.StartObject => ReadObject(ref reader, recordOrdinal, baseOffset),
            JsonTokenType.StartArray => ReadArray(ref reader, recordOrdinal, baseOffset),
            _ => throw EnvelopeException.Invalid("malformed", recordOrdinal, baseOffset + reader.TokenStartIndex),
        };
}
