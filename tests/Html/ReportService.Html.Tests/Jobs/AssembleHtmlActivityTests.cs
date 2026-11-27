using System.Formats.Tar;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using ReportService.Data;
using ReportService.Jobs;
using ReportService.Storage;
using ReportService.Storage.Filesystem;
using ReportService.TemplatePackages.Manifest;
using ReportService.TemplatePackages.Packages;
using ReportService.TemplatePackages.Records;
using ReportService.Templates;
using ReportService.Testing;
using ReportService.Uploads;
using ReportService.Workers.Orchestration.Jobs;
using Temporalio.Exceptions;
using Temporalio.Testing;

namespace ReportService.Workers.Orchestration.Tests.Jobs;

/// <summary>
/// The HTML assembly step (US-008.BOLT-002, SPEC-261125-1224 §8.1, Phase A) run as a plain
/// activity through <see cref="ActivityEnvironment"/> over doubles — an in-memory job/version
/// store, a filesystem object store holding the taken package, the index and the block, and the
/// real composition + <see cref="HtmlOutputAssembler"/> — with NO Temporal worker. The four cases
/// are the SPEC §8.1 scope-lock for this file; every oracle is derived from the approved intent
/// (US-008 AC-4/AC-5, ADR-047 §3.5/§3.6), never from reading <see cref="AssembleHtmlActivity"/>:
/// <list type="number">
/// <item>a produced result writes one attempt-scoped object and returns its key/sha/size (happy
/// path) — a self-contained HTML-capable package (<c>pilot-simple</c>, relative packaged CSS);</item>
/// <item>an assembler refusal — a package whose HTML references an out-of-package asset
/// (<c>integrity-table</c>'s remote stylesheet, ADR-047 self-containment) — writes nothing: no
/// partial object under the output namespace (ADR-047 §3.5 discard-on-refusal);</item>
/// <item>a package that declares no <c>outputs.html</c> part is refused with
/// <c>UNSUPPORTED_CAPABILITY</c> BEFORE the partition is read or anything is composed/written
/// (AC-5);</item>
/// <item>an empty snapshot assembles one header-only <c>.html</c> and succeeds (ADR-047 §3.6 —
/// the empty-block / empty-report intent).</item>
/// </list>
/// </summary>
public sealed class AssembleHtmlActivityTests : IDisposable
{
    private const string JobId = "job-0123456789abcdef0123456789abcdef";
    private const string OutputId = "output-0123456789abcdef0123456789abcdef";

    // A self-contained HTML-capable package: the committed pilot sample declares outputs.html and a
    // packaged relative stylesheet, so the real assembler produces one self-contained .html.
    private static readonly string SelfContainedTemplate =
        Path.Combine(RepositoryPaths.Root, "samples", "templates", "pilot-simple");

    // A package whose HTML references an out-of-package remote stylesheet: the real assembler
    // refuses it per ADR-047 self-containment (a genuine definitive HTML failure, no test seam).
    private static readonly string OutOfPackageReferenceTemplate =
        Path.Combine(RepositoryPaths.Root, "tests", "Support", "ReportService.Testing", "Templates", "integrity-table");

    private readonly string _root = Path.Combine(Path.GetTempPath(), "assemble-html-" + Guid.NewGuid().ToString("N"));
    private bool _disposed;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temporary directory is not a test failure.
        }
    }

    [Fact]
    public async Task AProducedResultWritesOneAttemptScopedObjectAndReturnsItsIdentity()
    {
        var store = Store();
        var package = await PackageAsync(store, SelfContainedTemplate);
        var pin = Pin(package.Sha256);
        var jobs = new HtmlAssemblyJobStore
        {
            Reference = ReferenceOf(pin),
            Partition = await StorageAsync(store, OneItemBlock()),
        };
        var activity = Activity(store, jobs, Versions(Record(pin.PackageSha256, package.Key, package.Size)));

        var result = await RunAsync(activity);

        // AC-4 (happy path): exactly one attempt-scoped object under the output namespace, keyed by
        // this job and output, with a hex-64 sha and a non-empty size.
        Assert.StartsWith("outputs/", result.ObjectKey, StringComparison.Ordinal);
        Assert.Contains(JobId, result.ObjectKey, StringComparison.Ordinal);
        Assert.Contains(OutputId, result.ObjectKey, StringComparison.Ordinal);
        Assert.Matches("^[0-9a-f]{64}$", result.Sha256);
        Assert.True(result.SizeBytes > 0, "a produced HTML output has a non-empty body");

        // The object was really written and is one complete, self-contained document that carries
        // the header and the composed row — the records reached the output.
        var html = await ReadObjectAsync(store, result);
        Assert.Contains("<!DOCTYPE html>", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("</html>", html, StringComparison.Ordinal);
        Assert.Contains("Monthly Statement", html, StringComparison.Ordinal);
        Assert.Contains("Widget", html, StringComparison.Ordinal);
        Assert.True(jobs.PartitionAsked, "a produced result reads the partition to compose the block");
    }

    [Fact]
    public async Task AnAssemblerRefusalPublishesNothing()
    {
        var store = Store();
        // The out-of-package-reference package: its documentStart links a remote stylesheet, which
        // the real assembler refuses (ADR-047 self-containment) with IsProduced == false.
        var package = await PackageAsync(store, OutOfPackageReferenceTemplate);
        var pin = Pin(package.Sha256);
        var jobs = new HtmlAssemblyJobStore
        {
            Reference = ReferenceOf(pin),
            Partition = await StorageAsync(store, OneItemBlock()),
        };
        var activity = Activity(store, jobs, Versions(Record(pin.PackageSha256, package.Key, package.Size)));

        var failure = await Assert.ThrowsAsync<ApplicationFailureException>(() => RunAsync(activity));

        // A definitive HTML failure: ends the output non-retryably and writes no partial object
        // under the output namespace (ADR-047 §3.5 discard-on-refusal — nothing is published).
        Assert.True(failure.NonRetryable);
        Assert.False(
            HasOutputObject(store),
            "a refused assembly must leave no object under the output namespace");
    }

    [Fact]
    public async Task APackageWithNoHtmlPartIsRefusedBeforeComposingOrWriting()
    {
        var store = Store();
        var package = await PackageAsync(store, SelfContainedTemplate);
        var pin = Pin(package.Sha256);
        var record = Record(pin.PackageSha256, package.Key, package.Size);
        var withoutHtml = record with
        {
            Manifest = record.Manifest with
            {
                Outputs = record.Manifest.Outputs! with { Html = null },
            },
        };
        var jobs = new HtmlAssemblyJobStore { Reference = ReferenceOf(pin) };
        var activity = Activity(store, jobs, Versions(withoutHtml));

        var failure = await Assert.ThrowsAsync<ApplicationFailureException>(() => RunAsync(activity));

        // AC-5: a package that declares no HTML-capable template is refused with the stable
        // UNSUPPORTED_CAPABILITY code, non-retryably, before the partition is ever read and before
        // anything is composed or written.
        Assert.Equal("UNSUPPORTED_CAPABILITY", failure.ErrorType);
        Assert.True(failure.NonRetryable);
        Assert.False(jobs.PartitionAsked, "the capability refusal happens before the partition is read");
        Assert.False(HasOutputObject(store), "nothing is written when the package cannot produce HTML");
    }

    [Fact]
    public async Task AnEmptySnapshotAssemblesAHeaderOnlyHtmlFile()
    {
        var store = Store();
        var package = await PackageAsync(store, SelfContainedTemplate);
        var pin = Pin(package.Sha256);
        var jobs = new HtmlAssemblyJobStore
        {
            Reference = ReferenceOf(pin),
            Partition = await StorageAsync(store, EmptyBlock()),
        };
        var activity = Activity(store, jobs, Versions(Record(pin.PackageSha256, package.Key, package.Size)));

        var result = await RunAsync(activity);

        // ADR-047 §3.6: an empty snapshot still produces one complete, self-contained .html — the
        // header and the document frame — with no data row.
        Assert.StartsWith("outputs/", result.ObjectKey, StringComparison.Ordinal);
        Assert.True(result.SizeBytes > 0, "a header-only HTML file still has a body");

        var html = await ReadObjectAsync(store, result);
        Assert.Contains("<!DOCTYPE html>", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("</html>", html, StringComparison.Ordinal);
        Assert.Contains("Monthly Statement", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Widget", html, StringComparison.Ordinal);
    }

    private static Task<AssembleHtmlStepResult> RunAsync(AssembleHtmlActivity activity)
    {
        var environment = new ActivityEnvironment
        {
            Info = ActivityEnvironment.DefaultInfo with
            {
                WorkflowId = $"report-job/{JobId}/output/html",
                WorkflowRunId = "run-0123456789abcdef",
                ActivityId = "1",
                ActivityType = HtmlOutputStepContracts.AssembleHtml,
                Attempt = 1,
                TaskQueue = "report-html",
            },
            Heartbeater = _ => { },
        };
        return environment.RunAsync(() => activity.RunAsync(new AssembleHtmlStepInput(JobId, OutputId)));
    }

    private AssembleHtmlActivity Activity(IObjectStore store, IJobStore jobs, ITemplateVersionStore versions) =>
        new(
            jobs,
            versions,
            store,
            new TakenTemplatePackageReader(store),
            new PreparedTemplateCache(),
            TemplateEngineAdapters.Registry(),
            TimeProvider.System,
            NullLoggerFactory.Instance,
            NullLogger<AssembleHtmlActivity>.Instance)
        {
            ScratchRoot = Path.Combine(_root, "scratch"),
        };

    private IObjectStore Store() => FilesystemObjectStore.Open(
        new FilesystemStorageOptions { Root = Path.Combine(_root, "store"), CreateIfMissing = true },
        _root,
        TimeProvider.System,
        NullLogger<FilesystemObjectStore>.Instance);

    private static HtmlAssemblyVersionStore Versions(TemplateVersionRecord record) => new(record);

    private bool HasOutputObject(IObjectStore store) =>
        Directory.Exists(Path.Combine(_root, "store", "outputs"))
        && Directory.EnumerateFiles(Path.Combine(_root, "store", "outputs"), "*", SearchOption.AllDirectories).Any();

    private static async Task<string> ReadObjectAsync(IObjectStore store, AssembleHtmlStepResult result)
    {
        var read = await store.OpenReadAsync(ObjectKey.Create(result.ObjectKey), result.SizeBytes, result.Sha256, CancellationToken.None);
        Assert.True(read.Succeeded, "the produced object is readable at the key it returned");
        await using (read.Value!.ConfigureAwait(false))
        {
            using var buffer = new MemoryStream();
            await read.Value!.Content.CopyToAsync(buffer, CancellationToken.None);
            return Encoding.UTF8.GetString(buffer.ToArray());
        }
    }

    // The index and the block as the data step leaves them: one block of the given content, written
    // to the store, with the partition record pointing at the index page.
    private async Task<PartitionRecord> StorageAsync(IObjectStore store, byte[] block)
    {
        var blockKey = ObjectKey.FromSegments("block", JobId, "0", "a");
        var blockWrite = await store.WriteAsync(blockKey, new MemoryStream(block), CancellationToken.None);

        var indexPage = BlockIndexPage.Serialize(new BlockIndexPage(1,
            [new BlockIndexEntry(0, blockKey.ToString(), blockWrite.Value!.Sha256, blockWrite.Value.Size, 1, 1, block.Length)]));
        var indexKey = ObjectKey.FromSegments("index", JobId, "0", "a");
        var indexWrite = await store.WriteAsync(indexKey, new MemoryStream(indexPage), CancellationToken.None);

        return new PartitionRecord(JobId, 1, 1, 1, blockWrite.Value.Size, indexKey.ToString(), indexWrite.Value!.Sha256, indexWrite.Value.Size, DateTimeOffset.UnixEpoch);
    }

    private static byte[] OneItemBlock() => Encoding.UTF8.GetBytes(
        "{\"contractVersion\":\"1.0\",\"header\":{\"reportTitle\":\"Monthly Statement\",\"customer\":{\"name\":\"ACME\",\"id\":\"C-1\"},"
        + "\"currency\":\"USD\",\"generatedAt\":\"2026-03-01\",\"totals\":{\"recordCount\":\"1\",\"amount\":\"10.00\"}},"
        + "\"items\":[{\"sequence\":\"1\",\"description\":\"Widget\",\"quantity\":\"2\",\"unitPrice\":\"5.00\",\"amount\":\"10.00\",\"date\":\"2026-03-01\"}]}");

    private static byte[] EmptyBlock() => Encoding.UTF8.GetBytes(
        "{\"contractVersion\":\"1.0\",\"header\":{\"reportTitle\":\"Monthly Statement\",\"customer\":{\"name\":\"ACME\",\"id\":\"C-1\"},"
        + "\"currency\":\"USD\",\"generatedAt\":\"2026-03-01\",\"totals\":{\"recordCount\":\"0\",\"amount\":\"0.00\"}},"
        + "\"items\":[]}");

    // The taken package as a Pax tar under a content-addressed key, from the given template folder.
    private static async Task<TakenPackage> PackageAsync(IObjectStore store, string folder)
    {
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
        var sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var key = ObjectKey.FromSegments("templates", "consumer", "html-sample", "1.0.0", sha256);
        await store.WriteAsync(key, buffer, CancellationToken.None);
        return new TakenPackage(key, sha256, bytes.LongLength);
    }

    private static TemplatePin Pin(string packageSha256) =>
        new(
            "html-sample",
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
            Id = "html-sample",
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
            },
            LocaleDefaults = new TemplateLocaleDefaults { Culture = "es-AR", TimeZone = "America/Argentina/Buenos_Aires" },
        };

        return new TemplateVersionRecord(
            "html-sample",
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
            FileCount: 5,
            objectKey,
            manifest,
            "{}",
            DateTimeOffset.UnixEpoch);
    }

    private sealed record TakenPackage(ObjectKey Key, string Sha256, long Size);

    // The job store as the HTML assembly step reads it: the three reads it makes — the pinned
    // reference, the committed partition and the job's workflow state — and nothing else. Tracks
    // whether the partition was read, so the "refused before composing" cases can prove it was not.
    private sealed class HtmlAssemblyJobStore : IJobStore
    {
        public TemplateReferenceRecord? Reference { get; init; }

        public PartitionRecord? Partition { get; init; }

        public bool PartitionAsked { get; private set; }

        public Task<TemplateReferenceRecord?> FindTemplateReferenceAsync(string reportJobId, CancellationToken cancellationToken) =>
            Task.FromResult(Reference);

        public Task<PartitionRecord?> FindPartitionAsync(string reportJobId, CancellationToken cancellationToken)
        {
            PartitionAsked = true;
            return Task.FromResult(Partition);
        }

        public Task<JobStateRecord?> FindForWorkflowAsync(string reportJobId, CancellationToken cancellationToken) =>
            Task.FromResult<JobStateRecord?>(new JobStateRecord(
                new ReportJobRecord(reportJobId, "consumer", "upload", ReportJobStatus.Running, new DateTimeOffset(2026, 3, 1, 12, 30, 0, TimeSpan.Zero), DateTimeOffset.UnixEpoch)
                {
                    Culture = "es-AR",
                    TimeZone = "America/Argentina/Buenos_Aires",
                },
                [],
                UploadStatus.Ready,
                DateTimeOffset.UnixEpoch,
                null,
                null,
                null));

        public Task<PartCommit?> FindPartAsync(string outputId, int blockIndex, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IdempotencyLookup> FindByIdempotencyKeyAsync(string consumerId, string idempotencyKey, RequestFingerprint fingerprint, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<PartCommitResult> CommitPartAsync(PartCommit commit, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<JobAcceptanceResult> AcceptAsync(JobAcceptance acceptance, DateTimeOffset now, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<JobFailureOutcome> FailJobAsync(string reportJobId, string code, string reason, string? message, DateTimeOffset now, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<TemplatePinState> PinTemplateAsync(string reportJobId, TemplatePin pin, DateTimeOffset now, CancellationToken cancellationToken, TemplateLocaleDefaults? presentation) => throw new NotSupportedException();
        public Task<BeginInputWaitOutcome> BeginInputWaitAsync(string reportJobId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<EndInputWaitOutcome> EndInputWaitAsync(string reportJobId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> FindInputWaitingJobIdsAsync(string uploadId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<TransitionJobResult> TransitionJobAsync(string reportJobId, IReadOnlyList<ReportJobStatus> from, ReportJobStatus to, DateTimeOffset now, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<TransitionOutcome> TransitionOutputAsync(string outputId, DateTimeOffset now, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<PartitionCommitResult> CommitPartitionAsync(PartitionRecord partition, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<PublishResult> PublishAsync(ArtifactRecord artifact, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<JobArtifactsView?> ListArtifactsAsync(string reportJobId, string consumerId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ArtifactView?> FindArtifactAsync(string reportJobId, string artifactId, string consumerId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<OutputFailureOutcome> FailOutputAsync(string outputId, string code, string? reason, DateTimeOffset now, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<JobFinalizationResult> FinalizeJobAsync(string reportJobId, DateTimeOffset now, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ReportJobStatusView?> FindStatusAsync(string reportJobId, string consumerId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ProgressWriteOutcome> RecordRecordsReadAsync(string reportJobId, long recordsRead, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AttemptCauseOutcome> RecordAttemptCauseAsync(AttemptCause cause, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AttemptCause?> FindLastAttemptCauseAsync(string outputId, int blockIndex, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    // The version store as the step reads it: it answers the one taken version for any find.
    private sealed class HtmlAssemblyVersionStore(TemplateVersionRecord record) : ITemplateVersionStore
    {
        public Task<TemplateVersionRecord?> FindAsync(string templateId, string version, CancellationToken cancellationToken) =>
            Task.FromResult<TemplateVersionRecord?>(record);

        public Task<IReadOnlyList<TemplateVersionRecord>> ListByTemplateAsync(string templateId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<TemplateVersionInsert> TryInsertAsync(TemplateVersionRecord record, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
