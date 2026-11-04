using System.Text.Json;
using Microsoft.Net.Http.Headers;
using ReportService.Api.Http;

namespace ReportService.Api.Jobs;

/// <summary>
/// Reads the document that creates a report job:
/// <c>{ "template": { "id", "version" }, "data": { "uploadId", "profile" }, "outputs": [ ] }</c>.
/// The document is read here, explicitly and within a bound, instead of through
/// automatic model binding, whose failures would answer with framework errors
/// carrying no stable code. Reading it is strict on purpose — one JSON object, only
/// the known members at every depth, each once and each of its declared type, and
/// at least one output, each named once. Every refusal is one condition chosen by
/// the fixed precedence of the API's malformed-request contract, described with a
/// known field path and never with a value the caller sent.
/// </summary>
public static class CreateReportJobRequestReader
{
    /// <summary>The largest creation document the operation accepts, in bytes.</summary>
    public const int MaxBodyBytes = 16_384;

    /// <summary>The media type the document must be sent as.</summary>
    public const string JsonMediaType = "application/json";

    /// <summary>The member carrying the template reference.</summary>
    public const string TemplateMember = "template";

    /// <summary>The member carrying the data reference.</summary>
    public const string DataMember = "data";

    /// <summary>The member carrying the requested outputs.</summary>
    public const string OutputsMember = "outputs";

    /// <summary>The member of the template object carrying its identifier.</summary>
    public const string IdMember = "id";

    /// <summary>The member of the template object carrying the version.</summary>
    public const string VersionMember = "version";

    /// <summary>The member of the data object carrying the upload's identifier.</summary>
    public const string UploadIdMember = "uploadId";

    /// <summary>The member of the data object carrying the declared profile.</summary>
    public const string ProfileMember = "profile";

    /// <summary>The field path of the template's identifier.</summary>
    public const string TemplateIdPath = "template.id";

    /// <summary>The field path of the template's version.</summary>
    public const string TemplateVersionPath = "template.version";

    /// <summary>The field path of the upload's identifier.</summary>
    public const string UploadIdPath = "data.uploadId";

    /// <summary>The field path of the declared profile.</summary>
    public const string ProfilePath = "data.profile";

    /// <summary>What an answer says when the request names no output.</summary>
    public const string EmptyOutputsDetail = "The field 'outputs' must name at least one output.";

    /// <summary>What an answer says when the request names an output twice.</summary>
    public const string RepeatedOutputDetail = "The field 'outputs' must not name the same output twice.";

    private const string StringType = "string";

    private const string ObjectType = "object";

    private const string ArrayType = "array";

    private const string ArrayOfStringsType = "array of strings";

    // The reader's own order: it decides which condition is reported when the document has several.
    private static readonly string[] Paths =
    [
        TemplateMember,
        TemplateIdPath,
        TemplateVersionPath,
        DataMember,
        UploadIdPath,
        ProfilePath,
        OutputsMember,
    ];

    private static readonly string[] RequiredPaths =
    [
        TemplateMember,
        TemplateIdPath,
        DataMember,
        UploadIdPath,
        ProfilePath,
        OutputsMember,
    ];

    /// <summary>Reads the job request of <paramref name="request"/>.</summary>
    /// <param name="request">The request being served; its body is read to its end or to the bound.</param>
    /// <param name="cancellationToken">The request's cancellation token.</param>
    public static async Task<Result> ReadAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!IsJson(request.ContentType))
        {
            return new Result.Rejected(InvalidRequestBody.MediaType());
        }

        if (request.ContentLength is { } declared && declared > MaxBodyBytes)
        {
            return new Result.TooLarge();
        }

        var body = await ReadBoundedAsync(request.Body, cancellationToken).ConfigureAwait(false);
        return body is null ? new Result.TooLarge() : Parse(body);
    }

    private static bool IsJson(string? contentType) =>
        MediaTypeHeaderValue.TryParse(contentType, out var mediaType)
        && string.Equals(mediaType.MediaType.Value, JsonMediaType, StringComparison.OrdinalIgnoreCase);

    // Buffers at most one byte past the bound, so an unbounded body is refused without being held.
    private static async Task<byte[]?> ReadBoundedAsync(Stream body, CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        using var buffered = new MemoryStream();
        var total = 0;
        while (true)
        {
            var room = MaxBodyBytes + 1 - total;
            if (room <= 0)
            {
                return null;
            }

            var read = await body.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, room)), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return buffered.ToArray();
            }

            total += read;
            if (total > MaxBodyBytes)
            {
                return null;
            }

            buffered.Write(buffer, 0, read);
        }
    }

    // One JSON object with exactly the known members at every depth, each once and each of its
    // type, and nothing after it; the outputs are one non-empty array of distinct strings.
    private static Result Parse(byte[] body)
    {
        ReadOnlySpan<byte> payload = body;
        if (payload.Length >= 3 && payload[0] == 0xEF && payload[1] == 0xBB && payload[2] == 0xBF)
        {
            payload = payload[3..];
        }

        var reader = new Utf8JsonReader(payload, new JsonReaderOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
        });

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var kinds = new Dictionary<string, JsonTokenType>(StringComparer.Ordinal);
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var outputs = new List<string>();
        var outputsHoldAStringOnly = true;
        var unknown = 0;
        var closed = false;

        try
        {
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                return new Result.Rejected(InvalidRequestBody.MalformedJson());
            }

            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject)
                {
                    closed = true;
                    break;
                }

                if (reader.TokenType != JsonTokenType.PropertyName || reader.GetString() is not { } name || !reader.Read())
                {
                    return new Result.Rejected(InvalidRequestBody.MalformedJson());
                }

                switch (name)
                {
                    case TemplateMember:
                        Count(counts, kinds, name, reader.TokenType);
                        if (reader.TokenType == JsonTokenType.StartObject
                            && !TryReadObject(
                                ref reader,
                                counts,
                                kinds,
                                values,
                                new Dictionary<string, string>(StringComparer.Ordinal)
                                {
                                    [IdMember] = TemplateIdPath,
                                    [VersionMember] = TemplateVersionPath,
                                },
                                ref unknown))
                        {
                            return new Result.Rejected(InvalidRequestBody.MalformedJson());
                        }
                        else if (reader.TokenType == JsonTokenType.StartArray)
                        {
                            reader.Skip();
                        }

                        break;

                    case DataMember:
                        Count(counts, kinds, name, reader.TokenType);
                        if (reader.TokenType == JsonTokenType.StartObject
                            && !TryReadObject(
                                ref reader,
                                counts,
                                kinds,
                                values,
                                new Dictionary<string, string>(StringComparer.Ordinal)
                                {
                                    [UploadIdMember] = UploadIdPath,
                                    [ProfileMember] = ProfilePath,
                                },
                                ref unknown))
                        {
                            return new Result.Rejected(InvalidRequestBody.MalformedJson());
                        }
                        else if (reader.TokenType == JsonTokenType.StartArray)
                        {
                            reader.Skip();
                        }

                        break;

                    case OutputsMember:
                        Count(counts, kinds, name, reader.TokenType);
                        if (reader.TokenType == JsonTokenType.StartArray)
                        {
                            if (!TryReadOutputs(ref reader, outputs, ref outputsHoldAStringOnly))
                            {
                                return new Result.Rejected(InvalidRequestBody.MalformedJson());
                            }
                        }
                        else if (reader.TokenType == JsonTokenType.StartObject)
                        {
                            reader.Skip();
                        }

                        break;

                    default:
                        // An unknown member is unknown once however it is written, and its name is
                        // caller text: the count is all an answer may say about it.
                        unknown++;
                        if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
                        {
                            reader.Skip();
                        }

                        break;
                }
            }

            if (!closed || reader.Read())
            {
                return new Result.Rejected(InvalidRequestBody.MalformedJson());
            }
        }
        catch (JsonException)
        {
            return new Result.Rejected(InvalidRequestBody.MalformedJson());
        }
        catch (InvalidOperationException)
        {
            return new Result.Rejected(InvalidRequestBody.MalformedJson());
        }

        if (unknown > 0)
        {
            return new Result.Rejected(InvalidRequestBody.UnknownField(unknown));
        }

        foreach (var path in Paths)
        {
            if (counts.GetValueOrDefault(path) > 1)
            {
                return new Result.Rejected(InvalidRequestBody.DuplicateField(path));
            }
        }

        foreach (var path in RequiredPaths)
        {
            // A member of a nested object is only required when its object is one: a null or a
            // wrongly typed container is answered as itself, not as its members being absent.
            if (!IsReachable(path, kinds))
            {
                continue;
            }

            if (!counts.TryGetValue(path, out var count) || count == 0)
            {
                return new Result.Rejected(InvalidRequestBody.MissingField(path));
            }
        }

        foreach (var path in Paths)
        {
            if (kinds.TryGetValue(path, out var kind) && kind == JsonTokenType.Null)
            {
                return new Result.Rejected(InvalidRequestBody.NullField(path));
            }
        }

        foreach (var (path, expected) in new[]
                 {
                     (TemplateMember, ObjectType),
                     (DataMember, ObjectType),
                     (OutputsMember, ArrayType),
                     (TemplateIdPath, StringType),
                     (TemplateVersionPath, StringType),
                     (UploadIdPath, StringType),
                     (ProfilePath, StringType),
                 })
        {
            // A member that was not sent at all is not of a wrong type: the optional one is the
            // reference's version, so an absent version is a reference by name.
            if (kinds.TryGetValue(path, out var kind) && kind != ExpectedToken(expected))
            {
                return new Result.Rejected(InvalidRequestBody.WrongType(path, expected));
            }
        }

        if (!outputsHoldAStringOnly)
        {
            return new Result.Rejected(InvalidRequestBody.WrongType(OutputsMember, ArrayOfStringsType));
        }

        if (outputs.Count == 0)
        {
            return new Result.Rejected(InvalidRequestBody.InvalidFormat(OutputsMember, EmptyOutputsDetail));
        }

        if (outputs.Distinct(StringComparer.Ordinal).Count() != outputs.Count)
        {
            return new Result.Rejected(InvalidRequestBody.InvalidFormat(OutputsMember, RepeatedOutputDetail));
        }

        return new Result.Accepted(new CreateReportJobRequest(
            values[TemplateIdPath],
            values.TryGetValue(TemplateVersionPath, out var version) ? version : null,
            values[UploadIdPath],
            values[ProfilePath],
            outputs));
    }

    private static void Count(
        Dictionary<string, int> counts,
        Dictionary<string, JsonTokenType> kinds,
        string path,
        JsonTokenType kind)
    {
        counts[path] = counts.GetValueOrDefault(path) + 1;
        kinds[path] = kind;
    }

    // Reads one known nested object to its end, recording each known member by its path.
    private static bool TryReadObject(
        ref Utf8JsonReader reader,
        Dictionary<string, int> counts,
        Dictionary<string, JsonTokenType> kinds,
        Dictionary<string, string> values,
        IReadOnlyDictionary<string, string> memberPaths,
        ref int unknown)
    {
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                return true;
            }

            if (reader.TokenType != JsonTokenType.PropertyName || reader.GetString() is not { } name || !reader.Read())
            {
                return false;
            }

            if (memberPaths.TryGetValue(name, out var path))
            {
                Count(counts, kinds, path, reader.TokenType);
                if (reader.TokenType == JsonTokenType.String)
                {
                    values[path] = reader.GetString() ?? string.Empty;
                }
                else if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
                {
                    reader.Skip();
                }
            }
            else
            {
                // Unknown at any depth, and never named: the member's name is caller text.
                unknown++;
                if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
                {
                    reader.Skip();
                }
            }
        }

        return false;
    }

    // Reads the outputs array to its end, keeping its strings in the order the request wrote them.
    private static bool TryReadOutputs(ref Utf8JsonReader reader, List<string> outputs, ref bool allStrings)
    {
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndArray)
            {
                return true;
            }

            if (reader.TokenType == JsonTokenType.String)
            {
                outputs.Add(reader.GetString() ?? string.Empty);
                continue;
            }

            allStrings = false;
            if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
            {
                reader.Skip();
            }
        }

        return false;
    }

    private static JsonTokenType ExpectedToken(string expected) => expected switch
    {
        ObjectType => JsonTokenType.StartObject,
        ArrayType => JsonTokenType.StartArray,
        _ => JsonTokenType.String,
    };

    // A top-level member is always reachable; a nested one only when its object was sent as one.
    private static bool IsReachable(string path, Dictionary<string, JsonTokenType> kinds)
    {
        var separator = path.IndexOf('.', StringComparison.Ordinal);
        return separator < 0
            || (kinds.TryGetValue(path[..separator], out var parent) && parent == JsonTokenType.StartObject);
    }

    /// <summary>The outcome of reading a job request.</summary>
    public abstract record Result
    {
        private Result()
        {
        }

        /// <summary>A well-formed request naming something the operation may offer.</summary>
        /// <param name="Request">The members as received.</param>
        public sealed record Accepted(CreateReportJobRequest Request) : Result;

        /// <summary>A malformed request; the condition is the first by the contract's precedence.</summary>
        /// <param name="Problem">The one condition the answer reports.</param>
        public sealed record Rejected(InvalidRequestBody Problem) : Result;

        /// <summary>The body is larger than the operation accepts.</summary>
        public sealed record TooLarge : Result;
    }
}
