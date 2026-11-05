using System.Formats.Tar;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using ReportService.Data;
using ReportService.Jobs;
using ReportService.Pdf.Verification;
using ReportService.Rendering;
using ReportService.Storage;
using ReportService.Storage.Filesystem;
using ReportService.TemplatePackages.Manifest;
using ReportService.TemplatePackages.Packages;
using ReportService.TemplatePackages.Records;
using ReportService.Templates;
using ReportService.Testing;
using ReportService.Uploads;
using ReportService.Workers.Pdf.Rendering;
using Temporalio.Testing;

namespace ReportService.Workers.Pdf.Tests.Rendering;

/// <summary>
/// The render step as a plain class: what it answers when the block was printed
/// already, what it refuses when the job's own state cannot be used, and one whole
/// render with doubles of the store, the storage and the renderer - which proves the
/// object key it writes, the commit it records and the answer it gives.
/// </summary>
public sealed class RenderPdfBlockActivityTests : IDisposable
{
    private const string JobId = "job-1";
    private const string OutputId = "output-1";
    private readonly string root = Path.Combine(Path.GetTempPath(), "render-step-" + Guid.NewGuid().ToString("N"));

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
    public void TheQueueRunsOneActivityAtATimeAndRetriesThreeTimes()
    {
        Assert.Equal("report-pdf-rendering", RenderPdfBlockOptions.Queue);
        Assert.Equal(TimeSpan.FromSeconds(5), RenderPdfBlockOptions.HeartbeatEvery);
        Assert.Equal(TimeSpan.FromSeconds(30), RenderPdfBlockOptions.HeartbeatTimeout);
        Assert.Equal(TimeSpan.FromSeconds(300), RenderPdfBlockOptions.StartToClose);
        Assert.Equal(3, RenderPdfBlockOptions.MaximumAttempts);

        var options = RenderPdfBlockOptions.Create();
        Assert.Equal("report-pdf-rendering", options.TaskQueue);
        Assert.Equal(3, options.RetryPolicy!.MaximumAttempts);
        Assert.Contains("PAGINATION_FAILED", RenderPdfBlockOptions.NonRetryableErrorTypes);
        Assert.Contains("UNSUPPORTED_CAPABILITY", RenderPdfBlockOptions.NonRetryableErrorTypes);
    }

    [Fact]
    public void TheAttemptIdentityIsOneStableDigestPerAttempt()
    {
        var first = RenderPdfBlockOptions.AttemptId("job", "run", "activity", 1);
        var second = RenderPdfBlockOptions.AttemptId("job", "run", "activity", 1);
        var other = RenderPdfBlockOptions.AttemptId("job", "run", "activity", 2);

        Assert.Equal(64, first.Length);
        Assert.Equal(first, second);
        Assert.NotEqual(first, other);
        Assert.All(first, character => Assert.True(char.IsAsciiHexDigitLower(character)));
    }

    [Fact]
    public void TheAttemptDirectoryIsCreatedAndRemoved()
    {
        string path;
        using (var directory = WorkerTemporaryDirectory.Create(Path.Combine(root, "work")))
        {
            path = directory.Path;
            Assert.True(Directory.Exists(path));
            File.WriteAllText(Path.Combine(path, "printed.pdf"), "x");
        }

        Assert.False(Directory.Exists(path));
    }

    [Fact]
    public async Task ABlockThatWasCommittedAlreadyIsAnsweredWithoutRendering()
    {
        var commit = new PartCommit(OutputId, 0, "outputs/job-1/output-1/attempt", new string('a', 64), 10, 2, new string('b', 64), DateTimeOffset.UnixEpoch);
        var activity = Activity(jobs: new FakeJobStore { Part = commit }, renderer: new FakeRenderer());

        var result = await new ActivityEnvironment().RunAsync(() => activity.RunAsync(new RenderPdfBlockInput(JobId, OutputId, 0)));

        Assert.Equal(commit.ObjectKey, result.PartKey);
        Assert.Equal(commit.Sha256, result.Sha256);
        Assert.Equal(2, result.PageCount);
    }

    [Fact]
    public async Task AJobWhoseTemplateWasNeverResolvedIsRefused()
    {
        var activity = Activity(jobs: new FakeJobStore { Reference = null }, renderer: new FakeRenderer());

        var failure = await Assert.ThrowsAsync<Temporalio.Exceptions.ApplicationFailureException>(
            () => new ActivityEnvironment().RunAsync(() => activity.RunAsync(new RenderPdfBlockInput(JobId, OutputId, 0))));

        Assert.Equal("EXECUTION_FAILED", failure.ErrorType);
        Assert.True(failure.NonRetryable);
    }

    [Fact]
    public async Task AJobWhoseVersionWasNeverTakenIsRefused()
    {
        var jobs = new FakeJobStore { Reference = ReferenceOf(Pin()) };
        var activity = Activity(jobs: jobs, versions: new FakeVersionStore { Record = null }, renderer: new FakeRenderer());

        var failure = await Assert.ThrowsAsync<Temporalio.Exceptions.ApplicationFailureException>(
            () => new ActivityEnvironment().RunAsync(() => activity.RunAsync(new RenderPdfBlockInput(JobId, OutputId, 0))));

        Assert.Equal("EXECUTION_FAILED", failure.ErrorType);
    }

    [Fact]
    public async Task AVersionThatNoLongerMatchesThePinIsRefused()
    {
        var jobs = new FakeJobStore { Reference = ReferenceOf(Pin(packageSha256: new string('c', 64))) };
        var activity = Activity(jobs: jobs, versions: new FakeVersionStore { Record = Record(new string('d', 64), default, 0) }, renderer: new FakeRenderer());

        var failure = await Assert.ThrowsAsync<Temporalio.Exceptions.ApplicationFailureException>(
            () => new ActivityEnvironment().RunAsync(() => activity.RunAsync(new RenderPdfBlockInput(JobId, OutputId, 0))));

        Assert.Equal("EXECUTION_FAILED", failure.ErrorType);
    }

    [Fact]
    public async Task AJobWithoutAPartitionIsRefused()
    {
        var jobs = new FakeJobStore { Reference = ReferenceOf(Pin()) };
        var activity = Activity(jobs: jobs, versions: new FakeVersionStore { Record = Record(Pin().PackageSha256, default, 0) }, renderer: new FakeRenderer());

        var failure = await Assert.ThrowsAsync<Temporalio.Exceptions.ApplicationFailureException>(
            () => new ActivityEnvironment().RunAsync(() => activity.RunAsync(new RenderPdfBlockInput(JobId, OutputId, 0))));

        Assert.Equal("EXECUTION_FAILED", failure.ErrorType);
    }

    [Fact]
    public async Task ABlockThatPrintsAndCommitsAnswersWithItsCommit()
    {
        var storage = await StorageAsync();
        var package = await PackageAsync(storage.Store);
        var pin = Pin(package.Sha256);
        var record = Record(pin.PackageSha256, package.Key, package.Size);
        var jobs = new FakeJobStore
        {
            Reference = ReferenceOf(pin),
            Partition = new PartitionRecord(JobId, 1, 1, 1, storage.Block.SizeBytes, storage.Index.Key, storage.Index.Sha256, storage.Index.SizeBytes, DateTimeOffset.UnixEpoch),
        };
        var renderer = new FakeRenderer();
        var activity = Activity(jobs, new FakeVersionStore { Record = record }, renderer, storage.Store);

        var result = await new ActivityEnvironment().RunAsync(() => activity.RunAsync(new RenderPdfBlockInput(JobId, OutputId, 0)));

        Assert.StartsWith("outputs/", result.PartKey, StringComparison.Ordinal);
        Assert.Equal(2, result.PageCount);
        Assert.NotNull(jobs.Committed);
        Assert.Equal(result.PartKey, jobs.Committed!.ObjectKey);
        Assert.Equal(pin.PackageSha256, jobs.Committed.TemplatePackageSha256);
        Assert.True(renderer.Called);
    }

    [Fact]
    public async Task ABlockOutsideTheJobsPartitionIsRefused()
    {
        var jobs = new FakeJobStore
        {
            Reference = ReferenceOf(Pin()),
            Partition = new PartitionRecord(JobId, 1, 1, 1, 0, "index/job-1/0/a", new string('a', 64), 1, DateTimeOffset.UnixEpoch),
        };
        var activity = Activity(jobs: jobs, versions: new FakeVersionStore { Record = Record(Pin().PackageSha256, default, 0) }, renderer: new FakeRenderer());

        var failure = await Assert.ThrowsAsync<Temporalio.Exceptions.ApplicationFailureException>(
            () => new ActivityEnvironment().RunAsync(() => activity.RunAsync(new RenderPdfBlockInput(JobId, OutputId, 3))));

        Assert.Equal("EXECUTION_FAILED", failure.ErrorType);
        Assert.True(failure.NonRetryable);
    }

    [Fact]
    public async Task APartitionWhoseIndexPageIsNotThereIsRefused()
    {
        var jobs = new FakeJobStore
        {
            Reference = ReferenceOf(Pin()),
            Partition = new PartitionRecord(JobId, 1, 1, 1, 0, "index/job-1/0/missing", new string('a', 64), 1, DateTimeOffset.UnixEpoch),
        };
        var activity = Activity(jobs: jobs, versions: new FakeVersionStore { Record = Record(Pin().PackageSha256, default, 0) }, renderer: new FakeRenderer());

        await Assert.ThrowsAnyAsync<Exception>(
            () => new ActivityEnvironment().RunAsync(() => activity.RunAsync(new RenderPdfBlockInput(JobId, OutputId, 0))));
    }

    [Fact]
    public async Task ATakenCopyThatIsNotThereIsRefused()
    {
        var storage = await StorageAsync();
        var pin = Pin(new string('9', 64));
        var jobs = new FakeJobStore
        {
            Reference = ReferenceOf(pin),
            Partition = new PartitionRecord(JobId, 1, 1, 1, storage.Block.SizeBytes, storage.Index.Key, storage.Index.Sha256, storage.Index.SizeBytes, DateTimeOffset.UnixEpoch),
        };
        var record = Record(pin.PackageSha256, ObjectKey.FromSegments("templates", "consumer", "integrity-table", "1.0.0", new string('9', 64)), 10);
        var activity = Activity(jobs, new FakeVersionStore { Record = record }, new FakeRenderer(), storage.Store);

        var failure = await Assert.ThrowsAsync<Temporalio.Exceptions.ApplicationFailureException>(
            () => new ActivityEnvironment().RunAsync(() => activity.RunAsync(new RenderPdfBlockInput(JobId, OutputId, 0))));

        Assert.Equal("EXECUTION_FAILED", failure.ErrorType);
    }

    private RenderPdfBlockActivity Activity(
        FakeJobStore jobs,
        FakeVersionStore? versions = null,
        FakeRenderer? renderer = null,
        IObjectStore? store = null)
    {
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
            new ScribanTemplateAdapter(),
            renderer ?? new FakeRenderer(),
            TimeProvider.System,
            NullLogger<RenderPdfBlockActivity>.Instance,
            Path.Combine(root, "scratch"));
    }

    private static TemplatePin Pin(string? packageSha256 = null) =>
        new("integrity-table", "1.0.0", new string('e', 40), packageSha256 ?? new string('f', 64), TemplateEngine.Scriban, "7.5.1", "schema.json", DateTimeOffset.UnixEpoch);

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

    /// <summary>The block and its index as the data step leaves them, written to a filesystem store.</summary>
    private async Task<Storage> StorageAsync()
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

        return new Storage(
            store,
            new BlockIndexEntry(0, blockKey.ToString(), blockWrite.Value.Sha256, blockWrite.Value.Size, 1, 1, block.Length),
            new BlockIndexEntry(0, indexKey.ToString(), indexWrite.Value!.Sha256, indexWrite.Value.Size, 1, 1, indexPage.Length));
    }

    /// <summary>The taken package as a tar, written to the store under a content-addressed key.</summary>
    private static async Task<TakenPackage> PackageAsync(IObjectStore store)
    {
        var folder = Path.Combine(RepositoryPaths.Root, "tests", "ReportService.Testing", "Templates", "integrity-table");
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

    private sealed record Storage(IObjectStore Store, BlockIndexEntry Block, BlockIndexEntry Index);

    private sealed record TakenPackage(ObjectKey Key, string Sha256, long Size);
}

/// <summary>The store as the step reads it: the four calls it makes, and nothing else.</summary>
internal sealed class FakeJobStore : IJobStore
{
    public TemplateReferenceRecord? Reference { get; init; }

    public PartitionRecord? Partition { get; init; }

    public PartCommit? Part { get; init; }

    public PartCommit? Commit { get; init; }

    public PartCommit? Committed { get; private set; }

    public Task<PartCommit?> FindPartAsync(string outputId, int blockIndex, CancellationToken cancellationToken) =>
        Task.FromResult(Part);

    public Task<TemplateReferenceRecord?> FindTemplateReferenceAsync(string reportJobId, CancellationToken cancellationToken) =>
        Task.FromResult(Reference);

    public Task<PartitionRecord?> FindPartitionAsync(string reportJobId, CancellationToken cancellationToken) =>
        Task.FromResult(Partition);

    public Task<JobStateRecord?> FindForWorkflowAsync(string reportJobId, CancellationToken cancellationToken) =>
        Task.FromResult<JobStateRecord?>(new JobStateRecord(
            new ReportJobRecord(reportJobId, "consumer", "upload", ReportJobStatus.Running, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch),
            [],
            UploadStatus.Ready,
            DateTimeOffset.UnixEpoch,
            null,
            null,
            null));

    public Task<PartCommitResult> CommitPartAsync(PartCommit commit, CancellationToken cancellationToken)
    {
        Committed = commit;
        return Task.FromResult(new PartCommitResult(PartCommitOutcome.Committed, commit));
    }

    public Task<JobAcceptanceResult> AcceptAsync(JobAcceptance acceptance, DateTimeOffset now, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<JobFailureOutcome> FailJobAsync(string reportJobId, string code, string reason, string? message, DateTimeOffset now, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<TemplatePinState> PinTemplateAsync(string reportJobId, TemplatePin pin, DateTimeOffset now, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<BeginInputWaitOutcome> BeginInputWaitAsync(string reportJobId, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<EndInputWaitOutcome> EndInputWaitAsync(string reportJobId, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<IReadOnlyList<string>> FindInputWaitingJobIdsAsync(string uploadId, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<TransitionJobResult> TransitionJobAsync(string reportJobId, IReadOnlyList<ReportJobStatus> from, ReportJobStatus to, DateTimeOffset now, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<TransitionOutcome> TransitionOutputAsync(string outputId, DateTimeOffset now, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<PartitionCommitResult> CommitPartitionAsync(PartitionRecord partition, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<PublishResult> PublishAsync(ArtifactRecord artifact, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<OutputFailureOutcome> FailOutputAsync(string outputId, string code, DateTimeOffset now, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<JobFinalizationResult> FinalizeJobAsync(string reportJobId, DateTimeOffset now, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<ReportJobStatusView?> FindStatusAsync(string reportJobId, string consumerId, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<ProgressWriteOutcome> RecordRecordsReadAsync(string reportJobId, long recordsRead, CancellationToken cancellationToken) => throw new NotSupportedException();
}

/// <summary>The version store as the step reads it.</summary>
internal sealed class FakeVersionStore : ITemplateVersionStore
{
    public TemplateVersionRecord? Record { get; init; }

    public Task<TemplateVersionRecord?> FindAsync(string templateId, string version, CancellationToken cancellationToken) => Task.FromResult(Record);

    public Task<IReadOnlyList<TemplateVersionRecord>> ListByTemplateAsync(string templateId, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<TemplateVersionInsert> TryInsertAsync(TemplateVersionRecord record, CancellationToken cancellationToken) => throw new NotSupportedException();
}

/// <summary>The renderer as the step calls it: it answers a verified document and writes nothing.</summary>
internal sealed class FakeRenderer : IBlockRenderer
{
    public bool Called { get; private set; }

    public async Task<BlockRenderOutcome> RenderAsync(BlockRenderRequest request, CancellationToken cancellationToken = default)
    {
        Called = true;
        Directory.CreateDirectory(request.WorkDirectory);
        var path = Path.Combine(request.WorkDirectory, "block-0.pdf");
        await File.WriteAllBytesAsync(path, "%PDF-1.4 printed"u8.ToArray(), cancellationToken).ConfigureAwait(false);
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        return new BlockRenderOutcome(
            path,
            bytes.LongLength,
            Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)),
            Print: null,
            new VerificationResult(true, null, 0, null, null, null, null, null, 2, 10, 1, 1));
    }
}
