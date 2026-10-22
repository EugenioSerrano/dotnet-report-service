using System.Text.Json;

namespace MarkdownSpike.Input;

/// <summary>
/// Streams a header-items-v1 file: the header once, then one record at a time.
/// Only the read buffer and the current record are held in memory.
/// </summary>
internal sealed class EnvelopeReader : IDisposable
{
    private const int InitialBufferSize = 64 * 1024;
    private const int MaxBufferSize = 4 * 1024 * 1024;

    private readonly FileStream stream;
    private byte[] buffer = new byte[InitialBufferSize];
    private int start;
    private int end;
    private bool isFinalBlock;
    private JsonReaderState state = new(new JsonReaderOptions { CommentHandling = JsonCommentHandling.Disallow });
    private bool itemsStarted;

    public EnvelopeReader(string path)
    {
        stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.SequentialScan);
        Header = ReadHeaderAndOpenItems();
        RecordCount = Header.GetProperty("totals").GetProperty("recordCount").GetInt64();
        GroupCount = Header.GetProperty("totals").TryGetProperty("groupCount", out JsonElement groups) ? groups.GetInt64() : 0;
        DatasetId = Header.GetProperty("datasetId").GetString() ?? string.Empty;
    }

    private delegate bool Step<T>(ref Utf8JsonReader reader, out T value);

    public JsonElement Header { get; }

    public long RecordCount { get; }

    public long GroupCount { get; }

    public string DatasetId { get; }

    public int BufferGrowths { get; private set; }

    /// <summary>Yields every record once, in file order, as an independent element.</summary>
    public IEnumerable<JsonElement> ReadItems()
    {
        if (!itemsStarted)
        {
            throw new InvalidOperationException("The items array was not reached.");
        }

        while (true)
        {
            (bool hasItem, JsonElement item) = Run(static (ref Utf8JsonReader reader, out (bool, JsonElement) value) =>
            {
                value = default;
                if (!reader.Read())
                {
                    return false;
                }

                if (reader.TokenType == JsonTokenType.EndArray)
                {
                    value = (false, default);
                    return true;
                }

                if (reader.TokenType != JsonTokenType.StartObject)
                {
                    throw new InvalidDataException("Every item must be a JSON object.");
                }

                Utf8JsonReader probe = reader;
                if (!probe.TrySkip())
                {
                    return false;
                }

                value = (true, JsonElement.ParseValue(ref reader));
                return true;
            });

            if (!hasItem)
            {
                yield break;
            }

            yield return item;
        }
    }

    public void Dispose() => stream.Dispose();

    private JsonElement ReadHeaderAndOpenItems()
    {
        Run(static (ref Utf8JsonReader reader, out bool value) =>
        {
            value = true;
            if (!reader.Read())
            {
                return false;
            }

            return reader.TokenType == JsonTokenType.StartObject
                ? true
                : throw new InvalidDataException("The file must hold one JSON object.");
        });

        JsonElement? header = null;
        while (true)
        {
            string name = Run(static (ref Utf8JsonReader reader, out string value) =>
            {
                value = string.Empty;
                if (!reader.Read())
                {
                    return false;
                }

                if (reader.TokenType != JsonTokenType.PropertyName)
                {
                    throw new InvalidDataException("The envelope ended before its items.");
                }

                value = reader.GetString() ?? string.Empty;
                return true;
            });

            if (name == "items")
            {
                Run(static (ref Utf8JsonReader reader, out bool value) =>
                {
                    value = true;
                    if (!reader.Read())
                    {
                        return false;
                    }

                    return reader.TokenType == JsonTokenType.StartArray
                        ? true
                        : throw new InvalidDataException("items must be an array.");
                });
                itemsStarted = true;
                return header ?? throw new InvalidDataException("The header must come before the items.");
            }

            if (name == "header")
            {
                header = Run(static (ref Utf8JsonReader reader, out JsonElement value) =>
                {
                    value = default;
                    if (!reader.Read())
                    {
                        return false;
                    }

                    Utf8JsonReader probe = reader;
                    if (!probe.TrySkip())
                    {
                        return false;
                    }

                    value = JsonElement.ParseValue(ref reader);
                    return true;
                });
                continue;
            }

            Run(static (ref Utf8JsonReader reader, out bool value) =>
            {
                value = true;
                if (!reader.Read())
                {
                    return false;
                }

                return reader.TokenType is not (JsonTokenType.StartObject or JsonTokenType.StartArray) || reader.TrySkip();
            });
        }
    }

    /// <summary>Runs one step on the unconsumed bytes, refilling and retrying from the saved state when the data ends too early.</summary>
    private T Run<T>(Step<T> step)
    {
        while (true)
        {
            var reader = new Utf8JsonReader(buffer.AsSpan(start, end - start), isFinalBlock, state);
            if (step(ref reader, out T value))
            {
                start += (int)reader.BytesConsumed;
                state = reader.CurrentState;
                return value;
            }

            if (isFinalBlock)
            {
                throw new InvalidDataException("The dataset file ended unexpectedly.");
            }

            Refill();
        }
    }

    private void Refill()
    {
        int remaining = end - start;
        if (start > 0)
        {
            Buffer.BlockCopy(buffer, start, buffer, 0, remaining);
            start = 0;
            end = remaining;
        }

        if (end == buffer.Length)
        {
            if (buffer.Length >= MaxBufferSize)
            {
                throw new InvalidDataException($"A single JSON value is larger than {MaxBufferSize} bytes.");
            }

            Array.Resize(ref buffer, buffer.Length * 2);
            BufferGrowths++;
        }

        int read = stream.Read(buffer, end, buffer.Length - end);
        if (read == 0)
        {
            isFinalBlock = true;
        }
        else
        {
            end += read;
        }
    }
}
