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
using Temporalio.Exceptions;
using Temporalio.Testing;

namespace ReportService.Workers.Pdf.Tests.Rendering;

/// <summary>
/// The guard's two branches at the activity boundary: a composition that passed its 60 s
/// deadline records <c>guard-deadline</c> for the attempt and rethrows the code retryable, so
/// the attempt is counted against the block's three attempts; a composition that passed the
/// output-size cap writes no cause row and rethrows the code non-retryable, because no retry
/// makes the same block smaller. Neither swallows the code, and the template library keeps no
/// store dependency.
/// </summary>
public sealed class GuardCauseTests : IDisposable
{
    private const string JobId = "job-1";
    private const string OutputId = "output-1";

    private readonly string root = Path.Combine(Path.GetTempPath(), "guard-cause-" + Guid.NewGuid().ToString("N"));

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

    [Fact]
    public async Task ACompositionDeadlineWritesItsCauseAndRethrowsRetryable()
    {
        var storage = await StorageAsync();
        var package = await PackageAsync(storage.Store);
        var pin = Pin(package.Sha256);
        var jobs = Jobs(ReferenceOf(pin), storage);
        using var logs = new CapturingLogs();
        var renderer = new GuardingRenderer(TemplateGuardException.Timeout("The composed text of the block was not produced within 60 s."));
        var activity = Activity(jobs, new FakeVersionStore { Record = Record(pin.PackageSha256, package.Key, package.Size) }, storage.Store, renderer, logs);

        var failure = await Assert.ThrowsAsync<ApplicationFailureException>(
            () => new ActivityEnvironment().RunAsync(() => activity.RunAsync(new RenderPdfBlockInput(JobId, OutputId, 0))));

        Assert.Equal(TemplateErrorCodes.RenderTimeout, failure.ErrorType);
        Assert.False(failure.NonRetryable);
        var row = Assert.Single(jobs.Causes);
        Assert.Equal(OutputId, row.OutputId);
        Assert.Equal(0, row.BlockIndex);
        Assert.Equal(1, row.Attempt);
        Assert.Equal(AttemptCauseVocabulary.GuardDeadline, row.Cause);
        Assert.Null(jobs.Committed);
        Assert.Contains(logs.Lines, line => line.Contains("passed its deadline", StringComparison.Ordinal));
        Assert.DoesNotContain(logs.Lines, line => line.Contains("60 s.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ASizeRefusalWritesNoCauseAndRethrowsNonRetryable()
    {
        var storage = await StorageAsync();
        var package = await PackageAsync(storage.Store);
        var pin = Pin(package.Sha256);
        var jobs = Jobs(ReferenceOf(pin), storage);
        using var logs = new CapturingLogs();
        var renderer = new GuardingRenderer(TemplateGuardException.Quota("render-output", "The composed text of the block passed its bound."));
        var activity = Activity(jobs, new FakeVersionStore { Record = Record(pin.PackageSha256, package.Key, package.Size) }, storage.Store, renderer, logs);

        var failure = await Assert.ThrowsAsync<ApplicationFailureException>(
            () => new ActivityEnvironment().RunAsync(() => activity.RunAsync(new RenderPdfBlockInput(JobId, OutputId, 0))));

        Assert.Equal(TemplateErrorCodes.QuotaExceeded, failure.ErrorType);
        Assert.True(failure.NonRetryable);
        Assert.Empty(jobs.Causes);
        Assert.Null(jobs.Committed);
        Assert.Contains(logs.Lines, line => line.Contains("render-output", StringComparison.Ordinal));
    }

    private static FakeJobStore Jobs(TemplateReferenceRecord reference, (IObjectStore Store, BlockIndexEntry Block, BlockIndexEntry Index) storage) =>
        new()
        {
            Reference = reference,
            Partition = new PartitionRecord(JobId, 1, 1, 1, storage.Block.SizeBytes, storage.Index.Key, storage.Index.Sha256, storage.Index.SizeBytes, DateTimeOffset.UnixEpoch),
        };

    private RenderPdfBlockActivity Activity(
        FakeJobStore jobs,
        FakeVersionStore versions,
        IObjectStore store,
        IBlockRenderer renderer,
        CapturingLogs logs) =>
        new(
            jobs,
            versions,
            store,
            new TakenTemplatePackageReader(store),
            new PreparedTemplateCache(),
            TemplateEngineAdapters.Registry(),
            renderer,
            new PdfRenderingMetrics(MeterIdentity.ForHost(TelemetryDefaults.MeterName)),
            TimeProvider.System,
            logs.Factory,
            logs.Factory.CreateLogger<RenderPdfBlockActivity>(),
            Path.Combine(root, "scratch"));

    private static TemplatePin Pin(string packageSha256) =>
        new(
            "integrity-table",
            "1.0.0",
            new string('e', 40),
            null,
            null,
            packageSha256,
            TemplateEngine.Scriban,
            "7.5.1",
            "schema.json",
            DateTimeOffset.UnixEpoch);

    private static TemplateReferenceRecord ReferenceOf(TemplatePin pin) =>
        new(JobId, pin.TemplateId, TemplateReferenceForm.Exact) { RequestedVersion = pin.ResolvedVersion, Pin = pin };

    private static TemplateVersionRecord Record(string packageSha256, ObjectKey objectKey, long packageSize)
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

    private async Task<(IObjectStore Store, BlockIndexEntry Block, BlockIndexEntry Index)> StorageAsync()
    {
        var store = FilesystemObjectStore.Open(
            new FilesystemStorageOptions { Root = Path.Combine(root, "store"), CreateIfMissing = true },
            root,
            TimeProvider.System,
            NullLogger<FilesystemObjectStore>.Instance);

        var block = Encoding.UTF8.GetBytes(
            "{\"contractVersion\":\"1.0\",\"header\":{\"reportTitle\":\"Statement\"},\"items\":[{\"sequence\":\"1\",\"description\":\"one\",\"amount\":\"10.00\"}]}");
        var blockKey = ObjectKey.FromSegments("block", JobId, "0", "a");
        var blockWrite = await store.WriteAsync(blockKey, new MemoryStream(block), CancellationToken.None);

        var indexPage = BlockIndexPage.Serialize(new BlockIndexPage(1,
            [new BlockIndexEntry(0, blockKey.ToString(), blockWrite.Value!.Sha256, blockWrite.Value.Size, 1, 1, block.Length)]));
        var indexKey = ObjectKey.FromSegments("index", JobId, "0", "a");
        var indexWrite = await store.WriteAsync(indexKey, new MemoryStream(indexPage), CancellationToken.None);

        return (
            store,
            new BlockIndexEntry(0, blockKey.ToString(), blockWrite.Value.Sha256, blockWrite.Value.Size, 1, 1, block.Length),
            new BlockIndexEntry(0, indexKey.ToString(), indexWrite.Value!.Sha256, indexWrite.Value.Size, 1, 1, indexPage.Length));
    }

    private static async Task<TakenPackage> PackageAsync(IObjectStore store)
    {
        var folder = Path.Combine(RepositoryPaths.Root, "tests", "Support", "ReportService.Testing", "Templates", "integrity-table");
        using var buffer = new MemoryStream();
        using (var writer = new TarWriter(buffer, TarEntryFormat.Pax, leaveOpen: true))
        {
            foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            {
                writer.WriteEntry(file, Path.GetRelativePath(folder, file).Replace('\\', '/'));
            }
        }

        var bytes = buffer.ToArray();
        buffer.Position = 0;
        var sha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
        var key = ObjectKey.FromSegments("templates", "consumer", "integrity-table", "1.0.0", sha256);
        await store.WriteAsync(key, buffer, CancellationToken.None);
        return new TakenPackage(key, sha256, bytes.LongLength);
    }

    private sealed record TakenPackage(ObjectKey Key, string Sha256, long Size);

    /// <summary>The renderer a guard stops: it throws the typed refusal the template library raises.</summary>
    private sealed class GuardingRenderer(TemplateGuardException guard) : IBlockRenderer
    {
        public Task<BlockRenderOutcome> RenderAsync(BlockRenderRequest request, CancellationToken cancellationToken = default) =>
            throw guard;
    }
}
