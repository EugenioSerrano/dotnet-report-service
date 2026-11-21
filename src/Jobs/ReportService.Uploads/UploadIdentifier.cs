using System.Globalization;

namespace ReportService.Uploads;

/// <summary>
/// The identifier of an upload session: the prefix <c>upload-</c> followed by 32
/// lower-case hexadecimal characters, 122 random bits that are neither ordered
/// nor guessable and that satisfy the storage key grammar as one segment. The
/// same function decides whether a value read from a route may be looked up, so
/// a reference to a session that cannot exist costs no read.
/// </summary>
public static class UploadIdentifier
{
    /// <summary>The prefix of every upload identifier.</summary>
    public const string Prefix = "upload-";

    /// <summary>The number of hexadecimal characters after the prefix.</summary>
    public const int RandomHexLength = 32;

    /// <summary>Creates a new identifier.</summary>
    public static string Create() => Prefix + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);

    /// <summary>Whether <paramref name="value"/> has the form of an identifier, in ordinal comparison.</summary>
    /// <param name="value">The value to check.</param>
    public static bool IsWellFormed(string? value)
    {
        if (value is null
            || value.Length != Prefix.Length + RandomHexLength
            || !value.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var character in value.AsSpan(Prefix.Length))
        {
            if (character is not ((>= '0' and <= '9') or (>= 'a' and <= 'f')))
            {
                return false;
            }
        }

        return true;
    }
}
