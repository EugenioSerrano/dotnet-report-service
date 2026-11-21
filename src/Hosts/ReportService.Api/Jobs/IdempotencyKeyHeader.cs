using Microsoft.Extensions.Primitives;
using ReportService.Api.Http;

namespace ReportService.Api.Jobs;

/// <summary>
/// The optional <c>Idempotency-Key</c> header of a job request: exactly one field, one to
/// 255 visible ASCII characters, kept exactly as it was received and compared ordinally.
/// A malformed header is answered as a malformed request naming the header, and the value
/// is never echoed, logged or measured: a consumer may put anything it likes in a key, and
/// the service only ever needs to know whether two keys are the same.
/// </summary>
public static class IdempotencyKeyHeader
{
    /// <summary>The header's name, on the wire and in a refusal.</summary>
    public const string HeaderName = "Idempotency-Key";

    /// <summary>The longest key the header admits.</summary>
    public const int MaxLength = 255;

    /// <summary>What a refusal says. It names the rule and never a value the caller sent.</summary>
    public const string InvalidDetail =
        "The header 'Idempotency-Key' must be 1 to 255 visible ASCII characters, sent once.";

    /// <summary>The reason constant a malformed header is refused with.</summary>
    public const string InvalidReason = RequestInvalidDetails.InvalidFormatReason;

    /// <summary>Reads the header of <paramref name="request"/>.</summary>
    /// <param name="request">The request being served.</param>
    public static Result Read(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        StringValues values = request.Headers[HeaderName];
        if (values.Count == 0)
        {
            return new Result.Absent();
        }

        return values.Count == 1 && IsKey(values[0])
            ? new Result.Present(values[0]!)
            : new Result.Invalid(InvalidRequestBody.InvalidFormat(HeaderName, InvalidDetail));
    }

    /// <summary>
    /// Whether <paramref name="key"/> is a key this service accepts: one to 255 characters,
    /// each of them visible ASCII (<c>!</c> to <c>~</c>).
    /// </summary>
    /// <param name="key">The value to check.</param>
    public static bool IsKey(string? key)
    {
        if (key is null || key.Length is 0 or > MaxLength)
        {
            return false;
        }

        foreach (var character in key)
        {
            if (character is < '!' or > '~')
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>What the header of one request is.</summary>
    public abstract record Result
    {
        private Result()
        {
        }

        /// <summary>The request carried no key.</summary>
        public sealed record Absent : Result;

        /// <summary>The request carried one key, kept exactly as it was received.</summary>
        /// <param name="Key">The value of the header, unmodified.</param>
        public sealed record Present(string Key) : Result;

        /// <summary>The request carried no usable key; the condition is a malformed request.</summary>
        /// <param name="Problem">The one condition the answer reports.</param>
        public sealed record Invalid(InvalidRequestBody Problem) : Result;
    }
}
