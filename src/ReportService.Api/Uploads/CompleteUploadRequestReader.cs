using System.Text.Json;
using Microsoft.Net.Http.Headers;
using ReportService.Api.Http;

namespace ReportService.Api.Uploads;

/// <summary>
/// Reads the optional document that confirms an upload:
/// <c>{ "sha256": "…" }</c>. The body may be absent, in which case nothing is
/// declared; when it is present it is read within the same bound and by the same
/// rules as the document that opens a session — one JSON object, one known member
/// at most, each once and each a string of the shape it declares — and every
/// refusal names the known field, never a value the caller sent.
/// </summary>
public static class CompleteUploadRequestReader
{
    /// <summary>The largest confirmation document the operation accepts, in bytes.</summary>
    public const int MaxBodyBytes = 16_384;

    /// <summary>The media type the document must be sent as when it is sent.</summary>
    public const string JsonMediaType = CreateUploadRequestReader.JsonMediaType;

    /// <summary>The member that declares the expected content hash.</summary>
    public const string Sha256Member = "sha256";

    /// <summary>The constant sentence a wrongly shaped hash is refused with.</summary>
    public const string Sha256Detail = "The field 'sha256' must be 64 hexadecimal characters.";

    private const string StringType = "string";

    /// <summary>Reads the confirmation document of <paramref name="request"/>.</summary>
    /// <param name="request">The request being served.</param>
    /// <param name="cancellationToken">The request's cancellation token.</param>
    public static async Task<Result> ReadAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!HasBody(request))
        {
            return new Result.Accepted(null);
        }

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

    // A request carries a body when it declares a length above zero, or when it declares
    // no length and streams one; a declared length of zero declares nothing.
    private static bool HasBody(HttpRequest request) =>
        request.ContentLength is > 0
        || (request.ContentLength is null && request.Headers.ContainsKey(HeaderNames.TransferEncoding));

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

    // One JSON object with the one known member at most, once, as a string of the declared shape.
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

        var count = 0;
        var kind = JsonTokenType.None;
        string? value = null;
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

                var known = string.Equals(name, Sha256Member, StringComparison.Ordinal);
                var tokenType = reader.TokenType;
                if (tokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
                {
                    reader.Skip();
                }
                else if (tokenType == JsonTokenType.String && known)
                {
                    value = reader.GetString();
                }

                if (known)
                {
                    count++;
                    kind = tokenType;
                }
                else
                {
                    // A member the operation does not know is counted, never named.
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

        if (count > 1)
        {
            return new Result.Rejected(InvalidRequestBody.DuplicateField(Sha256Member));
        }

        if (count == 0)
        {
            // An empty object declares nothing, which is valid: the hash is optional.
            return new Result.Accepted(null);
        }

        if (kind == JsonTokenType.Null)
        {
            return new Result.Rejected(InvalidRequestBody.NullField(Sha256Member));
        }

        if (kind != JsonTokenType.String)
        {
            return new Result.Rejected(InvalidRequestBody.WrongType(Sha256Member, StringType));
        }

        if (!IsSha256(value))
        {
            return new Result.Rejected(InvalidRequestBody.InvalidFormat(Sha256Member, Sha256Detail));
        }

        return new Result.Accepted(value!.ToLowerInvariant());
    }

    private static bool IsSha256(string? value)
    {
        if (value is not { Length: 64 })
        {
            return false;
        }

        foreach (var character in value)
        {
            if (!char.IsAsciiHexDigit(character))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The outcome of reading a confirmation request.</summary>
    public abstract record Result
    {
        private Result()
        {
        }

        /// <summary>A well-formed request, declaring the content hash or nothing.</summary>
        /// <param name="Sha256">The declared hash in lower-case hexadecimal, or <see langword="null"/>.</param>
        public sealed record Accepted(string? Sha256) : Result;

        /// <summary>A malformed request; the condition is the first by the contract's precedence.</summary>
        /// <param name="Problem">The one condition the answer reports.</param>
        public sealed record Rejected(InvalidRequestBody Problem) : Result;

        /// <summary>The body is larger than the operation accepts.</summary>
        public sealed record TooLarge : Result;
    }
}
