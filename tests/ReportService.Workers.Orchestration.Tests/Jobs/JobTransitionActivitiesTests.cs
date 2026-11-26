using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ReportService.Jobs;
using ReportService.TemplatePackages.Manifest;
using ReportService.TemplatePackages.Records;
using ReportService.ServiceDefaults;
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

    /// <summary>
    /// ADR-012 §3.11(5): the cancellation latency is observed once, when the finalisation that
    /// wrote the closure reports the instant the cancellation was requested. The elapsed time is
    /// recorded with no tag at all — the job's identifier is never a metric dimension (§3.11(4)) —
    /// and a retried finalisation, which finds the job terminal, adds nothing.
    /// </summary>
    [Fact]
    public async Task ACancelledFinalisationRecordsOneLatencyObservationWithNoTag()
    {
        var store = new RecordingJobStore
        {
            Finalization = new JobFinalizationResult(JobFinalizationOutcome.Applied, ReportJobStatus.Cancelled)
            {
                CancellationRequestedAt = Now.AddSeconds(-7),
            },
        };
        using var metrics = new OrchestrationMetrics(Identity);
        using var measurements = new DoubleMeasurements(Identity);
        var activity = new JobTransitionActivities(
            store,
            new FixedTimeProvider(Now),
            metrics,
            NullLoggerFactory.Instance,
            NullLogger<JobTransitionActivities>.Instance);

        await RunAsync(() => activity.FinalizeJobAsync(new FinalizeJobInput(ReportJobId)));

        var recorded = Assert.Single(
            measurements.Captured,
            measurement => measurement.Name == OrchestrationMetrics.CancellationDurationInstrumentName);
        Assert.Equal(7, recorded.Value);
        Assert.Empty(recorded.Tags);

        // A repeated finalisation wrote nothing, so it observes nothing: one cancelled job is one
        // latency, never one per attempt.
        store.Finalization = new JobFinalizationResult(JobFinalizationOutcome.AlreadyTerminal, null);
        await RunAsync(() => activity.FinalizeJobAsync(new FinalizeJobInput(ReportJobId)));
        Assert.Single(
            measurements.Captured,
            measurement => measurement.Name == OrchestrationMetrics.CancellationDurationInstrumentName);
    }

    /// <summary>A job that was not cancelled records no cancellation latency at all.</summary>
    [Fact]
    public async Task ANonCancelledFinalisationRecordsNoLatencyObservation()
    {
        var store = new RecordingJobStore();
        using var metrics = new OrchestrationMetrics(Identity);
        using var measurements = new DoubleMeasurements(Identity);
        var activity = new JobTransitionActivities(
            store,
            new FixedTimeProvider(Now),
            metrics,
            NullLoggerFactory.Instance,
            NullLogger<JobTransitionActivities>.Instance);

        await RunAsync(() => activity.FinalizeJobAsync(new FinalizeJobInput(ReportJobId)));

        Assert.DoesNotContain(
            measurements.Captured,
            measurement => measurement.Name == OrchestrationMetrics.CancellationDurationInstrumentName);
    }

    private static Task RunAsync(Func<Task> body) => new ActivityEnvironment().RunAsync(body);

    [Fact]
    public async Task TheReasonOfARefusalReachesTheStoreAsTheOutputsOwnDiagnostic()
    {
        var store = new RecordingJobStore();
        using var metrics = new OrchestrationMetrics(Identity);
        var activity = new JobTransitionActivities(
            store,
            new FixedTimeProvider(Now),
            metrics,
            NullLoggerFactory.Instance,
            NullLogger<JobTransitionActivities>.Instance);
        const string Reason =
            "the document cannot be used: code=ROW_BOUND_EXCEEDED reason=field-too-large size=262145 bound=262144 limit=row-bound ordinal=1500 offset=48213 path=$.description";

        var outcome = await RunAsync(() => activity.FailOutputAsync(
            new FailOutputInput("output-0123456789abcdef0123456789abcdef", "ROW_BOUND_EXCEEDED", Reason)));

        Assert.Equal(OutputFailureOutcome.Applied, outcome);
        var failure = Assert.Single(store.OutputFailures);
        Assert.Equal("output-0123456789abcdef0123456789abcdef", failure.OutputId);
        Assert.Equal("ROW_BOUND_EXCEEDED", failure.Code);
        Assert.Equal(Reason, failure.Reason);
    }

    private static Task<T> RunAsync<T>(Func<Task<T>> body) => new ActivityEnvironment().RunAsync(body);

    // The store the activities call, recording what they asked for and answering what the
    // case set; every member these cases never reach refuses loudly if one is reached.
    private sealed class RecordingJobStore : IJobStore
    {
        public TransitionOutcome Outcome { get; set; } = TransitionOutcome.Applied;

        public DateTimeOffset? CreatedAt { get; set; }

        public Exception? Failure { get; set; }

        /// <summary>The closure the store answers with.</summary>
        public JobFinalizationResult Finalization { get; set; } =
            new(JobFinalizationOutcome.Applied, ReportJobStatus.Succeeded);

        public List<(string ReportJobId, DateTimeOffset Now)> Finalizations { get; } = [];

        /// <summary>The output failures the step asked for, in the order it asked.</summary>
        public List<(string OutputId, string Code, string? Reason)> OutputFailures { get; } = [];

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
            return Task.FromResult(Finalization);
        }

        public Task<JobAcceptanceResult> AcceptAsync(JobAcceptance acceptance, DateTimeOffset now, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IdempotencyLookup> FindByIdempotencyKeyAsync(
        string consumerId,
        string idempotencyKey,
        RequestFingerprint fingerprint,
        CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<JobStateRecord?> FindForWorkflowAsync(string reportJobId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<TemplateReferenceRecord?> FindTemplateReferenceAsync(string reportJobId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<JobFailureOutcome> FailJobAsync(string reportJobId, string code, string reason, string? message, DateTimeOffset now, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<TemplatePinState> PinTemplateAsync(string reportJobId, TemplatePin pin, DateTimeOffset now, CancellationToken cancellationToken, TemplateLocaleDefaults? presentation) => throw new NotSupportedException();

        public Task<BeginInputWaitOutcome> BeginInputWaitAsync(string reportJobId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<EndInputWaitOutcome> EndInputWaitAsync(string reportJobId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<string>> FindInputWaitingJobIdsAsync(string uploadId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<TransitionOutcome> TransitionOutputAsync(string outputId, DateTimeOffset now, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<PartitionCommitResult> CommitPartitionAsync(PartitionRecord partition, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<PartitionRecord?> FindPartitionAsync(string reportJobId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<PartCommitResult> CommitPartAsync(PartCommit commit, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<PartCommit?> FindPartAsync(string outputId, int blockIndex, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<PublishResult> PublishAsync(ArtifactRecord artifact, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<JobArtifactsView?> ListArtifactsAsync(string reportJobId, string consumerId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<ArtifactView?> FindArtifactAsync(string reportJobId, string artifactId, string consumerId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<OutputFailureOutcome> FailOutputAsync(string outputId, string code, string? reason, DateTimeOffset now, CancellationToken cancellationToken)
        {
            OutputFailures.Add((outputId, code, reason));
            return Task.FromResult(OutputFailureOutcome.Applied);
        }

        public Task<ReportJobStatusView?> FindStatusAsync(string reportJobId, string consumerId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<ProgressWriteOutcome> RecordRecordsReadAsync(string reportJobId, long recordsRead, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<AttemptCauseOutcome> RecordAttemptCauseAsync(AttemptCause cause, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<AttemptCause?> FindLastAttemptCauseAsync(string outputId, int blockIndex, CancellationToken cancellationToken) => throw new NotSupportedException();
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
