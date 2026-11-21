using System.Globalization;

namespace ReportService.Jobs;

/// <summary>
/// The identifiers of the artifacts a job publishes: the prefix <c>artifact-</c>
/// followed by 32 lower-case hexadecimal characters, 128 random bits that are
/// neither ordered nor guessable. The same check decides whether a value read
/// from a caller may be looked up, so a reference to an artifact that cannot
/// exist costs no read; it is the grammar the publication writes and the
/// download route accepts.
/// </summary>
public static class ArtifactIdentifier
{
    /// <summary>The prefix of every artifact identifier.</summary>
    public const string Prefix = "artifact-";

    /// <summary>The number of hexadecimal characters after the prefix.</summary>
    public const int RandomHexLength = 32;

    /// <summary>Creates a new artifact identifier.</summary>
    public static string Create() => Prefix + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);

    /// <summary>Whether <paramref name="value"/> has the form of an artifact identifier, in ordinal comparison.</summary>
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
