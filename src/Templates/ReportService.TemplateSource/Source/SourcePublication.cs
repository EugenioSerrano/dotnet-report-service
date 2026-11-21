using ReportService.Storage;

namespace ReportService.TemplateSource.Source;

/// <summary>
/// One verified publication: the identity of the record it was read from - the
/// hash its own name carried and the size the listing gave - and every file the
/// record declares, with the size and SHA-256 the take must verify it against.
/// Nothing here is a settings value or a provider's claim: the record is the
/// publication's own statement and the store verified the bytes against it.
/// </summary>
/// <param name="TemplateId">The template the publication belongs to.</param>
/// <param name="Version">The version the record declares.</param>
/// <param name="DescriptorKey">Where the record was read.</param>
/// <param name="DescriptorSha256">The record's SHA-256, lower-case hexadecimal.</param>
/// <param name="DescriptorSize">The record's size in bytes, as the listing gave it.</param>
/// <param name="Files">Every file the record declares, in the record's order.</param>
/// <param name="FileCount">The record's file count, checked against <paramref name="Files"/>.</param>
/// <param name="TotalSize">The record's total size, checked against the files.</param>
public sealed record SourcePublication(
    string TemplateId,
    string Version,
    ObjectKey DescriptorKey,
    string DescriptorSha256,
    long DescriptorSize,
    IReadOnlyList<SourceFile> Files,
    int FileCount,
    long TotalSize);

/// <summary>One file of a publication, as the record declares it.</summary>
/// <param name="Path">The package-relative path, with <c>/</c> separators.</param>
/// <param name="Size">The size the take verifies the bytes against.</param>
/// <param name="Sha256">The SHA-256 the take verifies the bytes against, lower-case hexadecimal.</param>
public sealed record SourceFile(string Path, long Size, string Sha256);
