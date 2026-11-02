using ReportService.TemplatePackages.Manifest;

namespace ReportService.TemplatePackages.Records;

/// <summary>
/// What a job's reference carries once its version was resolved: the values
/// the record fixed, copied once and never recomputed. The writer of a job's
/// reference and the reader of its package share this type, so a pinned job can
/// only ever read the version its record names.
/// </summary>
/// <param name="TemplateId">The template's identifier.</param>
/// <param name="ResolvedVersion">The version the reference resolved to.</param>
/// <param name="CommitSha">The commit that version was read at.</param>
/// <param name="PackageSha256">The SHA-256 of the taken package.</param>
/// <param name="Engine">The engine that renders it.</param>
/// <param name="EngineVersion">The engine's version.</param>
/// <param name="SchemaRef">The package-relative path of its schema.</param>
/// <param name="ResolvedAt">When the reference was resolved.</param>
public sealed record TemplatePin(
    string TemplateId,
    string ResolvedVersion,
    string CommitSha,
    string PackageSha256,
    TemplateEngine Engine,
    string EngineVersion,
    string SchemaRef,
    DateTimeOffset ResolvedAt)
{
    /// <summary>The pin of <paramref name="record"/>, resolved at <paramref name="resolvedAt"/>.</summary>
    /// <param name="record">The record the reference resolved to.</param>
    /// <param name="resolvedAt">When the reference was resolved.</param>
    public static TemplatePin From(TemplateVersionRecord record, DateTimeOffset resolvedAt)
    {
        ArgumentNullException.ThrowIfNull(record);
        return new TemplatePin(
            record.TemplateId,
            record.Version,
            record.CommitSha,
            record.PackageSha256,
            record.Engine,
            record.EngineVersion,
            record.SchemaRef,
            resolvedAt);
    }
}
