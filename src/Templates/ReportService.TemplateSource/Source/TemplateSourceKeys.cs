using System.Text.RegularExpressions;
using ReportService.Storage;
using ReportService.TemplatePackages.Keys;
using ReportService.TemplatePackages.Packages;

namespace ReportService.TemplateSource.Source;

/// <summary>
/// The layout of the deployment's <c>template-source</c> class and the only
/// builder of a key into it. A version's files live under
/// <c>template-source/{template}/files/{version}/{path}</c> and its publication
/// record under <c>template-source/{template}/published/{version}/{sha256}.json</c>,
/// where every caller value is encoded by <see cref="KeySegmentEncoding"/> - except
/// the record's last segment, which <b>is</b> the record's own SHA-256 and is never
/// encoded again. A file key is at most twelve of the port's sixteen segments and
/// comfortably inside its 512 characters. A prefix is a string the port's listing
/// takes; a key is built only from validated segments.
/// </summary>
public static partial class TemplateSourceKeys
{
    /// <summary>The first segment of every key of the class.</summary>
    public const string ClassPrefix = "template-source";

    /// <summary>The segment that holds a version's publication record.</summary>
    public const string PublishedSegment = "published";

    /// <summary>The segment that holds a version's files.</summary>
    public const string FilesSegment = "files";

    /// <summary>The constant suffix of a publication record's object name.</summary>
    public const string DescriptorSuffix = ".json";

    /// <summary>The length of the SHA-256 text a record's name carries.</summary>
    public const int DescriptorHashLength = 64;

    /// <summary>The published prefix of one template, as a listing takes it.</summary>
    /// <param name="templateId">The template's identifier.</param>
    public static string PublishedPrefix(string templateId) =>
        $"{ClassPrefix}/{KeySegmentEncoding.Encode(templateId)}/{PublishedSegment}";

    /// <summary>The published prefix of one version, as the exact read lists it.</summary>
    /// <param name="templateId">The template's identifier.</param>
    /// <param name="version">The version.</param>
    public static string PublishedVersionPrefix(string templateId, string version) =>
        $"{PublishedPrefix(templateId)}/{KeySegmentEncoding.Encode(version)}";

    /// <summary>The files prefix of one version, as the incomplete check lists it.</summary>
    /// <param name="templateId">The template's identifier.</param>
    /// <param name="version">The version.</param>
    public static string FilesPrefix(string templateId, string version) =>
        $"{ClassPrefix}/{KeySegmentEncoding.Encode(templateId)}/{FilesSegment}/{KeySegmentEncoding.Encode(version)}";

    /// <summary>The key of one publication record. Its last segment is the hash itself.</summary>
    /// <param name="templateId">The template's identifier.</param>
    /// <param name="version">The version.</param>
    /// <param name="sha256">The record's SHA-256, lower-case hexadecimal.</param>
    public static ObjectKey Descriptor(string templateId, string version, string sha256)
    {
        if (!Sha256Text.IsValid(sha256))
        {
            throw new ArgumentException("a publication record's name is 64 lower-case hexadecimal characters", nameof(sha256));
        }

        return ObjectKey.FromSegments(
            ClassPrefix,
            KeySegmentEncoding.Encode(templateId),
            PublishedSegment,
            KeySegmentEncoding.Encode(version),
            sha256 + DescriptorSuffix);
    }

    /// <summary>The key of one file of a version.</summary>
    /// <param name="templateId">The template's identifier.</param>
    /// <param name="version">The version.</param>
    /// <param name="path">The package-relative path, with <c>/</c> separators.</param>
    /// <exception cref="ArgumentException">The path is not one a package may hold.</exception>
    public static ObjectKey File(string templateId, string version, string path)
    {
        if (!PackagePath.IsValid(path, out var reason))
        {
            throw new ArgumentException($"the path is not one a template package may hold: {reason}", nameof(path));
        }

        var segments = new List<string>
        {
            ClassPrefix,
            KeySegmentEncoding.Encode(templateId),
            FilesSegment,
            KeySegmentEncoding.Encode(version),
        };
        segments.AddRange(path.Split('/').Select(KeySegmentEncoding.Encode));
        return ObjectKey.FromSegments([.. segments]);
    }

    /// <summary>
    /// Reads one listing entry as the publication record of the exact version:
    /// its key's segments must be the class, the template, <c>published</c>, the
    /// version and the record's name - never a string prefix, so
    /// <c>1.1.0-rc.1</c> is never the record of <c>1.1.0</c>.
    /// </summary>
    /// <param name="key">The entry's key.</param>
    /// <param name="templateId">The requested template's identifier.</param>
    /// <param name="version">The requested version.</param>
    /// <param name="sha256">The hash the record's name carries.</param>
    /// <param name="reason">Why the entry is not the version's record; empty on success.</param>
    public static bool TryReadDescriptorName(ObjectKey key, string templateId, string version, out string sha256, out string reason)
    {
        sha256 = string.Empty;
        reason = string.Empty;
        var segments = key.Segments;
        if (segments.Count != 5
            || !string.Equals(segments[0], ClassPrefix, StringComparison.Ordinal)
            || !string.Equals(segments[1], KeySegmentEncoding.Encode(templateId), StringComparison.Ordinal)
            || !string.Equals(segments[2], PublishedSegment, StringComparison.Ordinal)
            || !string.Equals(segments[3], KeySegmentEncoding.Encode(version), StringComparison.Ordinal))
        {
            reason = "the entry is not one version's publication record";
            return false;
        }

        if (!TryReadName(segments[4], out sha256))
        {
            reason = "the entry's name is not 64 lower-case hexadecimal characters followed by '.json'";
            return false;
        }

        return true;
    }

    /// <summary>
    /// Reads one listing entry as any publication record of a template: the
    /// class, the template, <c>published</c>, a version segment and the record's
    /// name. Which version the segment means is the caller's rule.
    /// </summary>
    /// <param name="key">The entry's key.</param>
    /// <param name="templateId">The template's identifier.</param>
    /// <param name="versionSegment">The entry's version segment.</param>
    /// <param name="sha256">The hash the record's name carries.</param>
    /// <param name="reason">Why the entry is not a record; empty on success.</param>
    public static bool TryReadPublishedRecord(ObjectKey key, string templateId, out string versionSegment, out string sha256, out string reason)
    {
        versionSegment = string.Empty;
        sha256 = string.Empty;
        reason = string.Empty;
        var segments = key.Segments;
        if (segments.Count != 5
            || !string.Equals(segments[0], ClassPrefix, StringComparison.Ordinal)
            || !string.Equals(segments[1], KeySegmentEncoding.Encode(templateId), StringComparison.Ordinal)
            || !string.Equals(segments[2], PublishedSegment, StringComparison.Ordinal))
        {
            reason = "the entry is not under a template's published prefix";
            return false;
        }

        if (!TryReadName(segments[4], out sha256))
        {
            reason = "the entry's name is not 64 lower-case hexadecimal characters followed by '.json'";
            return false;
        }

        versionSegment = segments[3];
        return true;
    }

    /// <summary>True when <paramref name="key"/> holds at least one segment below <paramref name="prefix"/>.</summary>
    /// <param name="key">The entry's key.</param>
    /// <param name="prefix">The listing prefix, as <see cref="FilesPrefix"/> builds it.</param>
    public static bool IsUnderPrefix(ObjectKey key, string prefix)
    {
        var expected = prefix.Split('/');
        var segments = key.Segments;
        if (segments.Count <= expected.Length)
        {
            return false;
        }

        for (var index = 0; index < expected.Length; index++)
        {
            if (!string.Equals(segments[index], expected[index], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryReadName(string name, out string sha256)
    {
        sha256 = string.Empty;
        if (name.Length != DescriptorHashLength + DescriptorSuffix.Length
            || !name.EndsWith(DescriptorSuffix, StringComparison.Ordinal))
        {
            return false;
        }

        var candidate = name[..DescriptorHashLength];
        if (!Sha256Text.IsValid(candidate))
        {
            return false;
        }

        sha256 = candidate;
        return true;
    }
}
