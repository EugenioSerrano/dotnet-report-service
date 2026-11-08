using ReportService.Storage;

namespace ReportService.TemplateSource.Source;

/// <summary>One publication record a listing returned, with the identity its own name carries.</summary>
/// <param name="Key">The object's key.</param>
/// <param name="Sha256">The SHA-256 the record's name carries.</param>
/// <param name="Size">The size the listing gave.</param>
public sealed record SourceDescriptorName(ObjectKey Key, string Sha256, long Size);

/// <summary>
/// One enumerated publication: the version's text and the record's identity. Two
/// entries may carry the same text - grouping them and deciding that a repeated
/// version is a duplicate is the caller's rule, never the enumeration's.
/// </summary>
/// <param name="Version">The version's text.</param>
/// <param name="DescriptorKey">Where the record was found.</param>
/// <param name="DescriptorSha256">The record's SHA-256, lower-case hexadecimal.</param>
/// <param name="DescriptorSize">The record's size, as the listing gave it.</param>
public sealed record PublishedVersion(
    string Version,
    ObjectKey DescriptorKey,
    string DescriptorSha256,
    long DescriptorSize);
