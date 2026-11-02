using System.Globalization;

namespace ReportService.Jobs;

/// <summary>
/// The identifiers of a job and of its requested outputs: the prefix
/// <c>job-</c> or <c>output-</c> followed by 32 lower-case hexadecimal
/// characters, 122 random bits that are neither ordered nor guessable. The same
/// functions decide whether a value read from a caller may be looked up, so a
/// reference to a job that cannot exist costs no read.
/// </summary>
public static class JobIdentifier
{
    /// <summary>The prefix of every job identifier.</summary>
    public const string JobPrefix = "job-";

    /// <summary>The prefix of every output identifier.</summary>
    public const string OutputPrefix = "output-";

    /// <summary>The number of hexadecimal characters after a prefix.</summary>
    public const int RandomHexLength = 32;

    /// <summary>Creates a new job identifier.</summary>
    public static string Create() => JobPrefix + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);

    /// <summary>Creates a new output identifier.</summary>
    public static string CreateOutput() => OutputPrefix + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);

    /// <summary>Whether <paramref name="value"/> has the form of a job identifier, in ordinal comparison.</summary>
    /// <param name="value">The value to check.</param>
    public static bool IsWellFormed(string? value) => IsWellFormed(value, JobPrefix);

    /// <summary>Whether <paramref name="value"/> has the form of an output identifier, in ordinal comparison.</summary>
    /// <param name="value">The value to check.</param>
    public static bool IsWellFormedOutput(string? value) => IsWellFormed(value, OutputPrefix);

    private static bool IsWellFormed(string? value, string prefix)
    {
        if (value is null
            || value.Length != prefix.Length + RandomHexLength
            || !value.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var character in value.AsSpan(prefix.Length))
        {
            if (character is not ((>= '0' and <= '9') or (>= 'a' and <= 'f')))
            {
                return false;
            }
        }

        return true;
    }
}
