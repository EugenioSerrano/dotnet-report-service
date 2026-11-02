using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ReportService.Storage;
using ReportService.Testing;
using ReportService.Uploads;
using ReportService.Workers.Orchestration.Telemetry;
using ReportService.Workers.Orchestration.Uploads;

namespace ReportService.Workers.Orchestration.Tests;

/// <summary>
/// The sweep that makes an upload's stored state catch up without traffic: which
/// uploads it expires, what it deletes with them, how it behaves over several
/// batches and what it does when the storage refuses to delete. The interval is
/// advanced on a clock the test owns, never waited for.
/// </summary>
public sealed class UploadExpirySweeperTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 1, 4, 41, 2, 456, TimeSpan.Zero);

    private static readonly DateTimeOffset Later = Now.AddDays(1);

    [Fact]
    public async Task OverdueUploadsOfEveryStateAreExpiredAndTheRestIsUntouched()
    {
        var (sweeper, store, objects) = Create();
        var created = Session("upload-00000000000000000000000000000001", UploadStatus.Created, expiresAt: Now);
        var uploading = Session("upload-00000000000000000000000000000002", UploadStatus.Uploading, expiresAt: Now);
        var verifying = Session("upload-00000000000000000000000000000003", UploadStatus.Verifying, deadline: Now.AddMinutes(-1));
        var stillOpen = Session("upload-00000000000000000000000000000004", UploadStatus.Created, expiresAt: Later);
        var verifyingInTime = Session("upload-00000000000000000000000000000005", UploadStatus.Verifying, deadline: Later);
        foreach (var record in new[] { created, uploading, verifying, stillOpen, verifyingInTime })
        {
            await store.CreateAsync(record, CancellationToken.None);
        }

        var expired = await sweeper.SweepAsync(CancellationToken.None);

        Assert.Equal(3, expired);
        Assert.Equal(
            UploadStatus.Expired,
            (await store.FindAsync(created.UploadId, created.ConsumerId, CancellationToken.None))!.Status);
        Assert.Equal(
            UploadStatus.Expired,
            (await store.FindAsync(uploading.UploadId, uploading.ConsumerId, CancellationToken.None))!.Status);
        Assert.Equal(
            UploadStatus.Expired,
            (await store.FindAsync(verifying.UploadId, verifying.ConsumerId, CancellationToken.None))!.Status);
        Assert.Equal(
            UploadStatus.Created,
            (await store.FindAsync(stillOpen.UploadId, stillOpen.ConsumerId, CancellationToken.None))!.Status);
        Assert.Equal(
            UploadStatus.Verifying,
            (await store.FindAsync(verifyingInTime.UploadId, verifyingInTime.ConsumerId, CancellationToken.None))!.Status);

        Assert.Equal(3, objects.Deleted.Count);
        Assert.Contains(created.StagingKey, objects.Deleted);
        Assert.Contains(uploading.StagingKey, objects.Deleted);
        Assert.Contains(verifying.StagingKey, objects.Deleted);
    }

    [Fact]
    public async Task TheCauseOfEachExpiryIsTheEndItsUploadRanOutOf()
    {
        var (sweeper, store, _) = Create(500, out var logs);
        await store.CreateAsync(
            Session("upload-00000000000000000000000000000001", UploadStatus.Created, expiresAt: Now), CancellationToken.None);
        await store.CreateAsync(
            Session("upload-00000000000000000000000000000002", UploadStatus.Verifying, deadline: Now), CancellationToken.None);

        await sweeper.SweepAsync(CancellationToken.None);

        Assert.Contains(logs.Lines, line => line.Contains("expired (session)", StringComparison.Ordinal));
        Assert.Contains(logs.Lines, line => line.Contains("expired (verification-deadline)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ASweepExpiresMoreThanOneBatch()
    {
        var (sweeper, store, objects) = Create(batchSize: 2);
        var due = new List<UploadRecord>();
        for (var index = 0; index < 5; index++)
        {
            var record = Session(
                "upload-0000000000000000000000000000000" + index.ToString(System.Globalization.CultureInfo.InvariantCulture),
                UploadStatus.Created,
                expiresAt: Now);
            due.Add(record);
            await store.CreateAsync(record, CancellationToken.None);
        }

        var expired = await sweeper.SweepAsync(CancellationToken.None);

        Assert.Equal(5, expired);
        Assert.Equal(5, objects.Deleted.Count);
        foreach (var record in due)
        {
            Assert.Equal(
                UploadStatus.Expired,
                (await store.FindAsync(record.UploadId, record.ConsumerId, CancellationToken.None))!.Status);
        }
    }

    [Fact]
    public async Task ASecondSweepExpiresNothing()
    {
        var (sweeper, store, objects) = Create();
        await store.CreateAsync(
            Session("upload-00000000000000000000000000000001", UploadStatus.Created, expiresAt: Now), CancellationToken.None);

        Assert.Equal(1, await sweeper.SweepAsync(CancellationToken.None));
        Assert.Equal(0, await sweeper.SweepAsync(CancellationToken.None));
        Assert.Single(objects.Deleted);
    }

    [Fact]
    public async Task AStorageFailureIsLoggedAndDoesNotStopTheSweep()
    {
        var (sweeper, store, objects) = Create(500, out var logs);
        objects.ThrowOnDelete = true;
        await store.CreateAsync(
            Session("upload-00000000000000000000000000000001", UploadStatus.Created, expiresAt: Now), CancellationToken.None);
        await store.CreateAsync(
            Session("upload-00000000000000000000000000000002", UploadStatus.Created, expiresAt: Now), CancellationToken.None);

        var expired = await sweeper.SweepAsync(CancellationToken.None);

        // The uploads are expired whatever the storage did with their objects, and the
        // refusal is logged once per upload rather than ending the round.
        Assert.Equal(2, expired);
        Assert.Equal(
            UploadStatus.Expired,
            (await store.FindAsync("upload-00000000000000000000000000000001", "dev-consumer", CancellationToken.None))!.Status);
        Assert.Contains(logs.Lines, line => line.Contains("could not be deleted", StringComparison.Ordinal));
        Assert.Equal(0, await sweeper.SweepAsync(CancellationToken.None));
    }

    [Fact]
    public async Task TheIntervalDrivesTheSweepWithoutWaitingForWallClockTime()
    {
        var (sweeper, store, objects, clock) = CreateWithClock();
        await store.CreateAsync(
            Session("upload-00000000000000000000000000000001", UploadStatus.Created, expiresAt: Now), CancellationToken.None);
        using var stopping = new CancellationTokenSource();

        await sweeper.StartAsync(stopping.Token);
        try
        {
            // The loop arms its interval on the clock the test owns; only then can the test
            // end the wait, so nothing depends on the machine's speed.
            await Wait.UntilAsync(
                "the sweep to arm its interval",
                _ => Task.FromResult(clock.PendingTimers > 0),
                TimeSpan.FromSeconds(5),
                TimeSpan.FromMilliseconds(20),
                CancellationToken.None);
            clock.Advance(TimeSpan.FromMinutes(1));
            await Wait.UntilAsync(
                "the sweep to expire the overdue upload",
                async token => (await store.FindAsync(
                    "upload-00000000000000000000000000000001", "dev-consumer", token))?.Status == UploadStatus.Expired,
                TimeSpan.FromSeconds(5),
                TimeSpan.FromMilliseconds(20),
                CancellationToken.None);

            Assert.Single(objects.Deleted);
        }
        finally
        {
            await sweeper.StopAsync(CancellationToken.None);
            stopping.Dispose();
        }
    }

    private static (UploadExpirySweeper Sweeper, InMemoryUploadStore Store, RecordingObjectStore Objects) Create(
        int batchSize = 500) => Create(batchSize, out _);

    private static (UploadExpirySweeper Sweeper, InMemoryUploadStore Store, RecordingObjectStore Objects) Create(
        int batchSize,
        out LogCapture logs)
    {
        var clock = new ManualTimeProvider(Now);
        var store = new InMemoryUploadStore();
        var objects = new RecordingObjectStore();
        logs = new LogCapture();
        var sweeper = new UploadExpirySweeper(
            store,
            objects,
            Options.Create(new UploadExpiryOptions { SweepInterval = TimeSpan.FromMinutes(1), BatchSize = batchSize }),
            clock,
            new OrchestrationMetrics(),
            new LoggerFactory([logs]).CreateLogger<UploadExpirySweeper>());
        return (sweeper, store, objects);
    }

    private static (UploadExpirySweeper Sweeper, InMemoryUploadStore Store, RecordingObjectStore Objects, ManualTimeProvider Clock)
        CreateWithClock()
    {
        var clock = new ManualTimeProvider(Now);
        var store = new InMemoryUploadStore();
        var objects = new RecordingObjectStore();
        var sweeper = new UploadExpirySweeper(
            store,
            objects,
            Options.Create(new UploadExpiryOptions { SweepInterval = TimeSpan.FromMinutes(1), BatchSize = 500 }),
            clock,
            new OrchestrationMetrics(),
            new LoggerFactory([new LogCapture()]).CreateLogger<UploadExpirySweeper>());
        return (sweeper, store, objects, clock);
    }

    private static UploadRecord Session(
        string uploadId,
        UploadStatus status,
        DateTimeOffset? expiresAt = null,
        DateTimeOffset? deadline = null) => new(
        uploadId,
        "dev-consumer",
        UploadMode.ApiStream,
        DataProfile.HeaderItemsV1,
        status,
        ObjectKey.FromSegments("staging", uploadId, "content"),
        Now.AddHours(-1),
        expiresAt ?? Later)
        {
            CompletedAt = status == UploadStatus.Verifying ? Now.AddHours(-7) : null,
            VerificationDeadline = status == UploadStatus.Verifying ? deadline ?? Later : null,
        };

    [Fact]
    public async Task AStopDuringARoundEndsTheSweepWithoutFailingTheHost()
    {
        var clock = new ManualTimeProvider(Now);
        using var stopping = new CancellationTokenSource();
        using var metrics = new OrchestrationMetrics();
        var sweeper = new UploadExpirySweeper(
            new StoppingStore(stopping),
            new RecordingObjectStore(),
            Options.Create(new UploadExpiryOptions { SweepInterval = TimeSpan.FromMinutes(1), BatchSize = 500 }),
            clock,
            metrics,
            new LoggerFactory([new LogCapture()]).CreateLogger<UploadExpirySweeper>());

        await sweeper.StartAsync(stopping.Token);
        try
        {
            await Wait.UntilAsync(
                "the sweep to arm its interval",
                _ => Task.FromResult(clock.PendingTimers > 0),
                TimeSpan.FromSeconds(5),
                TimeSpan.FromMilliseconds(20),
                CancellationToken.None);

            // The process stops while a round is running: the round's own call is cancelled, and
            // the loop ends quietly — a host that is shutting down is not a failure of its sweep.
            clock.Advance(TimeSpan.FromMinutes(1));
            await sweeper.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);

            Assert.True(sweeper.ExecuteTask!.IsCompletedSuccessfully);
        }
        finally
        {
            await sweeper.StopAsync(CancellationToken.None);
            sweeper.Dispose();
        }
    }

    /// <summary>The record store of a process that stops in the middle of a sweep round.</summary>
    internal sealed class StoppingStore(CancellationTokenSource stopping) : IUploadStore
    {
        /// <inheritdoc />
        public Task<IReadOnlyList<ExpiredUpload>> ExpireOverdueAsync(
            DateTimeOffset now,
            int batchSize,
            CancellationToken cancellationToken)
        {
            stopping.Cancel();
            return Task.FromException<IReadOnlyList<ExpiredUpload>>(new OperationCanceledException(cancellationToken));
        }

        /// <inheritdoc />
        public Task CreateAsync(UploadRecord record, CancellationToken cancellationToken) =>
            throw new NotSupportedException("the sweep creates nothing");

        /// <inheritdoc />
        public Task<UploadRecord?> FindAsync(string uploadId, string consumerId, CancellationToken cancellationToken) =>
            throw new NotSupportedException("the sweep looks nothing up by caller");

        /// <inheritdoc />
        public Task<bool> TryStartTransferAsync(
            string uploadId,
            string consumerId,
            UploadContentEncoding encoding,
            DateTimeOffset now,
            CancellationToken cancellationToken) => throw new NotSupportedException("the sweep starts no transfer");

        /// <inheritdoc />
        public Task<bool> RecordTransferAsync(
            string uploadId,
            UploadContentOutcome outcome,
            long receivedBytes,
            long? stagingSize,
            string? stagingSha256,
            DateTimeOffset now,
            CancellationToken cancellationToken) => throw new NotSupportedException("the sweep records no transfer");

        /// <inheritdoc />
        public Task<bool> RejectTransferAsync(
            string uploadId,
            UploadRejectionCode code,
            UploadRejectionReason reason,
            long receivedBytes,
            DateTimeOffset now,
            CancellationToken cancellationToken) => throw new NotSupportedException("the sweep rejects no transfer");

        /// <inheritdoc />
        public Task<bool> ConfirmAsync(
            string uploadId,
            string consumerId,
            string? declaredSha256,
            DateTimeOffset completedAt,
            DateTimeOffset now,
            DateTimeOffset verificationDeadline,
            CancellationToken cancellationToken) => throw new NotSupportedException("the sweep confirms nothing");

        /// <inheritdoc />
        public Task<UploadExpiryOutcome> ExpireIfOverdueAsync(
            string uploadId,
            DateTimeOffset now,
            CancellationToken cancellationToken) => throw new NotSupportedException("the sweep expires one upload through its batch");

        /// <inheritdoc />
        public Task<UploadRecord?> LoadForVerificationAsync(string uploadId, CancellationToken cancellationToken) =>
            throw new NotSupportedException("the sweep verifies nothing");

        /// <inheritdoc />
        public Task<SnapshotCommitResult> CommitReadyAsync(
            SnapshotRecord snapshot,
            DateTimeOffset now,
            CancellationToken cancellationToken) => throw new NotSupportedException("the sweep commits no snapshot");

        /// <inheritdoc />
        public Task<bool> CommitRejectedAsync(
            string uploadId,
            UploadRejectionCode code,
            UploadRejectionReason reason,
            DateTimeOffset now,
            CancellationToken cancellationToken) => throw new NotSupportedException("the sweep rejects nothing");
    }

    /// <summary>The storage port, recording what it was asked to delete and failing when told to.</summary>
    internal sealed class RecordingObjectStore : IObjectStore
    {
        private readonly List<ObjectKey> _deleted = [];

        /// <summary>The keys this store was asked to delete, in order.</summary>
        public IReadOnlyList<ObjectKey> Deleted
        {
            get
            {
                lock (_deleted)
                {
                    return [.. _deleted];
                }
            }
        }

        /// <inheritdoc />
        public StorageCapabilities Capabilities => StorageCapabilities.None;

        /// <summary>Whether a deletion answers with a storage failure instead of succeeding.</summary>
        public bool ThrowOnDelete { get; set; }

        /// <inheritdoc />
        public Task<StorageResult<ObjectWrite>> WriteAsync(ObjectKey key, Stream content, CancellationToken cancellationToken) =>
            throw new NotSupportedException("the sweep writes nothing");

        /// <inheritdoc />
        public Task<StorageResult<VerifiedRead>> OpenReadAsync(
            ObjectKey key,
            long expectedSize,
            string expectedSha256,
            CancellationToken cancellationToken) => throw new NotSupportedException("the sweep reads nothing");

        /// <inheritdoc />
        public Task<StorageResult<ObjectInfo>> GetInfoAsync(ObjectKey key, CancellationToken cancellationToken) =>
            throw new NotSupportedException("the sweep inspects nothing");

        /// <inheritdoc />
        public Task<StorageResult<bool>> DeleteAsync(ObjectKey key, CancellationToken cancellationToken)
        {
            if (ThrowOnDelete)
            {
                throw new StorageException(StorageOutcome.Transient, new StorageError(StorageErrorCodes.Unexpected, "the deletion failed."));
            }

            lock (_deleted)
            {
                _deleted.Add(key);
            }

            return Task.FromResult(StorageResult.Success(true));
        }

        /// <inheritdoc />
        public IAsyncEnumerable<ObjectInfo> ListAsync(string classPrefix, CancellationToken cancellationToken) =>
            throw new NotSupportedException("the sweep lists nothing");
    }
}
