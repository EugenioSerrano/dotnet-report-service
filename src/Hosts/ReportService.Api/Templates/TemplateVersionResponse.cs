using System.Globalization;
using ReportService.TemplatePackages.Manifest;
using ReportService.TemplatePackages.Records;

namespace ReportService.Api.Templates;

/// <summary>
/// What the owner's read answers: everything a caller needs to identify the version
/// it was given and to know what was fixed when it was taken, and nothing about how
/// or where the service keeps the copy. Every member is always present; the package
/// hash and the publication descriptor's hash name their own algorithm so the value
/// can never be taken for another hash, the engine is the stored name of the engine
/// that renders the package, and the take's instant is this API's one timestamp
/// format. The two publication members are null for a version taken before the
/// source became the deployment's object store; no answer carries a commit SHA.
/// </summary>
/// <param name="TemplateId">The template's identifier.</param>
/// <param name="Version">The version, exactly as the manifest declares it.</param>
/// <param name="SourceDescriptorHash">The hash of the publication descriptor the take verified, prefixed with its algorithm; null for a version taken before the object-store source.</param>
/// <param name="Objects">The files the take verified; null for a version taken before the object-store source.</param>
/// <param name="PackageHash">The hash of the package's bytes, prefixed with its algorithm.</param>
/// <param name="Engine">The engine that renders the package.</param>
/// <param name="EngineVersion">The version of that engine the deployment renders with.</param>
/// <param name="Owner">The consumer the version belongs to.</param>
/// <param name="Shared">Whether every consumer may use the version.</param>
/// <param name="TakenAt">When the version was taken.</param>
public sealed record TemplateVersionResponse(
    string TemplateId,
    string Version,
    string? SourceDescriptorHash,
    TemplateSourceObjectResponse[]? Objects,
    string PackageHash,
    string Engine,
    string EngineVersion,
    string Owner,
    bool Shared,
    string TakenAt)
{
    /// <summary>The prefix every package hash carries, naming the algorithm it was computed with.</summary>
    public const string PackageHashPrefix = "sha256:";

    /// <summary>The format of a timestamp on this API's wire: UTC, three fractional digits, <c>Z</c>.</summary>
    public const string TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";

    /// <summary>Projects the record of a taken version onto the answer.</summary>
    /// <param name="record">The record to project.</param>
    public static TemplateVersionResponse From(TemplateVersionRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return new TemplateVersionResponse(
            record.TemplateId,
            record.Version,
            record.SourceDescriptorSha256 is null ? null : PackageHashPrefix + record.SourceDescriptorSha256,
            record.SourceObjects?.Select(
                item => new TemplateSourceObjectResponse(item.Path, item.Size, item.Sha256)).ToArray(),
            PackageHashPrefix + record.PackageSha256,
            TemplateEngineNames.ToStoredText(record.Engine),
            record.EngineVersion,
            record.OwnerConsumerId,
            record.Shared,
            FormatTimestamp(record.TakenAt));
    }

    /// <summary>Formats <paramref name="value"/> as a timestamp of this API's wire.</summary>
    /// <param name="value">The value to format.</param>
    public static string FormatTimestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString(TimestampFormat, CultureInfo.InvariantCulture);
}

/// <summary>One file of a version's verified package, as the metadata answer names it.</summary>
/// <param name="Path">The package-relative path, with <c>/</c> separators.</param>
/// <param name="Size">The size of the file's bytes.</param>
/// <param name="Sha256">The SHA-256 of the file's bytes, lower-case hexadecimal.</param>
public sealed record TemplateSourceObjectResponse(string Path, long Size, string Sha256);
