using System.Text.RegularExpressions;
using ReportService.Storage;

namespace ReportService.TemplatePackages.Keys;

/// <summary>
/// The key a taken version's package is stored under:
/// <c>templates/&lt;owner&gt;/&lt;templateId&gt;/&lt;version&gt;/&lt;sha256&gt;</c>, the last segment
/// being the hash of the package's bytes. The template source's own path is
/// never part of a key: a key says what the service keeps, never where it read
/// it from.
/// </summary>
public static partial class TemplatePackageKeys
{
    /// <summary>The first segment of every package key.</summary>
    public const string ClassPrefix = "templates";

    /// <summary>The longest segment this class writes.</summary>
    public const int MaxSegmentLength = KeySegmentEncoding.HashedLength;

    /// <summary>The longest key this class can build, in characters: the class and its separator, three segments and their separators, and the hash.</summary>
    public const int MaxKeyLength = 10 + (MaxSegmentLength + 1) + (MaxSegmentLength + 1) + (MaxSegmentLength + 1) + 64;

    /// <summary>The key of the package of one taken version.</summary>
    /// <param name="ownerConsumerId">The consumer the version is taken for.</param>
    /// <param name="templateId">The template's identifier.</param>
    /// <param name="version">The version, as the manifest declares it.</param>
    /// <param name="packageSha256">The SHA-256 of the package's bytes, lower-case hexadecimal.</param>
    public static ObjectKey ForVersion(string ownerConsumerId, string templateId, string version, string packageSha256)
    {
        ArgumentException.ThrowIfNullOrEmpty(ownerConsumerId);
        ArgumentException.ThrowIfNullOrEmpty(templateId);
        ArgumentException.ThrowIfNullOrEmpty(version);
        ArgumentException.ThrowIfNullOrEmpty(packageSha256);
        if (!IsSha256(packageSha256))
        {
            throw new ArgumentException("a package hash is 64 lower-case hexadecimal characters", nameof(packageSha256));
        }

        return ObjectKey.FromSegments(
            ClassPrefix,
            KeySegmentEncoding.Encode(ownerConsumerId),
            KeySegmentEncoding.Encode(templateId),
            KeySegmentEncoding.Encode(version),
            packageSha256);
    }

    /// <summary>True for a lower-case hexadecimal SHA-256.</summary>
    /// <param name="value">The value to check.</param>
    public static bool IsSha256(string? value) => value is not null && Sha256().IsMatch(value);

    [GeneratedRegex(@"^[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256();
}
