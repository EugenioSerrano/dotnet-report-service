using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using ReportService.Data;
using ReportService.Jobs;
using ReportService.Storage;
using ReportService.Storage.Filesystem;
using ReportService.Testing;
using ReportService.Uploads;
using ReportService.Workers.Orchestration.Jobs;
using Temporalio.Exceptions;
using Temporalio.Testing;

namespace ReportService.Workers.Orchestration.Tests.Jobs;

/// <summary>
/// The data step of a job, run inside the engine's host against a storage root of the
/// test's own, the in-memory records and a clock the test holds: the whole path from a
/// ready snapshot to a committed partition, the answer a second attempt receives, and
/// what it refuses when there is nothing to read.
/// </summary>
public sealed class ValidateAndPartitionActivityTests : IDisposable
{
    private const string ReportJobId = "job-0123456789abcdef0123456789abcdef";

    private const string ConsumerId = "dev-consumer";

    private static readonly DateTimeOffset Now = new(2026, 11, 1, 6, 12, 30, 400, TimeSpan.Zero);

    private readonly LoggerFactory _loggerFactory;
    private readonly string _root;
    private bool _disposed;

    /// <summary>Creates the rig of one case.</summary>
    public ValidateAndPartitionActivityTests()
    {
        _root = Path.Combine(
            Path.GetTempPath(),
            "report-service-tests",
            Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(_root);

        Logs = new LogCapture();
        _loggerFactory = new LoggerFactory([Logs]);
        Objects = new RecordingObjectStore(FilesystemObjectStore.Open(
            new FilesystemStorageOptions { Root = _root },
            _root,
            TimeProvider.System,
            _loggerFactory.CreateLogger<FilesystemObjectStore>()));
        Uploads = new InMemoryUploadStore();
        Jobs = new InMemoryJobStore(Uploads);
        Activity = new ValidateAndPartitionActivity(
            Jobs,
            Uploads,
            Objects,
            new FixedTimeProvider(Now),
            _loggerFactory.CreateLogger<ValidateAndPartitionActivity>());
    }

    /// <summary>Every line logged while the case ran.</summary>
    public LogCapture Logs { get; }

    /// <summary>The storage port of this case, counting what it was asked to do.</summary>
    internal RecordingObjectStore Objects { get; }

    /// <summary>The upload records of this case.</summary>
    public InMemoryUploadStore Uploads { get; }

    /// <summary>The job records of this case.</summary>
    public InMemoryJobStore Jobs { get; }

    /// <summary>The step under test.</summary>
    public ValidateAndPartitionActivity Activity { get; }

    [Fact]
    public async Task AReadySnapshotBecomesOneBlockAndOneIndexOfTwoRecords()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedAcceptedJobAsync(Records(2), cancellationToken);
        var writesBefore = Objects.Writes;

        var result = await RunAsync();

        Assert.Equal(1, result.BlockCount);
        Assert.Equal(2, result.RecordCount);
        Assert.Equal(1, result.FirstRecordOrdinal);
        Assert.Equal(SingleBlockPartitioner.PartitionerVersion, result.PartitionerVersion);
        Assert.Equal(2, Objects.Writes - writesBefore);
        Assert.Matches("^[0-9a-f]{64}$", result.BlockSha256);
        Assert.Matches("^[0-9a-f]{64}$", result.IndexSha256);

        var partition = await Jobs.FindPartitionAsync(ReportJobId, cancellationToken);
        Assert.NotNull(partition);
        Assert.Equal(2, partition!.RecordCount);
        Assert.Equal(result.IndexKey, partition.IndexObjectKey);
    }

    [Fact]
    public async Task ASecondAttemptReadsWhatTheFirstOneCommitted()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedAcceptedJobAsync(Records(3), cancellationToken);
        var first = await RunAsync();
        var writesAfterFirst = Objects.Writes;

        var second = await RunAsync();

        Assert.Equal(first.BlockKey, second.BlockKey);
        Assert.Equal(first.BlockSha256, second.BlockSha256);
        Assert.Equal(3, second.RecordCount);
        Assert.Equal(writesAfterFirst, Objects.Writes);
    }

    [Fact]
    public async Task ASnapshotWithNoRecordBecomesTheEmptyBlock()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedAcceptedJobAsync(Records(0), cancellationToken);

        var result = await RunAsync();

        Assert.Equal(1, result.BlockCount);
        Assert.Equal(0, result.RecordCount);
        Assert.Equal(1, result.FirstRecordOrdinal);
    }

    [Fact]
    public async Task ASnapshotTheProfileRefusesEndsTheStepWithoutBeingRetried()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedAcceptedJobAsync(Encoding.UTF8.GetBytes("""{"contractVersion":"2.0","header":{},"items":[]}"""), cancellationToken);

        var failure = await Assert.ThrowsAsync<DataContractViolation>(RunAsync);

        Assert.Equal("contract-version", failure.Reason);
        Assert.Null(await Jobs.FindPartitionAsync(ReportJobId, cancellationToken));
    }

    [Fact]
    public async Task ThePartitionThatAlreadyExistsIsAnsweredFromItsIndexPage()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var entry = new BlockIndexEntry(
            0,
            $"block/{ReportJobId}/0/attempt",
            new string('a', 64),
            1_024,
            1,
            25,
            512);
        var page = BlockIndexPage.Serialize(new BlockIndexPage(SingleBlockPartitioner.PartitionerVersion, [entry]));
        var indexKey = ObjectKey.FromSegments("index", ReportJobId, "0", "attempt");
        using var content = new MemoryStream(page, writable: false);
        var write = await Objects.WriteAsync(indexKey, content, cancellationToken);
        Assert.True(write.Succeeded);

        await Jobs.CommitPartitionAsync(
            new PartitionRecord(
                ReportJobId,
                SingleBlockPartitioner.PartitionerVersion,
                1,
                25,
                512,
                indexKey.ToString(),
                write.Value!.Sha256,
                write.Value.Size,
                Now),
            cancellationToken);

        var result = await RunAsync();

        Assert.Equal(25, result.RecordCount);
        Assert.Equal(entry.Key, result.BlockKey);
        Assert.Equal(entry.Sha256, result.BlockSha256);
    }

    [Fact]
    public async Task AJobThatIsNotRecordedIsRefusedWithoutBeingRetried()
    {
        var failure = await Assert.ThrowsAsync<ApplicationFailureException>(RunAsync);

        Assert.Equal("INPUT_NOT_FOUND", failure.ErrorType);
        Assert.True(failure.NonRetryable);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _loggerFactory.Dispose();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temporary directory is not a test failure.
        }
    }

    private static byte[] Records(int count)
    {
        var items = string.Join(',', Enumerable.Range(1, count).Select(ordinal => $$"""{"sequence":{{ordinal}},"amount":{{ordinal}}.5}"""));
        return Encoding.UTF8.GetBytes($$"""{"contractVersion":"1.0","header":{"title":"Monthly"},"items":[{{items}}]}""");
    }

    // The snapshot is written to the storage root and the upload is driven to ready
    // through the upload records' own steps, so the case starts from a state the
    // service can reach.
    private async Task SeedAcceptedJobAsync(byte[] snapshot, CancellationToken cancellationToken)
    {
        var uploadId = UploadIdentifier.Prefix + "0123456789abcdef0123456789abcdef";
        var staging = ObjectKey.FromSegments("staging", uploadId, "content");
        var created = Now.AddHours(-1);
        await Uploads.CreateAsync(
            new UploadRecord(
                uploadId,
                ConsumerId,
                UploadMode.ApiStream,
                DataProfile.HeaderItemsV1,
                UploadStatus.Created,
                staging,
                created,
                created.AddHours(2)),
            cancellationToken);

        var key = ObjectKey.FromSegments("snapshots", uploadId, "a");
        using var content = new MemoryStream(snapshot, writable: false);
        var written = await Objects.WriteAsync(key, content, cancellationToken);
        Assert.True(written.Succeeded);

        var started = Now.AddMinutes(-20);
        Assert.True(await Uploads.TryStartTransferAsync(uploadId, ConsumerId, UploadContentEncoding.None, started, cancellationToken));
        Assert.True(await Uploads.RecordTransferAsync(
            uploadId, UploadContentOutcome.Complete, snapshot.Length, written.Value!.Size, written.Value.Sha256, started, cancellationToken));
        Assert.True(await Uploads.ConfirmAsync(
            uploadId, ConsumerId, written.Value.Sha256, started, started, Now.AddHours(5), cancellationToken));
        Assert.Equal(
            SnapshotCommitResult.Committed,
            await Uploads.CommitReadyAsync(
                new SnapshotRecord(
                    uploadId,
                    ConsumerId,
                    key,
                    UploadContentEncoding.None,
                    DataProfile.HeaderItemsV1,
                    written.Value.Sha256,
                    snapshot.Length,
                    written.Value.Sha256,
                    written.Value.Size,
                    started),
                started,
                cancellationToken));

        var accepted = await Jobs.AcceptAsync(
            new JobAcceptance(
                ReportJobId,
                "output-0123456789abcdef0123456789abcdef",
                ConsumerId,
                uploadId,
                "pilot-simple",
                TemplateReferenceForm.Exact,
                "1.0.0"),
            Now,
            cancellationToken);
        Assert.IsType<JobAcceptanceResult.Accepted>(accepted);
    }

    private Task<PartitionResult> RunAsync()
    {
        var environment = new ActivityEnvironment
        {
            Info = ActivityEnvironment.DefaultInfo with
            {
                WorkflowId = $"report-job/{ReportJobId}",
                WorkflowRunId = "run-0123456789abcdef",
                ActivityId = "1",
                ActivityType = ValidateAndPartitionActivity.Name,
                Attempt = 1,
                TaskQueue = PdfOutputActivityOptions.DataQueue,
            },
            Heartbeater = _ => { },
        };
        return environment.RunAsync(() => Activity.RunAsync(new ValidateAndPartitionInput(ReportJobId)));
    }
}
