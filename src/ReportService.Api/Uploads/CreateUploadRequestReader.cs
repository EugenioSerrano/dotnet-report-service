using System.Text.Json;
using Microsoft.Net.Http.Headers;
using ReportService.Api.Http;

namespace ReportService.Api.Uploads;

/// <summary>
/// Reads the small control document that opens an upload session:
/// <c>{ "profile": "…", "mode": "…" }</c>. The document is read here, explicitly
/// and within a bound, instead of through automatic model binding, whose failures
/// would answer with framework errors carrying no stable code. Reading it is
/// strict on purpose — one JSON object, exactly the two known members, each once
/// and each a string — and every refusal is one condition chosen by a fixed
/// precedence, described with a known field path and never with a value the
/// caller sent.
/// </summary>
public static class CreateUploadRequestReader
{
    /// <summary>The largest creation document the operation accepts, in bytes.</summary>
    public const int MaxBodyBytes = 16_384;

    /// <summary>The media type the document must be sent as.</summary>
    public const string JsonMediaType = "application/json";

    /// <summary>The member naming the declared data profile.</summary>
    public const string ProfileMember = "profile";

    /// <summary>The member naming the declared upload mode.</summary>
    public const string ModeMember = "mode";

    // The reader's own order: it decides which condition is reported when the document has several.
    private static readonly string[] Members = [ProfileMember, ModeMember];

    private const string StringType = "string";

    /// <summary>Reads the session request of <paramref name="request"/>.</summary>
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

    // One JSON object with exactly the known members, each once and each a string, and nothing after it.
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

                var known = name is ProfileMember or ModeMember;
                var kind = reader.TokenType;
                if (kind is JsonTokenType.StartObject or JsonTokenType.StartArray)
                {
                    reader.Skip();
                }
                else if (kind == JsonTokenType.String && known)
                {
                    values[name] = reader.GetString() ?? string.Empty;
                }

                if (known)
                {
                    counts[name] = counts.GetValueOrDefault(name) + 1;
                    kinds[name] = kind;
                }
                else
                {
                    // A repeated unknown member is unknown twice: the count is all an answer may say about it.
                    unknown++;
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

        foreach (var member in Members)
        {
            if (counts.GetValueOrDefault(member) > 1)
            {
                return new Result.Rejected(InvalidRequestBody.DuplicateField(member));
            }
        }

        foreach (var member in Members)
        {
            if (!counts.TryGetValue(member, out var count) || count == 0)
            {
                return new Result.Rejected(InvalidRequestBody.MissingField(member));
            }

            if (kinds[member] == JsonTokenType.Null)
            {
                return new Result.Rejected(InvalidRequestBody.NullField(member));
            }

            if (kinds[member] != JsonTokenType.String)
            {
                return new Result.Rejected(InvalidRequestBody.WrongType(member, StringType));
            }
        }

        return new Result.Accepted(new CreateUploadRequest(values[ProfileMember], values[ModeMember]));
    }

    /// <summary>The outcome of reading a session request.</summary>
    public abstract record Result
    {
        private Result()
        {
        }

        /// <summary>A well-formed request naming something the operation may offer.</summary>
        /// <param name="Request">The two declared strings, as received.</param>
        public sealed record Accepted(CreateUploadRequest Request) : Result;

        /// <summary>A malformed request; the condition is the first by the contract's precedence.</summary>
        /// <param name="Problem">The one condition the answer reports.</param>
        public sealed record Rejected(InvalidRequestBody Problem) : Result;

        /// <summary>The body is larger than the operation accepts.</summary>
        public sealed record TooLarge : Result;
    }
}
