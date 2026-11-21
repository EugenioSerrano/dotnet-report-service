using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace ReportService.TemplatePackages.Keys;

/// <summary>
/// How a caller's value becomes one segment of a storage key. A value that
/// already reads as a segment is kept as it is, so ordinary keys stay readable;
/// every other value becomes <c>h-</c> and the first 32 hexadecimal characters
/// of its SHA-256, so the encoding is total, has no escaping ambiguity and
/// never carries a value a provider would refuse. The two ranges are disjoint
/// by length — at most 32 characters, or exactly 34 — so two values encode to
/// the same segment only if they are equal or their hashes collide.
/// </summary>
public static partial class KeySegmentEncoding
{
    /// <summary>The longest value kept as it is.</summary>
    public const int MaxRawLength = 32;

    /// <summary>The prefix of an encoded value.</summary>
    public const string HashedPrefix = "h-";

    /// <summary>The length of every encoded value.</summary>
    public const int HashedLength = 34;

    private static readonly string[] DeviceNames =
    [
        "con", "prn", "aux", "nul",
        "com1", "com2", "com3", "com4", "com5", "com6", "com7", "com8", "com9",
        "lpt1", "lpt2", "lpt3", "lpt4", "lpt5", "lpt6", "lpt7", "lpt8", "lpt9",
    ];

    /// <summary>True when <paramref name="value"/> is kept as it is.</summary>
    /// <param name="value">The caller's value.</param>
    public static bool IsRaw(string? value) =>
        value is not null
        && RawSegment().IsMatch(value)
        && Array.IndexOf(DeviceNames, value.Split('.')[0]) < 0;

    /// <summary>The key segment of <paramref name="value"/>.</summary>
    /// <param name="value">The caller's value; required.</param>
    public static string Encode(string? value)
    {
        ArgumentException.ThrowIfNullOrEmpty(value);
        return IsRaw(value) ? value : HashedPrefix + Hash(value);
    }

    private static string Hash(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..(HashedLength - HashedPrefix.Length)];

    [GeneratedRegex(@"^[a-z0-9]([a-z0-9._-]{0,30}[a-z0-9])?$", RegexOptions.CultureInvariant)]
    private static partial Regex RawSegment();
}
