using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using ReportService.Storage;
using ReportService.Testing;
using ReportService.Uploads;
using ReportService.Workers.Orchestration.Telemetry;
using ReportService.Workers.Orchestration.Uploads;
using Temporalio.Activities;
using Temporalio.Testing;

namespace ReportService.Workers.Orchestration.Tests;

/// <summary>
/// The activity the workflow runs, exercised in the engine's own host for one
/// attempt: two attempts of one verification write their copies under two
/// different keys and one attempt always under the same one; a verification that
/// takes time keeps telling the engine it is alive; and an attempt cancelled while
/// it reads stops with the cancellation and leaves the upload exactly as it was —
/// no copy written, nothing committed.
/// </summary>
public sealed class VerifyUploadActivityTests
{
    private const string UploadId = "upload-0123456789abcdef0123456789abcdef";

    private const string ConsumerId = "dev-consumer";

    private static readonly DateTimeOffset CreatedAt = new(2026, 11, 1, 4, 39, 23, 123, TimeSpan.Zero);

    private static readonly DateTimeOffset ConfirmedAt = new(2026, 11, 1, 4, 41, 2, 456, TimeSpan.Zero);

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    /// <summary>How long a cancelled attempt is given to end before the case says it did not.</summary>
    private static readonly TimeSpan StopDeadline = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task TwoAttemptsOfOneVerificationWriteUnderTwoDifferentKeys()
    {
        // Two attempts of the same verification: each is its own upload record in the
        // state a confirmation leaves, as a retried attempt would find it, and each is
        // run with its own attempt number.
        using var firstAttempt = await Rig.CreateAsync(Payload(4096));
        using var secondAttempt = await Rig.CreateAsync(Payload(4096));

        var firstOutcome = await firstAttempt.RunAsync(attempt: 1);
        var secondOutcome = await secondAttempt.RunAsync(attempt: 2);

        Assert.Equal(VerificationResult.Ready, firstOutcome.Outcome);
        Assert.Equal(VerificationResult.Ready, secondOutcome.Outcome);
        Assert.Single(firstAttempt.Objects.Written);
        Assert.Single(secondAttempt.Objects.Written);
        Assert.NotEqual(firstAttempt.Objects.Written[0], secondAttempt.Objects.Written[0]);
        Assert.All(firstAttempt.Objects.Written, key => Assert.StartsWith($"snapshots/{UploadId}/", key, StringComparison.Ordinal));
        Assert.All(secondAttempt.Objects.Written, key => Assert.StartsWith($"snapshots/{UploadId}/", key, StringComparison.Ordinal));
        Assert.Equal(UploadVerificationActivities.AttemptKey(UploadId, firstAttempt.Info(1)).ToString(), firstAttempt.Objects.Written[0]);
        Assert.Equal(UploadVerificationActivities.AttemptKey(UploadId, secondAttempt.Info(2)).ToString(), secondAttempt.Objects.Written[0]);
    }

    [Fact]
    public async Task TheSameAttemptAlwaysWritesUnderTheSameKey()
    {
        using var first = await Rig.CreateAsync(Payload(4096));
        using var second = await Rig.CreateAsync(Payload(4096));

        await first.RunAsync(attempt: 3);
        await second.RunAsync(attempt: 3);

        Assert.Single(first.Objects.Written);
        Assert.Single(second.Objects.Written);
        Assert.Equal(first.Objects.Written[0], second.Objects.Written[0]);
        Assert.Equal(UploadVerificationActivities.AttemptKey(UploadId, first.Info(3)).ToString(), first.Objects.Written[0]);
    }

    [Fact]
    public async Task ASlowVerificationKeepsTellingTheEngineItIsAlive()
    {
        using var rig = await Rig.CreateAsync(Payload(4096), gated: true);
        var running = rig.StartAsync(attempt: 1);

        await rig.Objects.EnteredRead.Task.WaitAsync(Cancellation);
        Assert.True(
            rig.Heartbeats >= 1,
            "the attempt has to tell the engine it is still working while the verification is still reading, and no heartbeat had arrived.");

        rig.Objects.Resume();
        var outcome = await running;

        Assert.Equal(VerificationResult.Ready, outcome.Outcome);
        Assert.True(rig.Heartbeats >= 1);
    }

    [Fact]
    public async Task CancellingTheAttemptStopsItAndCommitsNothing()
    {
        using var rig = await Rig.CreateAsync(Payload(4096), gated: true);
        var running = rig.StartAsync(attempt: 1);
        await rig.Objects.EnteredRead.Task.WaitAsync(Cancellation);

        try
        {
            rig.Environment.Cancel();
            var stopped = await Task.WhenAny(running, Task.Delay(StopDeadline, Cancellation));
            Assert.True(
                stopped == running,
                "a cancelled attempt has to end; this one was still reading after the cancellation was asked for.");
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        }
        finally
        {
            rig.Objects.Resume();
        }

        var record = await rig.Uploads.FindAsync(UploadId, ConsumerId, Cancellation);
        Assert.NotNull(record);
        Assert.Equal(UploadStatus.Verifying, record.Status);
        Assert.Null(record.Snapshot);
        Assert.Null(rig.Uploads.SnapshotOf(UploadId));
        Assert.Empty(rig.Objects.Written);
    }

    private static byte[] Payload(int bytes)
    {
        var content = new byte[bytes];
        for (var index = 0; index < content.Length; index++)
        {
            content[index] = (byte)(0x5A ^ (index & 0xFF) ^ (index >> 8));
        }

        return content;
    }

    /// <summary>
    /// One attempt of one verification: an upload recorded as the API leaves it
    /// when it is confirmed, the storage port behind a store the case controls, the
    /// verifier the activity uses, and the engine's host around it, configured with
    /// the attempt the case asks for and a callback counting the heartbeats.
    /// </summary>
    private sealed class Rig : IDisposable
    {
        private readonly OrchestrationMetrics _metrics = new();
        private ActivityEnvironment? _environment;
        private int _heartbeats;

        private Rig(byte[] content)
        {
            Clock = new ManualTimeProvider(CreatedAt);
            Objects = new GatedObjectStore(Clock, content);
            Uploads = new InMemoryUploadStore();
            Verifier = new UploadVerifier(
                Uploads,
                Objects,
                new VerificationOptions { MaxStoredBytes = 8 << 20, MaxUncompressedBytes = 8 << 20 },
                Clock,
                NullLogger<UploadVerifier>.Instance,
                _metrics);
            Activities = new UploadVerificationActivities(Verifier, Clock);
        }

        public ManualTimeProvider Clock { get; }

        public GatedObjectStore Objects { get; }

        public InMemoryUploadStore Uploads { get; }

        public UploadVerifier Verifier { get; }

        public UploadVerificationActivities Activities { get; }

        /// <summary>The host of the attempt that was started last.</summary>
        public ActivityEnvironment Environment => _environment
            ?? throw new InvalidOperationException("no attempt has been started.");

        public int Heartbeats => Volatile.Read(ref _heartbeats);

        public static async Task<Rig> CreateAsync(byte[] content, bool gated = false)
        {
            var rig = new Rig(content);
            rig.Objects.Gated = gated;
            var cancellationToken = Cancellation;
            var record = new UploadRecord(
                UploadId,
                ConsumerId,
                UploadMode.ApiStream,
                DataProfile.HeaderItemsV1,
                UploadStatus.Created,
                ObjectKey.FromSegments("staging", UploadId, "content"),
                CreatedAt,
                CreatedAt.AddDays(1));
            await rig.Uploads.CreateAsync(record, cancellationToken);
            await rig.Uploads.TryStartTransferAsync(UploadId, ConsumerId, UploadContentEncoding.None, cancellationToken);
            await rig.Uploads.RecordTransferAsync(
                UploadId,
                UploadContentOutcome.Complete,
                content.Length,
                content.Length,
                Convert.ToHexStringLower(SHA256.HashData(content)),
                cancellationToken);
            await rig.Uploads.ConfirmAsync(UploadId, ConsumerId, null, ConfirmedAt, cancellationToken);
            return rig;
        }

        /// <summary>The engine's description of one attempt of this upload's verification.</summary>
        /// <param name="attempt">The attempt number, from one.</param>
        public ActivityInfo Info(int attempt) => ActivityEnvironment.DefaultInfo with
        {
            WorkflowId = $"upload/{UploadId}",
            WorkflowRunId = "run-0123456789abcdef",
            WorkflowType = UploadVerificationWorkflow.WorkflowType,
            ActivityId = "1",
            ActivityType = UploadVerificationActivities.Name,
            Attempt = attempt,
            TaskQueue = UploadVerificationActivityOptions.DataQueue,
        };

        /// <summary>Runs one attempt inside the engine's host and waits for its result.</summary>
        /// <param name="attempt">The attempt number, from one.</param>
        public Task<VerifyUploadResult> RunAsync(int attempt) => StartAsync(attempt);

        /// <summary>Starts one attempt inside the engine's host, waiting for nothing.</summary>
        /// <param name="attempt">The attempt number, from one.</param>
        public Task<VerifyUploadResult> StartAsync(int attempt)
        {
            _environment = new ActivityEnvironment
            {
                Info = Info(attempt),
                Heartbeater = _ => Interlocked.Increment(ref _heartbeats),
            };
            return Environment.RunAsync(() => Activities.VerifyUploadAsync(new VerifyUploadInput(UploadId)));
        }

        public void Dispose() => _metrics.Dispose();
    }

    /// <summary>
    /// The storage port over one object held in memory: the staging object is read
    /// back exactly as recorded, and a write is accepted only once its source has
    /// been read to its end, so a cancelled read leaves nothing written. A gated
    /// read stops on its first block, moves the process's clock forward — which is
    /// what fires the activity's own keep-alive — and waits to be let go.
    /// </summary>
    private sealed class GatedObjectStore(ManualTimeProvider clock, byte[] content) : IObjectStore
    {
        private byte[] _content = content;

        public bool Gated { get; set; }

        public TaskCompletionSource EnteredRead { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<string> Written { get; } = [];

        private TaskCompletionSource Resumed { get; set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public StorageCapabilities Capabilities => StorageCapabilities.None;

        public void Resume() => Resumed.TrySetResult();

        public Task<StorageResult<ObjectWrite>> WriteAsync(ObjectKey key, Stream contentStream, CancellationToken cancellationToken) =>
            WriteCoreAsync(key, contentStream, cancellationToken);

        public Task<StorageResult<VerifiedRead>> OpenReadAsync(
            ObjectKey key,
            long expectedSize,
            string expectedSha256,
            CancellationToken cancellationToken)
        {
            EnteredRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Resumed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var read = new VerifiedRead(new GatedContent(this, _content, clock), expectedSize, expectedSha256);
            return Task.FromResult(StorageResult.Success(read));
        }

        public Task<StorageResult<ObjectInfo>> GetInfoAsync(ObjectKey key, CancellationToken cancellationToken) =>
            Task.FromResult(StorageResult.Success(new ObjectInfo(key, _content.Length, DateTimeOffset.UnixEpoch)));

        public Task<StorageResult<bool>> DeleteAsync(ObjectKey key, CancellationToken cancellationToken) =>
            Task.FromResult(StorageResult.Success(true));

        public async IAsyncEnumerable<ObjectInfo> ListAsync(
            string classPrefix,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            yield break;
        }

        private async Task<StorageResult<ObjectWrite>> WriteCoreAsync(
            ObjectKey key,
            Stream source,
            CancellationToken cancellationToken)
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[64 * 1024];
            long count = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
            {
                hash.AppendData(buffer, 0, read);
                count += read;
            }

            Written.Add(key.ToString());
            return StorageResult.Success(new ObjectWrite(key, count, Convert.ToHexStringLower(hash.GetHashAndReset()), null));
        }

        private sealed class GatedContent(GatedObjectStore owner, byte[] content, ManualTimeProvider clock) : Stream
        {
            private int _position;

            private bool _entered;

            public override bool CanRead => true;

            public override bool CanSeek => false;

            public override bool CanWrite => false;

            public override long Length => content.Length;

            public override long Position
            {
                get => _position;
                set => throw new NotSupportedException("the stored object is read forward only.");
            }

            public override int Read(byte[] buffer, int offset, int count) =>
                Read(buffer.AsSpan(offset, count));

            public override int Read(Span<byte> buffer) => Take(buffer);

            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                if (owner.Gated && !_entered)
                {
                    _entered = true;

                    // Time passes while the object is still being read: the activity's own
                    // keep-alive is driven by this clock, so it has to fire here, inside the
                    // attempt, and not after the read finished.
                    clock.Advance(UploadVerificationActivities.HeartbeatInterval);
                    owner.EnteredRead.TrySetResult();
                    await owner.Resumed.Task.WaitAsync(cancellationToken);
                }

                return Take(buffer.Span);
            }

            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
                ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

            public override void Flush()
            {
            }

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException("the stored object is read forward only.");

            public override void SetLength(long value) => throw new NotSupportedException("the stored object is read only.");

            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException("the stored object is read only.");

            private int Take(Span<byte> buffer)
            {
                var take = Math.Min(buffer.Length, content.Length - _position);
                if (take <= 0)
                {
                    return 0;
                }

                content.AsSpan(_position, take).CopyTo(buffer);
                _position += take;
                return take;
            }
        }
    }
}
