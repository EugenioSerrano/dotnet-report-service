using System.Text.Json.Nodes;
using ReportService.TemplatePackages.Keys;
using ReportService.TemplatePackages.Manifest;
using ReportService.TemplatePackages.Records;

namespace ReportService.Testing;

/// <summary>
/// The record of one taken version, built with the values a take would have
/// fixed, for the cases that need a version without reading a source: the same
/// identifiers, hashes, engine and manifest a take of the pilot package leaves,
/// with every member a case cares about replaceable.
/// </summary>
public static class TemplateVersionRecords
{
    /// <summary>The package hash every case of this file uses, a syntactically valid SHA-256.</summary>
    public const string PackageSha256 = "5e88e6f1b5f8d8f0eaa2b2a4b6c8d0e2f4a6b8c0d2e4f6a8b0c2d4e6f8a0b2c4";

    /// <summary>The commit a read is pinned to, a syntactically valid git SHA-1.</summary>
    public const string CommitSha = "a1b2c3d4e5f60718293a4b5c6d7e8f9012345678";

    /// <summary>The git tree of the version's folder at that commit.</summary>
    public const string SourceTreeSha = "b2c3d4e5f60718293a4b5c6d7e8f90123456789a";

    /// <summary>Builds the record of a taken version, with the pilot's values unless a case replaces them.</summary>
    /// <param name="templateId">The template's identifier.</param>
    /// <param name="version">The version.</param>
    /// <param name="owner">The consumer that owns it.</param>
    /// <param name="shared">Whether every consumer may use it.</param>
    /// <param name="engine">The engine that renders it.</param>
    /// <param name="engineVersion">The version of that engine.</param>
    /// <param name="schemaRef">The package-relative path of the schema.</param>
    /// <param name="packageSha256">The hash of the package's bytes.</param>
    /// <param name="takenAt">When the version was taken.</param>
    public static TemplateVersionRecord Sample(
        string templateId = "invoice-energy",
        string version = "1.1.0",
        string owner = "billing-system",
        bool shared = false,
        TemplateEngine engine = TemplateEngine.Scriban,
        string engineVersion = "7.5.1",
        string schemaRef = "schema/data.schema.json",
        string packageSha256 = PackageSha256,
        DateTimeOffset? takenAt = null)
    {
        var manifest = new TemplateManifest
        {
            ManifestVersion = "1.0",
            Id = templateId,
            Version = version,
            Owner = owner,
            Shared = shared,
            Engine = engine,
            DeclaredEngineVersion = engineVersion,
            DataContract = new TemplateDataContract
            {
                Profile = "header-items-v1",
                Version = "1.0",
                Schema = schemaRef,
            },
        };

        var document = new JsonObject
        {
            ["manifestVersion"] = "1.0",
            ["id"] = templateId,
            ["version"] = version,
            ["owner"] = owner,
            ["shared"] = shared,
            ["engine"] = TemplateEngineNames.ToManifestName(engine),
            ["engineVersion"] = engineVersion,
            ["dataContract"] = new JsonObject
            {
                ["profile"] = "header-items-v1",
                ["version"] = "1.0",
                ["schema"] = schemaRef,
            },
        };

        return new TemplateVersionRecord(
            templateId,
            version,
            owner,
            shared,
            "1.0",
            engine,
            engineVersion,
            schemaRef,
            CommitSha,
            SourceTreeSha,
            packageSha256,
            PackageSize: 4_096,
            FileCount: 4,
            ObjectKey: TemplatePackageKeys.ForVersion(owner, templateId, version, packageSha256),
            Manifest: manifest,
            ManifestJson: TemplateManifestJson.Write(document),
            TakenAt: takenAt ?? new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero));
    }
}
