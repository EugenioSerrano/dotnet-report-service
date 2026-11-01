namespace ReportService.Api.Http;

/// <summary>
/// The declared consumer identity of a request, carried in the
/// <c>X-Consumer-Id</c> header: a header keeps the identity out of the paths the
/// service logs and lets a gateway in front of the service set it. The service
/// authenticates nobody in this version, so the identity is declared and taken as
/// given; what it insists on is that the declaration is usable — present exactly
/// once, as one value, in the registry's character set.
/// </summary>
public static class ConsumerIdentity
{
    /// <summary>The header that carries the declared consumer identity.</summary>
    public const string HeaderName = "X-Consumer-Id";

    /// <summary>The longest identity the registry admits.</summary>
    public const int MaxLength = 128;

    /// <summary>
    /// Reads the identity of <paramref name="request"/>.
    /// </summary>
    /// <param name="request">The request being served.</param>
    /// <param name="consumerId">The identity as declared, or an empty string when there is none.</param>
    /// <returns>
    /// <see langword="true"/> when the header is present exactly once, carries one
    /// value and that value matches the registry's grammar; otherwise
    /// <see langword="false"/>. Comparison is ordinal and the value is never
    /// normalised.
    /// </returns>
    public static bool TryRead(HttpRequest request, out string consumerId)
    {
        ArgumentNullException.ThrowIfNull(request);
        consumerId = string.Empty;

        if (!request.Headers.TryGetValue(HeaderName, out var values) || values.Count != 1)
        {
            return false;
        }

        var value = values[0];
        if (!IsWellFormed(value))
        {
            return false;
        }

        consumerId = value!;
        return true;
    }

    /// <summary>Whether <paramref name="value"/> has the registry's grammar: 1–128 of <c>A–Z a–z 0–9 . _ -</c>.</summary>
    /// <param name="value">The value to check.</param>
    public static bool IsWellFormed(string? value)
    {
        if (value is null || value.Length is 0 or > MaxLength)
        {
            return false;
        }

        foreach (var character in value)
        {
            if (character is not ((>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '.' or '_' or '-'))
            {
                return false;
            }
        }

        return true;
    }
}
