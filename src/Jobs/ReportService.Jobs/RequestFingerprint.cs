namespace ReportService.Jobs;

/// <summary>
/// The stored identity of a job request: the version of the canonical form the
/// fingerprint was computed with, then the SHA-256 of the request's members as
/// lower-case hexadecimal. The value is an identity, not a secret and not a
/// signature: it says whether two requests asked for the same thing, and nothing
/// about what they asked for. A job's fingerprint is written once, with the job,
/// and never changes.
/// </summary>
public readonly record struct RequestFingerprint
{
    /// <summary>The prefix the first version of the canonical form is stored with.</summary>
    public const string FirstVersionPrefix = "v1:";

    /// <summary>How many hexadecimal characters a digest is written with.</summary>
    public const int DigestLength = 64;

    /// <summary>The most version digits the stored form admits.</summary>
    public const int MaxVersionDigits = 3;

    /// <summary>Wraps a stored value exactly as it is read back.</summary>
    /// <param name="value">The stored form, <c>v&lt;N&gt;:&lt;64 lower-case hex&gt;</c>.</param>
    public RequestFingerprint(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        Value = value;
    }

    /// <summary>The stored form, exactly as the database holds it.</summary>
    public string Value { get; }

    /// <summary>
    /// Whether <paramref name="value"/> is a stored fingerprint this service wrote:
    /// <c>v</c>, one to three version digits without a leading zero, a colon, and
    /// exactly sixty-four lower-case hexadecimal characters.
    /// </summary>
    /// <param name="value">The value to check.</param>
    public static bool IsWellFormed(string? value)
    {
        if (value is null || value.Length < 2 + 1 + DigestLength)
        {
            return false;
        }

        if (value[0] != 'v')
        {
            return false;
        }

        var digits = 0;
        while (1 + digits < value.Length && char.IsAsciiDigit(value[1 + digits]))
        {
            digits++;
            if (digits > MaxVersionDigits)
            {
                return false;
            }
        }

        if (digits == 0 || value[1] == '0')
        {
            return false;
        }

        var colon = 1 + digits;
        if (colon >= value.Length || value[colon] != ':' || value.Length != colon + 1 + DigestLength)
        {
            return false;
        }

        for (var index = colon + 1; index < value.Length; index++)
        {
            var character = value[index];
            if (character is not ((>= '0' and <= '9') or (>= 'a' and <= 'f')))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Wraps the digest of a request computed with the first canonical form.</summary>
    /// <param name="digest">The SHA-256 of the canonical bytes.</param>
    public static RequestFingerprint FromDigest(ReadOnlySpan<byte> digest) =>
        new(FirstVersionPrefix + Convert.ToHexStringLower(digest));

    /// <summary>The stored form, so a fingerprint is logged and compared as the value it is.</summary>
    public override string ToString() => Value;
}
