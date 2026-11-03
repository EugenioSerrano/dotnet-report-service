using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;
using ReportService.Jobs;
using ReportService.ServiceDefaults.Telemetry;
using ReportService.Storage;
using ReportService.Testing;
using ReportService.Uploads;
using ReportService.Workers.Orchestration.Jobs;
using ReportService.Workers.Orchestration.Telemetry;
using Temporalio.Testing;

namespace ReportService.Workers.Orchestration.Tests.Jobs;

/// <summary>
/// The step that tells the jobs waiting on an upload that its state changed: it asks the
/// record store which jobs of the upload may be waiting, nudges each of them once through
/// the seam, and answers how many it reached. A job whose execution does not exist yet, or
/// that has already closed, is not an error — the nudge is a courtesy and the wait has its
/// own timer — so the step logs it, skips it and keeps going; anything else the engine
/// answers fails the attempt, which the engine retries.
/// </summary>
public sealed class InputWaitSignalActivitiesTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 5, 0, 0, TimeSpan.Zero);

    private const string Consumer = "dev-consumer";

    [Fact]
    public async Task EveryWaitingJobOfTheUploadIsNudgedInTheOrderItWasAccepted()
    {
        using var rig = await Rig.CreateAsync();
        var first = await rig.AcceptAsync(Now);
        var second = await rig.AcceptAsync(Now.AddSeconds(1));
        var failed = await rig.AcceptAsync(Now.AddSeconds(2));
        await rig.Jobs.BeginInputWaitAsync(first, TestContext.Current.CancellationToken);
        Assert.Equal(
            JobFailureOutcome.Applied,
            await rig.Jobs.FailJobAsync(failed, "INPUT_NOT_FOUND", "input-expired", Now, TestContext.Current.CancellationToken));
        var otherUpload = await rig.AcceptAsync(Now.AddSeconds(3), uploadId: Rig.OtherUploadId);

        var signalled = await rig.SignalAsync();

        Assert.Equal(2, signalled);
        Assert.Equal(
            [ReportJobWorkflow.WorkflowIdFor(first), ReportJobWorkflow.WorkflowIdFor(second)],
            rig.Signaller.Signalled);
        Assert.DoesNotContain(ReportJobWorkflow.WorkflowIdFor(failed), rig.Signaller.Signalled);
        Assert.DoesNotContain(ReportJobWorkflow.WorkflowIdFor(otherUpload), rig.Signaller.Signalled);
        Assert.Equal(1, rig.Store.Queries);
        Assert.Contains(rig.Logs.Lines, line => line.EndsWith("Upload " + Rig.UploadId + ": 2 waiting job(s) notified", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnUploadWithNothingWaitingNotifiesNoOne()
    {
        using var rig = await Rig.CreateAsync();

        var signalled = await rig.SignalAsync();

        Assert.Equal(0, signalled);
        Assert.Empty(rig.Signaller.Signalled);
        Assert.Equal(1, rig.Store.Queries);
        Assert.Contains(rig.Logs.Lines, line => line.EndsWith("Upload " + Rig.UploadId + ": 0 waiting job(s) notified", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AJobWithoutAnExecutionIsSkippedAndTheOthersAreStillNudged()
    {
        using var rig = await Rig.CreateAsync();
        var absent = await rig.AcceptAsync(Now);
        var waiting = await rig.AcceptAsync(Now.AddSeconds(1));
        await rig.Jobs.BeginInputWaitAsync(waiting, TestContext.Current.CancellationToken);
        rig.Signaller.Absent.Add(ReportJobWorkflow.WorkflowIdFor(absent));

        var signalled = await rig.SignalAsync();

        Assert.Equal(1, signalled);
        Assert.Equal([ReportJobWorkflow.WorkflowIdFor(waiting)], rig.Signaller.Signalled);
        Assert.Contains(
            rig.Logs.Lines,
            line => line.StartsWith("Debug: ", StringComparison.Ordinal)
                && line.Contains(absent, StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnEngineFailureFailsTheAttemptSoItIsRetried()
    {
        using var rig = await Rig.CreateAsync();
        await rig.AcceptAsync(Now);
        rig.Signaller.Failure = new InvalidOperationException("the engine is unreachable");

        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.RunAsync());

        Assert.Equal("failed", Assert.Single(rig.Measurements.Captured, measurement => measurement.Name == OrchestrationMetrics.InputWaitNotificationsInstrumentName).Tag("result"));
    }

    [Fact]
    public async Task EveryNudgeIsCountedWithAConstantResult()
    {
        using var rig = await Rig.CreateAsync();
        var waiting = await rig.AcceptAsync(Now);
        var absent = await rig.AcceptAsync(Now.AddSeconds(1));
        await rig.Jobs.BeginInputWaitAsync(waiting, TestContext.Current.CancellationToken);
        rig.Signaller.Absent.Add(ReportJobWorkflow.WorkflowIdFor(absent));

        await rig.SignalAsync();

        var counts = rig.Measurements.Captured
            .Where(measurement => measurement.Name == OrchestrationMetrics.InputWaitNotificationsInstrumentName)
            .ToList();
        Assert.Equal(2, counts.Count);
        Assert.Equal(1L, Assert.Single(counts, measurement => Equals(measurement.Tag("result"), "signalled")).Value);
        Assert.Equal(1L, Assert.Single(counts, measurement => Equals(measurement.Tag("result"), "skipped")).Value);
        Assert.DoesNotContain(counts, measurement => measurement.Tags.Any(tag => tag.Value is Rig.UploadId || tag.Value is string value && value.StartsWith("job-", StringComparison.Ordinal)));
    }

    /// <summary>An upload, the jobs accepted on it, and the step over the two stores and the seam.</summary>
    private sealed class Rig : IDisposable
    {
        private readonly InMemoryUploadStore _uploads;
        private readonly OrchestrationMetrics _metrics = new(MeterIdentity.ForHost(TelemetryDefaults.MeterName));
        private readonly ILoggerFactory _loggers;
        private readonly ActivityEnvironment _environment = new();

        private Rig(InMemoryUploadStore uploads, InMemoryJobStore jobs, CountingStore store, LogCapture logs, ILoggerFactory loggers)
        {
            _uploads = uploads;
            Jobs = jobs;
            Store = store;
            Logs = logs;
            _loggers = loggers;
            Signaller = new RecordingSignaller();
            Activities = new InputWaitSignalActivities(store, Signaller, _metrics, loggers.CreateLogger<InputWaitSignalActivities>());
        }

        public const string UploadId = "upload-0000000000000000000000000000000a";

        public const string OtherUploadId = "upload-0000000000000000000000000000000b";

        public InMemoryJobStore Jobs { get; }

        public CountingStore Store { get; }

        public RecordingSignaller Signaller { get; }

        public LogCapture Logs { get; }

        public InputWaitSignalActivities Activities { get; }

        public Measurements Measurements { get; } = new();

        public static async Task<Rig> CreateAsync()
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var uploads = new InMemoryUploadStore();
            var jobs = new InMemoryJobStore(uploads);
            foreach (var uploadId in new[] { UploadId, OtherUploadId })
            {
                await uploads.CreateAsync(
                    new UploadRecord(
                        uploadId,
                        Consumer,
                        UploadMode.ApiStream,
                        DataProfile.HeaderItemsV1,
                        UploadStatus.Created,
                        ObjectKey.FromSegments("staging", uploadId, "content"),
                        Now.AddHours(-1),
                        Now.AddHours(1)),
                    cancellationToken);
                await uploads.TryStartTransferAsync(uploadId, Consumer, UploadContentEncoding.None, Now, cancellationToken);
                await uploads.RecordTransferAsync(uploadId, UploadContentOutcome.Complete, 900, 800, new string('a', 64), Now, cancellationToken);
                await uploads.ConfirmAsync(uploadId, Consumer, new string('a', 64), Now, Now, Now.AddHours(6), cancellationToken);
            }

            var logs = new LogCapture();
            var loggers = LoggerFactory.Create(builder => builder.AddProvider(logs).SetMinimumLevel(LogLevel.Debug));
            return new Rig(uploads, jobs, new CountingStore(jobs), logs, loggers);
        }

        public async Task<string> AcceptAsync(DateTimeOffset at, string uploadId = UploadId)
        {
            var acceptance = new JobAcceptance(
                JobIdentifier.Create(),
                JobIdentifier.CreateOutput(),
                Consumer,
                uploadId,
                "pilot-grouped",
                TemplateReferenceForm.Exact,
                "1.0.0");
            Assert.IsType<JobAcceptanceResult.Accepted>(await Jobs.AcceptAsync(acceptance, at, TestContext.Current.CancellationToken));
            return acceptance.ReportJobId;
        }

        public Task<int> SignalAsync() => _environment.RunAsync(() => Activities.SignalAsync(new SignalWaitingJobsInput(UploadId)));

        public Task RunAsync() => _environment.RunAsync(() => Activities.SignalAsync(new SignalWaitingJobsInput(UploadId)));

        public void Dispose()
        {
            _ = _uploads;
            _loggers.Dispose();
            _metrics.Dispose();
            Measurements.Dispose();
        }
    }

    /// <summary>The record store, wrapped so a case can count the queries the step makes.</summary>
    private sealed class CountingStore(IJobStore inner) : IJobStore
    {
        public int Queries { get; private set; }

        public Task<IReadOnlyList<string>> FindInputWaitingJobIdsAsync(string uploadId, CancellationToken cancellationToken)
        {
            Queries++;
            return inner.FindInputWaitingJobIdsAsync(uploadId, cancellationToken);
        }

        public Task<JobAcceptanceResult> AcceptAsync(JobAcceptance acceptance, DateTimeOffset now, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<JobStateRecord?> FindForWorkflowAsync(string reportJobId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<JobFailureOutcome> FailJobAsync(string reportJobId, string code, string reason, DateTimeOffset now, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<BeginInputWaitOutcome> BeginInputWaitAsync(string reportJobId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<EndInputWaitOutcome> EndInputWaitAsync(string reportJobId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<TransitionOutcome> TransitionJobAsync(string reportJobId, IReadOnlyList<ReportJobStatus> from, ReportJobStatus to, DateTimeOffset now, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<TransitionOutcome> TransitionOutputAsync(string outputId, DateTimeOffset now, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<PartitionCommitResult> CommitPartitionAsync(PartitionRecord partition, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<PartitionRecord?> FindPartitionAsync(string reportJobId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<PartCommitResult> CommitPartAsync(PartCommit commit, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<PartCommit?> FindPartAsync(string outputId, int blockIndex, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<PublishResult> PublishAsync(ArtifactRecord artifact, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<OutputFailureOutcome> FailOutputAsync(string outputId, string code, DateTimeOffset now, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<JobFinalizationResult> FinalizeJobAsync(string reportJobId, DateTimeOffset now, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    /// <summary>The seam, doubled: it remembers the executions it nudged and the ones the engine says do not exist.</summary>
    private sealed class RecordingSignaller : IWorkflowSignaller
    {
        public List<string> Signalled { get; } = [];

        public HashSet<string> Absent { get; } = new(StringComparer.Ordinal);

        public Exception? Failure { get; set; }

        public Task<bool> SignalInputChangedAsync(string workflowId, CancellationToken cancellationToken)
        {
            if (Failure is not null)
            {
                throw Failure;
            }

            if (Absent.Contains(workflowId))
            {
                return Task.FromResult(false);
            }

            Signalled.Add(workflowId);
            return Task.FromResult(true);
        }
    }

    /// <summary>What the product's meter published while the case ran.</summary>
    private sealed class Measurements : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly Lock _gate = new();
        private readonly List<Measurement> _captured = [];

        public Measurements()
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (string.Equals(instrument.Meter.Name, TelemetryDefaults.MeterName, StringComparison.Ordinal))
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>((instrument, value, tags, state) =>
            {
                // The product's meter is process-wide and other cases record on it while this one
                // runs, on other threads: the capture is taken under a lock and read as a snapshot.
                lock (_gate)
                {
                    _captured.Add(new Measurement(instrument.Name, value, tags.ToArray()));
                }
            });
            _listener.Start();
        }

        public IReadOnlyList<Measurement> Captured
        {
            get
            {
                lock (_gate)
                {
                    return [.. _captured];
                }
            }
        }

        public void Dispose() => _listener.Dispose();

        public sealed record Measurement(string Name, long Value, KeyValuePair<string, object?>[] Tags)
        {
            public object? Tag(string name) =>
                Tags.Where(tag => string.Equals(tag.Key, name, StringComparison.Ordinal)).Select(tag => tag.Value).FirstOrDefault();
        }
    }
}
