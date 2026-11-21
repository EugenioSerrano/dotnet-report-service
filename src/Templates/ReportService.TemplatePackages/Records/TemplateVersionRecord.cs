using ReportService.Storage;
using ReportService.TemplatePackages.Manifest;

namespace ReportService.TemplatePackages.Records;

/// <summary>
/// One taken template version: the values the service fixed when it read the
/// version, and the only thing that makes the version usable. A record is
/// written once and never changed; the copy it names is immutable, so a later
/// read of the source can never alter what a job renders. A version taken from
/// the deployment's object store holds the publication descriptor's hash, the key
/// it was read from and the list of files the take verified; a version taken
/// before that change holds the commit and the tree it was read at and no
/// publication provenance. One record holds one of the two, never both and never
/// neither.
/// </summary>
/// <param name="TemplateId">The template's identifier, as the folder and the manifest name it.</param>
/// <param name="Version">The version, exactly as the manifest declares it (build metadata included).</param>
/// <param name="OwnerConsumerId">The consumer the version belongs to.</param>
/// <param name="Shared">Whether every consumer may use the version.</param>
/// <param name="ManifestVersion">The manifest format, <c>1.0</c>.</param>
/// <param name="Engine">The engine that renders the package.</param>
/// <param name="EngineVersion">The version of that engine the deployment renders with.</param>
/// <param name="SchemaRef">The package-relative path of the schema the records are checked against.</param>
/// <param name="CommitSha">The commit the whole read was pinned to; null for a version taken from the object store.</param>
/// <param name="SourceTreeSha">The git tree of the version's folder at that commit; null for a version taken from the object store.</param>
/// <param name="PackageSha256">The SHA-256 of the package's bytes, lower-case hexadecimal.</param>
/// <param name="PackageSize">The size of the package's bytes.</param>
/// <param name="FileCount">How many files the package holds.</param>
/// <param name="ObjectKey">The content-addressed key the package is stored under.</param>
/// <param name="Manifest">The typed view of the stored manifest.</param>
/// <param name="ManifestJson">The manifest document as JSON text, every member and every value the take read, with member order and spacing left to the storage.</param>
/// <param name="TakenAt">When the version was taken.</param>
/// <param name="SourceDescriptorSha256">The SHA-256 of the publication descriptor the take verified, lower-case hexadecimal; a version taken from the object store only.</param>
/// <param name="SourceDescriptorKey">Where the source kept the publication descriptor; a version taken from the object store only.</param>
/// <param name="SourceObjects">The files the take verified; a version taken from the object store only.</param>
public sealed record TemplateVersionRecord(
    string TemplateId,
    string Version,
    string OwnerConsumerId,
    bool Shared,
    string ManifestVersion,
    TemplateEngine Engine,
    string EngineVersion,
    string SchemaRef,
    string? CommitSha,
    string? SourceTreeSha,
    string PackageSha256,
    long PackageSize,
    int FileCount,
    ObjectKey ObjectKey,
    TemplateManifest Manifest,
    string ManifestJson,
    DateTimeOffset TakenAt,
    string? SourceDescriptorSha256 = null,
    string? SourceDescriptorKey = null,
    TemplateSourceObject[]? SourceObjects = null);
