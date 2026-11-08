using ReportService.TemplatePackages.Manifest;

namespace ReportService.TemplatePackages.Records;

/// <summary>
/// What a job's reference carries once its version was resolved: the values the
/// record fixed, copied once and never recomputed. The writer of a job's
/// reference and the reader of its package share this type, so a pinned job can
/// only ever read the version its record names. The pin records one of the two
/// source shapes the taken version holds - the commit a version taken before the
/// object-store source was read at, or the publication descriptor's hash and the
/// verified file list of a version taken from it - and never a mixture or
/// neither. The owner, the shared flag and the descriptor's key are the taken
/// version's, and the pin has no member that could repeat them.
/// </summary>
/// <param name="TemplateId">The template's identifier.</param>
/// <param name="ResolvedVersion">The version the reference resolved to.</param>
/// <param name="CommitSha">The commit that version was read at; null for a version taken from the object store.</param>
/// <param name="SourceDescriptorSha256">The SHA-256 of the publication descriptor the take verified; a version taken from the object store only.</param>
/// <param name="SourceObjects">The files the take verified; a version taken from the object store only.</param>
/// <param name="PackageSha256">The SHA-256 of the taken package.</param>
/// <param name="Engine">The engine that renders it.</param>
/// <param name="EngineVersion">The engine's version.</param>
/// <param name="SchemaRef">The package-relative path of its schema.</param>
/// <param name="ResolvedAt">When the reference was resolved.</param>
public sealed record TemplatePin(
    string TemplateId,
    string ResolvedVersion,
    string? CommitSha,
    string? SourceDescriptorSha256,
    TemplateSourceObject[]? SourceObjects,
    string PackageSha256,
    TemplateEngine Engine,
    string EngineVersion,
    string SchemaRef,
    DateTimeOffset ResolvedAt)
{
    /// <summary>The pin of <paramref name="record"/>, resolved at <paramref name="resolvedAt"/>.</summary>
    /// <param name="record">The record the reference resolved to.</param>
    /// <param name="resolvedAt">When the reference was resolved.</param>
    /// <exception cref="InvalidOperationException">The record carries both source shapes, or neither.</exception>
    public static TemplatePin From(TemplateVersionRecord record, DateTimeOffset resolvedAt)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (record.SourceDescriptorSha256 is not null)
        {
            if (record.CommitSha is not null || record.SourceTreeSha is not null)
            {
                throw new InvalidOperationException("the version record carries two source provenances");
            }

            if (record.SourceObjects is null)
            {
                throw new InvalidOperationException("the version record carries no source provenance");
            }

            // The pin is the job's own statement of what it rendered: it never points at
            // the record's array, so a later reader of the record cannot move the job.
            return new TemplatePin(
                record.TemplateId,
                record.Version,
                null,
                record.SourceDescriptorSha256,
                [.. record.SourceObjects],
                record.PackageSha256,
                record.Engine,
                record.EngineVersion,
                record.SchemaRef,
                resolvedAt);
        }

        if (record.CommitSha is not null && record.SourceTreeSha is not null)
        {
            return new TemplatePin(
                record.TemplateId,
                record.Version,
                record.CommitSha,
                null,
                null,
                record.PackageSha256,
                record.Engine,
                record.EngineVersion,
                record.SchemaRef,
                resolvedAt);
        }

        throw new InvalidOperationException("the version record carries no source provenance");
    }
}
