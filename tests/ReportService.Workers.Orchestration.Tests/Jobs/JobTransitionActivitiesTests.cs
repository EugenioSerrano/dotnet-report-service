using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ReportService.Jobs;
using ReportService.ServiceDefaults.Telemetry;
using ReportService.Testing;
using ReportService.Workers.Orchestration.Jobs;
using ReportService.Workers.Orchestration.Telemetry;
using Temporalio.Testing;

namespace ReportService.Workers.Orchestration.Tests.Jobs;

/// <summary>
/// The states a job is moved forward from, the metric the first stage records, and the
/// step that closes a job from its outputs: the map holds exactly the states the path
/// passes through, the first stage records one value with no tag at all, and the
/// finalisation calls the store once with the job and the process's instant and lets a
/// store that cannot be reached answer for itself.
/// </summary>
public sealed class JobTransitionActivitiesTests
{
    private const string ReportJobId = "job-0123456789abcdef0123456789abcdef";

    private static readonly DateTimeOffset Now = new(2026, 11, 1, 6, 12, 30, 400, TimeSpan.Zero);

    private static readonly MeterIdentity Identity = MeterIdentity.ForHost("ReportService.Tests.JobTransitionMetrics");

    [Fact]
    public void ThePathOfAJobIsTheOnlyOneTheMapHolds()
    {
        Assert.Equal([ReportJobStatus.Queued], JobTransitionActivities.SourcesOf(ReportJobStatus.Validating));
        Assert.Equal([ReportJobStatus.Validating], JobTransitionActivities.SourcesOf(ReportJobStatus.Running));
    }

    [Fact]
    public void AStateThePathDoesNotMoveToHasNoSource()
    {
        // A job ends through the step that closes it, which writes the terminal state with
        // the outcome of its outputs; no transition of the path targets one.
        Assert.Empty(JobTransitionActivities.SourcesOf(ReportJobStatus.Succeeded));
        Assert.Empty(JobTransitionActivities.SourcesOf(ReportJobStatus.Failed));
        Assert.Empty(JobTransitionActivities.SourcesOf(ReportJobStatus.Partitioning));
        Assert.Empty(JobTransitionActivities.SourcesOf(ReportJobStatus.Finalizing));
        Assert.Empty(JobTransitionActivities.SourcesOf(ReportJobStatus.Queued));
        Assert.Empty(JobTransitionActivities.SourcesOf(ReportJobStatus.Cancelling));
        Assert.Empty(JobTransitionActivities.SourcesOf(ReportJobStatus.Cancelled));
    }

    [Fact]
    public async Task TheFirstStageRecordsOneValueWithNoTagAndLaterOutcomesRecordNone()
    {
        var store = new RecordingJobStore { CreatedAt = Now.AddSeconds(-7) };
        using var metrics = new OrchestrationMetrics(Identity);
        using var measurements = new DoubleMeasurements(Identity);
        var activity = new JobTransitionActivities(
            store,
            new FixedTimeProvider(Now),
            metrics,
            NullLoggerFactory.Instance,
            NullLogger<JobTransitionActivities>.Instance);

        // The first move out of queued is the one the histogram measures.
        Assert.Equal(
            TransitionOutcome.Applied,
            await RunAsync(() => activity.TransitionJobAsync(new TransitionJobInput(ReportJobId, ReportJobStatus.Validating))));

        var recorded = Assert.Single(measurements.Captured);
        Assert.Equal(OrchestrationMetrics.QueueToFirstStageDurationInstrumentName, recorded.Name);
        Assert.Equal(7, recorded.Value);
        Assert.Empty(recorded.Tags);

        // A repeated move wrote nothing, and a later transition is not the first stage:
        // neither records a value, and the job identifier is never a tag.
        store.Outcome = TransitionOutcome.AlreadyApplied;
        await RunAsync(() => activity.TransitionJobAsync(new TransitionJobInput(ReportJobId, ReportJobStatus.Validating)));
        store.Outcome = TransitionOutcome.Applied;
        await RunAsync(() => activity.TransitionJobAsync(new TransitionJobInput(ReportJobId, ReportJobStatus.Running)));
        Assert.Single(measurements.Captured);
        Assert.DoesNotContain(
            measurements.Captured,
            measurement => measurement.Tags.Any(tag => tag.Value?.ToString() == ReportJobId));
    }

    [Fact]
    public async Task FinalizeJobCallsTheStoreOnceWithTheJobAndTheClockAndLogsTheOutcome()
    {
        var store = new RecordingJobStore();
        var logs = new LogCapture();
        using var loggerFactory = new LoggerFactory([logs]);
        using var metrics = new OrchestrationMetrics(Identity);
        var activity = new JobTransitionActivities(
            store,
            new FixedTimeProvider(Now),
            metrics,
            loggerFactory,
            loggerFactory.CreateLogger<JobTransitionActivities>());

        var result = await RunAsync(() => activity.FinalizeJobAsync(new FinalizeJobInput(ReportJobId)));

        Assert.Equal(JobFinalizationOutcome.Applied, result.Outcome);
        Assert.Equal(ReportJobStatus.Succeeded, result.Status);
        var called = Assert.Single(store.Finalizations);
        Assert.Equal(ReportJobId, called.ReportJobId);
        Assert.Equal(Now, called.Now);
        Assert.Contains(logs.Lines, line => line.Contains("finalization: Applied", StringComparison.Ordinal));
        Assert.Contains(logs.Lines, line => line.Contains("SUCCEEDED", StringComparison.Ordinal));

        // The outcome is a record, never a warning: nothing of this step logs at Warning.
        Assert.DoesNotContain(logs.Lines, line => line.StartsWith("Warning", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FinalizeJobSurfacesAStoreThatCannotBeReached()
    {
        var store = new RecordingJobStore
        {
            Failure = new JobStoreUnavailableException("08006", new TimeoutException("the server did not answer")),
        };
        using var metrics = new OrchestrationMetrics(Identity);
        var activity = new JobTransitionActivities(
            store,
            new FixedTimeProvider(Now),
            metrics,
            NullLoggerFactory.Instance,
            NullLogger<JobTransitionActivities>.Instance);

        await Assert.ThrowsAsync<JobStoreUnavailableException>(
            () => RunAsync(() => activity.FinalizeJobAsync(new FinalizeJobInput(ReportJobId))));
    }

    private static Task RunAsync(Func<Task> body) => new ActivityEnvironment().RunAsync(body);

    private static Task<T> RunAsync<T>(Func<Task<T>> body) => new ActivityEnvironment().RunAsync(body);

    // The store the activities call, recording what they asked for and answering what the
    // case set; every member these cases never reach refuses loudly if one is reached.
    private sealed class RecordingJobStore : IJobStore
    {
        public TransitionOutcome Outcome { get; set; } = TransitionOutcome.Applied;

        public DateTimeOffset? CreatedAt { get; set; }

        public Exception? Failure { get; set; }

        public List<(string ReportJobId, DateTimeOffset Now)> Finalizations { get; } = [];

        public Task<TransitionJobResult> TransitionJobAsync(
            string reportJobId,
            IReadOnlyList<ReportJobStatus> from,
            ReportJobStatus to,
            DateTimeOffset now,
            CancellationToken cancellationToken) =>
            Task.FromResult(new TransitionJobResult(Outcome, Outcome == TransitionOutcome.Applied ? CreatedAt : null));

        public Task<JobFinalizationResult> FinalizeJobAsync(string reportJobId, DateTimeOffset now, CancellationToken cancellationToken)
        {
            if (Failure is not null)
            {
                throw Failure;
            }

            Finalizations.Add((reportJobId, now));
            return Task.FromResult(new JobFinalizationResult(JobFinalizationOutcome.Applied, ReportJobStatus.Succeeded));
        }

        public Task<JobAcceptanceResult> AcceptAsync(JobAcceptance acceptance, DateTimeOffset now, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<JobStateRecord?> FindForWorkflowAsync(string reportJobId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<TemplateReferenceRecord?> FindTemplateReferenceAsync(string reportJobId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<JobFailureOutcome> FailJobAsync(string reportJobId, string code, string reason, DateTimeOffset now, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<BeginInputWaitOutcome> BeginInputWaitAsync(string reportJobId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<EndInputWaitOutcome> EndInputWaitAsync(string reportJobId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<string>> FindInputWaitingJobIdsAsync(string uploadId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<TransitionOutcome> TransitionOutputAsync(string outputId, DateTimeOffset now, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<PartitionCommitResult> CommitPartitionAsync(PartitionRecord partition, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<PartitionRecord?> FindPartitionAsync(string reportJobId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<PartCommitResult> CommitPartAsync(PartCommit commit, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<PartCommit?> FindPartAsync(string outputId, int blockIndex, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<PublishResult> PublishAsync(ArtifactRecord artifact, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<OutputFailureOutcome> FailOutputAsync(string outputId, string code, DateTimeOffset now, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<ReportJobStatusView?> FindStatusAsync(string reportJobId, string consumerId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<ProgressWriteOutcome> RecordRecordsReadAsync(string reportJobId, long recordsRead, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    // What the product's meter published while the case ran: only its own identity is
    // listened to, and only the histogram's measurements are captured.
    private sealed class DoubleMeasurements : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly Lock _gate = new();
        private readonly List<Measurement> _captured = [];

        public DoubleMeasurements(MeterIdentity identity)
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (string.Equals(instrument.Meter.Name, identity.Name, StringComparison.Ordinal))
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<double>((instrument, value, tags, state) =>
            {
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

        public sealed record Measurement(string Name, double Value, KeyValuePair<string, object?>[] Tags);
    }
}
