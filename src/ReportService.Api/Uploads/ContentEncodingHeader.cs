using ReportService.Uploads;

namespace ReportService.Api.Uploads;

/// <summary>
/// Reads how a body is declared encoded from the request's
/// <c>Content-Encoding</c> header. The service decodes nothing itself at transfer
/// time: it classifies the declaration into the three values the record store
/// keeps, and an encoding it does not decode is recorded and refused later, at
/// confirmation. The header's text is never stored, logged or echoed — only this
/// classification leaves this type.
/// </summary>
public static class ContentEncodingHeader
{
    /// <summary>The header that declares the body's encoding.</summary>
    public const string HeaderName = "Content-Encoding";

    /// <summary>The one encoding the service decodes.</summary>
    public const string Gzip = "gzip";

    /// <summary>The encoding that means "not encoded".</summary>
    public const string Identity = "identity";

    /// <summary>Classifies the encoding <paramref name="request"/> declares.</summary>
    /// <param name="request">The request being served.</param>
    /// <returns>
    /// <see cref="UploadContentEncoding.None"/> when the header is absent or declares
    /// <c>identity</c> once, <see cref="UploadContentEncoding.Gzip"/> when it declares
    /// <c>gzip</c> once, and <see cref="UploadContentEncoding.Other"/> for every other
    /// declaration — another token, a list, several headers or an empty value.
    /// </returns>
    public static UploadContentEncoding Classify(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!request.Headers.TryGetValue(HeaderName, out var values) || values.Count == 0)
        {
            return UploadContentEncoding.None;
        }

        if (values.Count != 1 || values[0] is not { Length: > 0 } value)
        {
            return UploadContentEncoding.Other;
        }

        if (string.Equals(value, Identity, StringComparison.OrdinalIgnoreCase))
        {
            return UploadContentEncoding.None;
        }

        return string.Equals(value, Gzip, StringComparison.OrdinalIgnoreCase)
            ? UploadContentEncoding.Gzip
            : UploadContentEncoding.Other;
    }
}
