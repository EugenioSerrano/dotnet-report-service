using System.Formats.Tar;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ReportService.Data;
using ReportService.Jobs;
using ReportService.Rendering;
using ReportService.ServiceDefaults;
using ReportService.Storage;
using ReportService.Storage.Filesystem;
using ReportService.TemplatePackages.Manifest;
using ReportService.TemplatePackages.Packages;
using ReportService.TemplatePackages.Records;
using ReportService.Templates;
using ReportService.Testing;
using ReportService.Workers.Pdf.Rendering;

namespace ReportService.Workers.Pdf.Tests.Rendering;

/// <summary>
/// The render step over the doubles the refusal cases need: one job, one output,
/// one block and one index page written to a filesystem store, one taken package,
/// and the step built from them. The cases that need a store of their own wrap the
/// one this rig creates; the block and the index are written before the step runs,
/// the way the data step leaves them.
/// </summary>
internal sealed class RenderStepHarness : IDisposable
{
    /// <summary>The job every case renders.</summary>
    public const string JobId = "job-1";

    /// <summary>The output every case renders.</summary>
    public const string OutputId = "output-1";

    private const string DefaultBlock =
        "{\"contractVersion\":\"1.0\",\"header\":{\"reportTitle\":\"Statement\",\"customer\":{\"id\":\"C-1\"}},\"items\":[{\"sequence\":1,\"description\":\"one\",\"amount\":\"10.00\"}]}";

    private readonly string root = Path.Combine(Path.GetTempPath(), "render-step-" + Guid.NewGuid().ToString("N"));

    /// <summary>The directory the case's store and scratch folders live in.</summary>
    public string Root => root;

    /// <summary>The step, over the stores, the renderer and the storage the case gives it.</summary>
    /// <param name="jobs">The job store the step reads.</param>
    /// <param name="versions">The version store, or a store with no record.</param>
    /// <param name="renderer">The renderer, or the one that answers a verified document.</param>
    /// <param name="store">The storage, or the filesystem provider under the case's root.</param>
    /// <param name="metrics">The metrics, or the product meter's.</param>
    /// <param name="loggers">The loggers, or none.</param>
    public RenderPdfBlockActivity Activity(
        FakeJobStore jobs,
        FakeVersionStore? versions = null,
        IBlockRenderer? renderer = null,
        IObjectStore? store = null,
        PdfRenderingMetrics? metrics = null,
        ILoggerFactory? loggers = null)
    {
        ArgumentNullException.ThrowIfNull(jobs);
        store ??= FilesystemObjectStore.Open(
            new FilesystemStorageOptions { Root = Path.Combine(root, "store"), CreateIfMissing = true },
            root,
            TimeProvider.System,
            NullLogger<FilesystemObjectStore>.Instance);

        return new RenderPdfBlockActivity(
            jobs,
            versions ?? new FakeVersionStore(),
            store,
            new TakenTemplatePackageReader(store),
            new PreparedTemplateCache(),
            TemplateEngineAdapters.Registry(),
            renderer ?? new FakeRenderer(),
            metrics ?? new PdfRenderingMetrics(MeterIdentity.ForHost(TelemetryDefaults.MeterName)),
            TimeProvider.System,
            loggers ?? NullLoggerFactory.Instance,
            loggers?.CreateLogger<RenderPdfBlockActivity>() ?? NullLogger<RenderPdfBlockActivity>.Instance,
            Path.Combine(root, "scratch"));
    }

    /// <summary>The filesystem provider under the case's root.</summary>
    public IObjectStore Store() => FilesystemObjectStore.Open(
        new FilesystemStorageOptions { Root = Path.Combine(root, "store"), CreateIfMissing = true },
        root,
        TimeProvider.System,
        NullLogger<FilesystemObjectStore>.Instance);

    /// <summary>The pin one taken version of the package is fixed to.</summary>
    /// <param name="packageSha256">The package's hash, or a syntactically valid one.</param>
    /// <param name="engine">The engine the pin records.</param>
    public static TemplatePin Pin(string? packageSha256 = null, TemplateEngine engine = TemplateEngine.Scriban) =>
        new(
            "integrity-table",
            "1.0.0",
            new string('e', 40),
            null,
            null,
            packageSha256 ?? new string('f', 64),
            engine,
            "7.5.1",
            "schema.json",
            DateTimeOffset.UnixEpoch);

    /// <summary>The job's recorded reference to the pinned version.</summary>
    /// <param name="pin">The pin the job was fixed to.</param>
    public static TemplateReferenceRecord ReferenceOf(TemplatePin pin) =>
        new(JobId, pin.TemplateId, TemplateReferenceForm.Exact) { RequestedVersion = pin.ResolvedVersion, Pin = pin };

    /// <summary>The taken version's record, naming the package and its manifest.</summary>
    /// <param name="packageSha256">The package's hash.</param>
    /// <param name="objectKey">The key the package is stored under.</param>
    /// <param name="packageSize">The package's size.</param>
    public static TemplateVersionRecord Record(string packageSha256, ObjectKey objectKey, long packageSize)
    {
        var manifest = new TemplateManifest
        {
            ManifestVersion = "1.0",
            Id = "integrity-table",
            Version = "1.0.0",
            Engine = TemplateEngine.Scriban,
            DeclaredEngineVersion = "7.5.1",
            Outputs = new TemplateOutputs
            {
                Html = new TemplateHtmlOutput
                {
                    Parts = new TemplateParts
                    {
                        DocumentStart = "html/document-start.html.sbn",
                        Row = "html/row.html.sbn",
                        DocumentEnd = "html/document-end.html.sbn",
                    },
                    Styles = ["html/styles.css"],
                },
                Pdf = new TemplatePdfOutput
                {
                    Paper = "A4",
                    Orientation = "portrait",
                    PageTotals = new TemplatePageTotals { Enabled = false },
                    MarginMm = new TemplateMargins { Top = 15, Right = 12, Bottom = 18, Left = 12 },
                    Verification = new TemplatePdfVerification
                    {
                        Anchors = new TemplatePdfAnchors { Header = ["$.reportTitle"], Item = ["$.description"] },
                    },
                },
            },
            LocaleDefaults = new TemplateLocaleDefaults { Culture = "es-AR", TimeZone = "America/Argentina/Buenos_Aires" },
        };

        return new TemplateVersionRecord(
            "integrity-table",
            "1.0.0",
            "consumer",
            Shared: true,
            "1.0",
            TemplateEngine.Scriban,
            "7.5.1",
            "schema.json",
            new string('e', 40),
            new string('e', 40),
            packageSha256,
            packageSize,
            FileCount: 4,
            objectKey,
            manifest,
            "{}",
            DateTimeOffset.UnixEpoch);
    }

    /// <summary>The block and its index page as the data step leaves them, written to the case's store.</summary>
    /// <param name="block">The block document, or the one-record block the cases share.</param>
    public async Task<RenderStorage> StorageAsync(byte[]? block = null)
    {
        var store = Store();
        var bytes = block ?? Encoding.UTF8.GetBytes(DefaultBlock);
        var blockKey = ObjectKey.FromSegments("block", JobId, "0", "a");
        var blockWrite = await store.WriteAsync(blockKey, new MemoryStream(bytes), CancellationToken.None);
        var indexPage = BlockIndexPage.Serialize(new BlockIndexPage(1,
            [new BlockIndexEntry(0, blockKey.ToString(), blockWrite.Value!.Sha256, blockWrite.Value.Size, 1, 1, bytes.Length)]));
        var indexKey = ObjectKey.FromSegments("index", JobId, "0", "a");
        var indexWrite = await store.WriteAsync(indexKey, new MemoryStream(indexPage), CancellationToken.None);

        return new RenderStorage(
            store,
            new BlockIndexEntry(0, blockKey.ToString(), blockWrite.Value.Sha256, blockWrite.Value.Size, 1, 1, bytes.Length),
            new BlockIndexEntry(0, indexKey.ToString(), indexWrite.Value!.Sha256, indexWrite.Value.Size, 1, 1, indexPage.Length));
    }

    /// <summary>The delivered test package as a tar, written to the store under a content-addressed key.</summary>
    /// <param name="store">The store the package is written to.</param>
    public static async Task<TakenPackage> PackageAsync(IObjectStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        var folder = Path.Combine(RepositoryPaths.Root, "tests", "Support", "ReportService.Testing", "Templates", "integrity-table");
        using var buffer = new MemoryStream();
        using (var writer = new TarWriter(buffer, TarEntryFormat.Pax, leaveOpen: true))
        {
            foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            {
                var relative = Path.GetRelativePath(folder, file).Replace('\\', '/');
                writer.WriteEntry(file, relative);
            }
        }

        var bytes = buffer.ToArray();
        buffer.Position = 0;
        var sha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
        var key = ObjectKey.FromSegments("templates", "consumer", "integrity-table", "1.0.0", sha256);
        await store.WriteAsync(key, buffer, CancellationToken.None);
        return new TakenPackage(key, sha256, bytes.LongLength);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

/// <summary>The block and its index page as the data step leaves them.</summary>
/// <param name="Store">The store the step reads them through.</param>
/// <param name="Block">The block's entry in the index page.</param>
/// <param name="Index">The index page's entry.</param>
internal sealed record RenderStorage(IObjectStore Store, BlockIndexEntry Block, BlockIndexEntry Index);

/// <summary>The taken package as a tar: where it is, its hash and its size.</summary>
/// <param name="Key">The key it is stored under.</param>
/// <param name="Sha256">Its hash, lower-case hexadecimal.</param>
/// <param name="Size">Its size in bytes.</param>
internal sealed record TakenPackage(ObjectKey Key, string Sha256, long Size);
