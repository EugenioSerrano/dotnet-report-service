using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using ReportService.Storage;
using ReportService.TemplatePackages.Keys;
using ReportService.TemplatePackages.Manifest;
using ReportService.TemplatePackages.Packages;
using ReportService.TemplateSource.Manifest;
using ReportService.Testing.Fixtures;
using ReportService.Testing.TemplateSource;

namespace ReportService.Testing.VerificationLoss;

/// <summary>
/// The documents a refused print is evidenced with: four snapshots of the data
/// contract - three carrying a loss and one honest - and the two template
/// packages that print them. Every snapshot is a function of its identifier and
/// the values below, so generating twice gives byte-identical files and the same
/// identifier gives the same bytes on every machine; the sums file records the
/// bytes the cases were measured on, and the packages are the product-format
/// folders the rendering tier prints and the job cases take.
/// </summary>
public static class VerificationLossCorpus
{
    /// <summary>The package every job case prints.</summary>
    public const string Template = "verification-loss";

    /// <summary>The package's version.</summary>
    public const string Version = "1.0.0";

    /// <summary>The package only the rendering tier prints: one row keeps its fragment with its text omitted, and the document ends on a blank page.</summary>
    public const string SupplementaryTemplate = "verification-loss-supplementary";

    /// <summary>The consumer the seeded versions belong to, as the test doubles record it.</summary>
    public const string Consumer = "dev-consumer";

    /// <summary>The file the SHA-256 of every snapshot is recorded in.</summary>
    public const string SumsFileName = "SHA256SUMS";

    /// <summary>The identifier of the document whose text loses one character in print.</summary>
    public const string TextLoss = "VL-TEXT";

    /// <summary>The identifier of the document whose row prints no text at all.</summary>
    public const string RowLoss = "VL-ROW";

    /// <summary>The identifier of the document that carries both defects.</summary>
    public const string ManyLoss = "VL-MANY";

    /// <summary>The identifier of the honest document.</summary>
    public const string Honest = "VL-OK";

    /// <summary>
    /// The value the text-loss document anchors: the soft hyphen is a character
    /// the browser does not paint, so the composed text holds it and the printed
    /// text does not.
    /// </summary>
    public const string PlantedTextLoss = "AB\u00ADCD";

    /// <summary>The header value the row-loss document carries, planted so a diagnostic that quotes it is caught.</summary>
    public const string PlantedHeader = "PLANTED-HDR-VLROW";

    /// <summary>Every snapshot identifier, in the order the corpus declares them.</summary>
    public static IReadOnlyList<string> SnapshotIds { get; } = [TextLoss, RowLoss, ManyLoss, Honest];

    private static readonly JsonSerializerOptions Writing = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly UTF8Encoding Utf8WithoutBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>The directory the committed package folders live in.</summary>
    public static string PackagesDirectory => Path.Combine(
        RepositoryPaths.Root, "tests", "ReportService.Testing", "VerificationLoss", "packages");

    /// <summary>The folder of one package, as it is committed.</summary>
    /// <param name="template">The package's identifier.</param>
    public static string PackageFolder(string template) => Path.Combine(PackagesDirectory, template);

    /// <summary>The bytes of <paramref name="id"/>'s snapshot, generated from its values.</summary>
    /// <param name="id">One of <see cref="SnapshotIds"/>.</param>
    public static SnapshotFixture Snapshot(string id)
    {
        var (title, customer, descriptions) = id switch
        {
            TextLoss => (
                "Verification-loss text report",
                "C-VLTEXT",
                new[] { "Annual inspection", PlantedTextLoss, "Monthly audit" }),
            RowLoss => (
                PlantedHeader,
                "C-VLROW",
                new[] { "Annual inspection", string.Empty, "Monthly audit" }),
            ManyLoss => (
                "Verification-loss many report",
                "C-VLMANY",
                new[] { PlantedTextLoss, "Quarterly review", string.Empty }),
            Honest => (
                "Verification-loss honest report",
                "C-VLOOK",
                new[] { "Annual inspection", "Quarterly review", "Monthly audit" }),
            _ => throw new ArgumentException($"Unknown snapshot id '{id}'.", nameof(id)),
        };

        var items = new JsonArray();
        for (var index = 0; index < descriptions.Length; index++)
        {
            items.Add(new JsonObject
            {
                ["sequence"] = index + 1,
                ["description"] = descriptions[index],
            });
        }

        var document = new JsonObject
        {
            ["contractVersion"] = "1.0",
            ["header"] = new JsonObject
            {
                ["reportTitle"] = title,
                ["customer"] = new JsonObject { ["id"] = customer },
            },
            ["items"] = items,
        };

        var bytes = Utf8WithoutBom.GetBytes(document.ToJsonString(Writing));
        return new SnapshotFixture(id, bytes, Convert.ToHexStringLower(SHA256.HashData(bytes)), descriptions.Length);
    }

    /// <summary>Every snapshot of the corpus, in the declared order.</summary>
    public static IReadOnlyList<SnapshotFixture> Snapshots() => [.. SnapshotIds.Select(Snapshot)];

    /// <summary>
    /// Writes every snapshot and the sums file into <paramref name="directory"/>:
    /// the same folder a case seeds, and the file the corpus is frozen by.
    /// </summary>
    /// <param name="directory">The directory to write into.</param>
    public static void WriteSnapshots(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        Directory.CreateDirectory(directory);
        var sums = new StringBuilder();
        foreach (var snapshot in Snapshots())
        {
            snapshot.WriteTo(directory);
            sums.Append(snapshot.Sha256).Append("  ").Append(snapshot.FileName).Append('\n');
        }

        File.WriteAllText(Path.Combine(directory, SumsFileName), sums.ToString(), Utf8WithoutBom);
    }

    /// <summary>
    /// The files of one package: the committed folder's files, with the sample
    /// generated from the honest snapshot so the package and its document cannot
    /// describe two different payloads.
    /// </summary>
    /// <param name="template">The package's identifier.</param>
    public static IReadOnlyDictionary<string, byte[]> PackageFiles(string template)
    {
        var folder = PackageFolder(template);
        if (!Directory.Exists(folder))
        {
            throw new DirectoryNotFoundException($"The verification-loss package folder {folder} is missing.");
        }

        var files = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .ToDictionary(
                file => Path.GetRelativePath(folder, file).Replace('\\', '/'),
                File.ReadAllBytes,
                StringComparer.Ordinal);
        files["sample.json"] = Snapshot(Honest).Bytes;
        return files;
    }

    /// <summary>
    /// Registers both packages with the template-source test double the job cases
    /// seed: the package is archived with the product's own writer and written to
    /// the store, the record is read from the package's own manifest, and the take
    /// answers it under the package's identifier and version.
    /// </summary>
    /// <param name="objects">The store the taken copy is written to.</param>
    /// <param name="take">The take the job cases read their version from.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>The recorded versions, by package identifier, and the snapshot identifiers the cases seed.</returns>
    public static async Task<CorpusSeeding> SeedAsync(
        IObjectStore objects,
        InMemoryTemplateTake take,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(objects);
        ArgumentNullException.ThrowIfNull(take);

        var records = new Dictionary<string, ReportService.TemplatePackages.Records.TemplateVersionRecord>(StringComparer.Ordinal);
        foreach (var template in new[] { Template, SupplementaryTemplate })
        {
            var files = PackageFiles(template);
            var sources = files
                .OrderBy(file => file.Key, StringComparer.Ordinal)
                .Select(file => new PackageFileSource(
                    file.Key,
                    _ => Task.FromResult<Stream>(new MemoryStream(file.Value, writable: false))))
                .ToList();

            using var buffer = new MemoryStream();
            await TemplatePackageArchive.WriteAsync(sources, buffer, cancellationToken).ConfigureAwait(false);
            var bytes = buffer.ToArray();
            var packageSha256 = Convert.ToHexStringLower(SHA256.HashData(bytes));
            var key = TemplatePackageKeys.ForVersion(Consumer, template, Version, packageSha256);
            using (var content = new MemoryStream(bytes, writable: false))
            {
                var written = await objects.WriteAsync(key, content, cancellationToken).ConfigureAwait(false);
                if (!written.Succeeded && written.Outcome != StorageOutcome.AlreadyExists)
                {
                    throw new InvalidOperationException(
                        $"The {template} package had to be written: the store answered {written.Outcome}.");
                }
            }

            // The record carries the manifest the archived files themselves declare,
            // so the taken copy and its record cannot describe two different packages.
            var read = new TemplateManifestReader().Read(new MemoryStream(files["template.yaml"], writable: false));
            if (!read.Succeeded)
            {
                throw new InvalidOperationException($"The {template} manifest does not read: {read.Reason}");
            }

            var manifest = read.Manifest!;
            var record = TemplateVersionRecords.Sample(
                    template,
                    Version,
                    manifest.Owner ?? Consumer,
                    shared: manifest.Shared,
                    schemaRef: manifest.DataContract!.Schema!,
                    packageSha256: packageSha256) with
                {
                    ManifestVersion = manifest.ManifestVersion!,
                    Engine = manifest.Engine!.Value,
                    EngineVersion = TemplateEngineCatalog.VersionOf(manifest.Engine.Value),
                    PackageSize = bytes.LongLength,
                    FileCount = files.Count,
                    ObjectKey = key,
                    Manifest = manifest,
                    ManifestJson = read.CanonicalJson!,
                };

            take.SeedTaken(template, Version, record);
            records[template] = record;
        }

        return new CorpusSeeding(records, SnapshotIds);
    }
}

/// <summary>The corpus as a class seeds it: the two taken versions and the snapshot identifiers.</summary>
/// <param name="Versions">The recorded versions, by package identifier.</param>
/// <param name="Snapshots">The snapshot identifiers a job case writes for its upload.</param>
public sealed record CorpusSeeding(
    IReadOnlyDictionary<string, ReportService.TemplatePackages.Records.TemplateVersionRecord> Versions,
    IReadOnlyList<string> Snapshots);
