using System.Net;
using ReportService.Api.Http;
using ReportService.Api.Jobs;
using ReportService.Jobs;
using ReportService.TemplatePackages.Manifest;
using ReportService.TemplatePackages.Records;
using ReportService.Testing;
using ReportService.Uploads;

namespace ReportService.Api.Tests.Jobs;

/// <summary>
/// Requesting cancellation of a report job through the API's real pipeline, with the job and
/// upload records kept in memory. The oracles are the approved US-010 AC-1 and AC-6 and
/// ADR-012 §3.10 — never the implementation (G06): a cancel on a non-terminal job is accepted
/// (<c>202</c>), durably records exactly one <c>cancel-job</c> intent and moves the job to
/// <c>CANCELLING</c>; a repeat while <c>CANCELLING</c> is idempotent (no second intent, no
/// regress, <c>202</c>); a cancel on a terminal job is an idempotent no-op that returns the
/// current state (<c>200</c>) and records no intent; an unknown consumer is refused
/// (<c>403</c>); a foreign, absent or malformed identifier is the same not-found (<c>404</c>,
/// no existence leak); and a transport event (an aborted request token) never aborts the
/// cancel, which records regardless (ADR-012 §3.10, the decision uses <c>CancellationToken.None</c>).
/// </summary>
public sealed class CancelReportJobEndpointTests : IDisposable
{
    private const string AnotherConsumer = "another-consumer";

    // The durable-intent kind the cancel surface records, fixed by ADR-012 §3.2(6) / §3.10(1).
    // Asserted as the wire value so the oracle does not depend on a production constant's name.
    private const string CancelJobKind = "cancel-job";

    private const string Hash = "9ffcbf0acedfe4529a6093c4c669efab29c2d523a7794bacfb8eb8a946ca8dd3";

    private const string ArtifactId = "artifact-0123456789abcdef0123456789abcdef";

    private static readonly DateTimeOffset Now = JobWireValues.TruncateToMillisecond(JobApiHost.Now);

    private readonly JobApiHost _host = new();

    public void Dispose() => _host.Dispose();

    // ----- AC-1: a cancel on any non-terminal status → 202 + CANCELLING + exactly one durable intent.

    [Theory]
    [InlineData(ReportJobStatus.Queued)]
    [InlineData(ReportJobStatus.Validating)]
    [InlineData(ReportJobStatus.Partitioning)]
    [InlineData(ReportJobStatus.Running)]
    [InlineData(ReportJobStatus.Finalizing)]
    public async Task ACancelOnANonTerminalStatusAcceptsRecordsOneIntentAndMovesToCancelling(ReportJobStatus status)
    {
        var (jobId, _) = await AcceptedAsync();
        await PlaceInStatusAsync(jobId, status);

        using var response = await CancelAsync(JobApiHost.Consumer, jobId);
        var answer = await JobApiHost.AnswerAsync(response);

        // AC-1: the request is accepted, the job observably reports CANCELLING, and the current
        // state is returned in the body.
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal("cancelling", answer.GetProperty("status").GetString());
        Assert.Equal(jobId, answer.GetProperty("reportJobId").GetString());
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());

        // The cancel intent is durably recorded — exactly one row for this job (no duplication of
        // the start intent, no second cancel intent).
        Assert.Single(_host.Jobs.Intents.Where(intent =>
            intent.Kind == CancelJobKind && intent.ReportJobId == jobId));

        // A subsequent read of the job reports CANCELLING.
        using var read = await StatusAsync(JobApiHost.Consumer, jobId);
        Assert.Equal("cancelling", (await JobApiHost.AnswerAsync(read)).GetProperty("status").GetString());
    }

    // ----- AC-1: a repeat cancel while CANCELLING is idempotent — no second intent, no regress, 202.

    [Fact]
    public async Task ARepeatCancelWhileCancellingRecordsNoSecondIntentAndDoesNotRegress()
    {
        var (jobId, _) = await AcceptedAsync();

        using (var first = await CancelAsync(JobApiHost.Consumer, jobId))
        {
            Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
            Assert.Equal("cancelling", (await JobApiHost.AnswerAsync(first)).GetProperty("status").GetString());
        }

        using var repeat = await CancelAsync(JobApiHost.Consumer, jobId);
        var answer = await JobApiHost.AnswerAsync(repeat);

        // The idempotent replay: still accepted, the state has not regressed, and the current
        // state is returned.
        Assert.Equal(HttpStatusCode.Accepted, repeat.StatusCode);
        Assert.Equal("cancelling", answer.GetProperty("status").GetString());

        // No second cancel intent was recorded: exactly one remains.
        Assert.Single(_host.Jobs.Intents.Where(intent =>
            intent.Kind == CancelJobKind && intent.ReportJobId == jobId));
    }

    // ----- AC-6: a cancel on each terminal status is an idempotent no-op → 200, state unchanged, no intent.

    [Fact]
    public async Task ACancelOnEachTerminalStatusIsANoOpReturnsTheCurrentStateAndRecordsNoIntent()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        // SUCCEEDED: one output published and the job finalised.
        var (succeeded, succeededOutput) = await AcceptedAsync();
        await _host.Jobs.TransitionOutputAsync(succeededOutput, Now, cancellationToken);
        await _host.Jobs.PublishAsync(Artifact(succeeded, succeededOutput), cancellationToken);
        await _host.Jobs.FinalizeJobAsync(succeeded, Now.AddMinutes(1), cancellationToken);

        // FAILED: a job failed before its partition.
        var (failed, _) = await AcceptedAsync();
        await _host.Jobs.FailJobAsync(failed, "QUOTA_EXCEEDED", "block", Now, cancellationToken);

        // PARTIALLY_SUCCEEDED (ADR-048 R2): a succeeded PDF beside a failed HTML output.
        var (partial, partialOutput) = await AcceptedAsync();
        await _host.Jobs.TransitionOutputAsync(partialOutput, Now, cancellationToken);
        await _host.Jobs.PublishAsync(Artifact(partial, partialOutput), cancellationToken);
        var partialHtml = JobIdentifier.CreateOutput();
        _host.Jobs.SeedOutput(partial, partialHtml, OutputFormat.Html);
        await _host.Jobs.FailOutputAsync(partialHtml, "UNSUPPORTED_CAPABILITY", null, Now, cancellationToken);
        await _host.Jobs.FinalizeJobAsync(partial, Now.AddMinutes(1), cancellationToken);

        // CANCELLED: a job brought to its terminal cancelled state (R0/R4).
        var (cancelled, cancelledOutput) = await AcceptedAsync();
        _host.Jobs.SetJobCancelling(cancelled);
        _host.Jobs.CancelOutput(cancelledOutput);
        await _host.Jobs.FinalizeJobAsync(cancelled, Now.AddMinutes(1), cancellationToken);

        foreach (var jobId in new[] { succeeded, failed, partial, cancelled })
        {
            // The state the job already carries, read before the cancel.
            using var before = await StatusAsync(JobApiHost.Consumer, jobId);
            var terminalStatus = (await JobApiHost.AnswerAsync(before)).GetProperty("status").GetString();

            using var response = await CancelAsync(JobApiHost.Consumer, jobId);
            var answer = await JobApiHost.AnswerAsync(response);

            // AC-6: the terminal state is left unchanged, the current state is returned (200), and
            // no cancel intent is recorded.
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(terminalStatus, answer.GetProperty("status").GetString());
            Assert.Empty(_host.Jobs.Intents.Where(intent =>
                intent.Kind == CancelJobKind && intent.ReportJobId == jobId));
        }
    }

    // ----- Authorisation (SPEC §14 D2): an unknown/unregistered consumer → 403, no record touched.

    [Fact]
    public async Task AnUnknownConsumerIsForbiddenBeforeAnyRecordIsTouched()
    {
        var (jobId, _) = await AcceptedAsync();
        var writesBefore = _host.Jobs.WriteCalls;

        using var response = await CancelAsync(consumerId: null, jobId);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(ErrorCodes.ConsumerUnknown, (await JobApiHost.AnswerAsync(response)).GetProperty("code").GetString());
        Assert.Equal(writesBefore, _host.Jobs.WriteCalls);
        Assert.Empty(_host.Jobs.Intents.Where(intent => intent.Kind == CancelJobKind));
    }

    // ----- Owner scoping (SPEC §14 D2): a foreign, absent or malformed identifier is the same 404.

    [Fact]
    public async Task AForeignAnAbsentAndAMalformedIdentifierAnswerTheSameNotFoundWithNoLeak()
    {
        var (foreign, _) = await AcceptedAsync(AnotherConsumer);
        var absent = "job-ffffffffffffffffffffffffffffffff";
        var malformed = new[]
        {
            "job-XYZ",
            "upload-0123456789abcdef0123456789abcdef",
            "job-0123456789abcdef0123456789abcdef0",
            "job-0123456789abcdef0123456789abcdeF",
        };

        var problems = new List<string>();
        foreach (var jobId in new[] { foreign, absent })
        {
            using var response = await CancelAsync(JobApiHost.Consumer, jobId);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal(ErrorCodes.InputNotFound, (await JobApiHost.AnswerAsync(response)).GetProperty("code").GetString());
            problems.Add(await JobApiHost.ProblemAsync(response));
        }

        // A malformed identifier is answered as a missing one without ever reaching the store.
        var writesBefore = _host.Jobs.WriteCalls;
        foreach (var jobId in malformed)
        {
            using var response = await CancelAsync(JobApiHost.Consumer, jobId);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            problems.Add(await JobApiHost.ProblemAsync(response));
        }

        Assert.Equal(writesBefore, _host.Jobs.WriteCalls);
        // A foreign job was never cancelled: no leak, and no intent recorded for anyone.
        Assert.Empty(_host.Jobs.Intents.Where(intent => intent.Kind == CancelJobKind));
        // The foreign, absent and malformed cases are indistinguishable: one problem document.
        Assert.Single(problems.Distinct(StringComparer.Ordinal));
    }

    // ----- AC-6 / SPEC §14 D3: a transport event never cancels — the decision uses
    //       CancellationToken.None, so an aborted request token does not abort the cancel.

    [Fact]
    public async Task ATransportEventDuringAnInFlightCancelDoesNotAbortTheCancellation()
    {
        CancelStoreProbe? probe = null;
        using var host = new JobApiHost(store => probe = new CancelStoreProbe(store));

        // The job is accepted on the host's own store, the one the probe wraps, so the cancel
        // request runs against the observed store.
        var (observedJob, _) = await AcceptedOnAsync(host);

        using var response = await host.Client.SendAsync(
            CancelRequest(JobApiHost.Consumer, observedJob),
            TestContext.Current.CancellationToken);
        var answer = await JobApiHost.AnswerAsync(response);

        // The cancel completed: 202, CANCELLING, one durable intent — the client's connection was
        // never observed. The token the handler passed to the store cannot be cancelled, which is
        // the structural guarantee that a disconnect/timeout (RequestAborted) never cancels.
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal("cancelling", answer.GetProperty("status").GetString());
        Assert.Single(host.Jobs.Intents.Where(intent =>
            intent.Kind == CancelJobKind && intent.ReportJobId == observedJob));
        Assert.NotNull(probe);
        Assert.True(probe!.Observed, "the cancel had to reach the store");
        Assert.False(probe.LastToken.CanBeCanceled, "the cancel decision must not observe the request's connection (CancellationToken.None)");
    }

    // ----- A store that cannot be reached answers 503 without internal text.

    [Fact]
    public async Task AnUnavailableStoreAnswersServiceUnavailable()
    {
        using var host = new JobApiHost(store => new CancelStoreProbe(store)
        {
            Failure = new JobStoreUnavailableException("08006", new TimeoutException("the server did not answer")),
        });
        var (jobId, _) = await AcceptedOnAsync(host);

        using var response = await host.Client.SendAsync(
            CancelRequest(JobApiHost.Consumer, jobId),
            TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(ErrorCodes.ServiceUnavailable, (await JobApiHost.AnswerAsync(response)).GetProperty("code").GetString());
        Assert.DoesNotContain("08006", body, StringComparison.Ordinal);
        Assert.DoesNotContain("TimeoutException", body, StringComparison.Ordinal);
    }

    // Drives a job into the given non-terminal status through the store the service writes to.
    private async Task PlaceInStatusAsync(string jobId, ReportJobStatus status)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        switch (status)
        {
            case ReportJobStatus.Queued:
                return;

            case ReportJobStatus.Validating:
                await _host.Jobs.TransitionJobAsync(jobId, [ReportJobStatus.Queued], ReportJobStatus.Validating, Now, cancellationToken);
                return;

            case ReportJobStatus.Running:
                await _host.Jobs.TransitionJobAsync(jobId, [ReportJobStatus.Queued], ReportJobStatus.Validating, Now, cancellationToken);
                await _host.Jobs.TransitionJobAsync(jobId, [ReportJobStatus.Validating], ReportJobStatus.Running, Now, cancellationToken);
                return;

            case ReportJobStatus.Partitioning:
            case ReportJobStatus.Finalizing:
                // No stage-bearing transition reaches these states in this increment (JobStages),
                // so the double's arrangement seam places the job directly — the states are
                // non-terminal and the cancel guard must admit them (ADR-012 §3.10(1)).
                _host.Jobs.PlaceInStatus(jobId, status);
                return;

            default:
                throw new ArgumentOutOfRangeException(nameof(status), status, "not a non-terminal arrangement");
        }
    }

    private Task<(string JobId, string OutputId)> AcceptedAsync(string consumer = JobApiHost.Consumer) =>
        AcceptedOnAsync(_host, consumer);

    private static async Task<(string JobId, string OutputId)> AcceptedOnAsync(JobApiHost host, string consumer = JobApiHost.Consumer)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var uploadId = await host.UploadAsync(JobApiHost.UploadState.Ready, consumer);
        var acceptance = new JobAcceptance(
            JobIdentifier.Create(),
            [new RequestedOutput(JobIdentifier.CreateOutput(), OutputFormat.Pdf)],
            consumer,
            uploadId,
            "pilot-grouped",
            TemplateReferenceForm.Exact,
            "1.0.0",
            AcceptanceIdentity.For(uploadId));
        Assert.IsType<JobAcceptanceResult.Accepted>(await host.Jobs.AcceptAsync(acceptance, JobApiHost.Now, cancellationToken));
        return (acceptance.ReportJobId, acceptance.Outputs[0].OutputId);
    }

    private Task<HttpResponseMessage> CancelAsync(string? consumerId, string jobId) =>
        _host.Client.SendAsync(CancelRequest(consumerId, jobId), TestContext.Current.CancellationToken);

    private Task<HttpResponseMessage> StatusAsync(string? consumerId, string jobId)
    {
        var message = new HttpRequestMessage(
            HttpMethod.Get,
            new Uri($"{CreateReportJobEndpoint.RoutePrefix}/{jobId}", UriKind.Relative));
        if (consumerId is not null)
        {
            message.Headers.TryAddWithoutValidation(ConsumerIdentity.HeaderName, consumerId);
        }

        return _host.Client.SendAsync(message, TestContext.Current.CancellationToken);
    }

    private static HttpRequestMessage CancelRequest(string? consumerId, string jobId)
    {
        var message = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri($"{CreateReportJobEndpoint.RoutePrefix}/{jobId}/cancel", UriKind.Relative));
        if (consumerId is not null)
        {
            message.Headers.TryAddWithoutValidation(ConsumerIdentity.HeaderName, consumerId);
        }

        return message;
    }

    private static ArtifactRecord Artifact(string jobId, string outputId) => new(
        ArtifactId,
        jobId,
        outputId,
        OutputFormat.Pdf,
        "application/pdf",
        $"outputs/{jobId}/{outputId}/attempt",
        Hash,
        2_048,
        3,
        Now,
        Now.AddDays(7));

    // Forwards every member to the store it wraps; observes the cancellation call — the token the
    // handler passed, and, when a failure is set, raises it — so a case can prove the decision does
    // not observe the client's connection and that a store fault becomes a 503.
    private sealed class CancelStoreProbe(IJobStore inner) : IJobStore
    {
        public bool Observed { get; private set; }

        public CancellationToken LastToken { get; private set; }

        public Exception? Failure { get; init; }

        public Task<CancellationRequestResult> RequestCancellationAsync(
            string reportJobId,
            string consumerId,
            DateTimeOffset now,
            CancellationToken cancellationToken)
        {
            Observed = true;
            LastToken = cancellationToken;
            return Failure is not null
                ? throw Failure
                : inner.RequestCancellationAsync(reportJobId, consumerId, now, cancellationToken);
        }

        public Task<JobAcceptanceResult> AcceptAsync(JobAcceptance acceptance, DateTimeOffset now, CancellationToken cancellationToken) =>
            inner.AcceptAsync(acceptance, now, cancellationToken);

        public Task<IdempotencyLookup> FindByIdempotencyKeyAsync(string consumerId, string idempotencyKey, RequestFingerprint fingerprint, CancellationToken cancellationToken) =>
            inner.FindByIdempotencyKeyAsync(consumerId, idempotencyKey, fingerprint, cancellationToken);

        public Task<JobStateRecord?> FindForWorkflowAsync(string reportJobId, CancellationToken cancellationToken) =>
            inner.FindForWorkflowAsync(reportJobId, cancellationToken);

        public Task<TemplateReferenceRecord?> FindTemplateReferenceAsync(string reportJobId, CancellationToken cancellationToken) =>
            inner.FindTemplateReferenceAsync(reportJobId, cancellationToken);

        public Task<JobFailureOutcome> FailJobAsync(string reportJobId, string code, string reason, string? message, DateTimeOffset now, CancellationToken cancellationToken) =>
            inner.FailJobAsync(reportJobId, code, reason, message, now, cancellationToken);

        public Task<TemplatePinState> PinTemplateAsync(string reportJobId, TemplatePin pin, DateTimeOffset now, CancellationToken cancellationToken, TemplateLocaleDefaults? presentation) =>
            inner.PinTemplateAsync(reportJobId, pin, now, cancellationToken, presentation);

        public Task<BeginInputWaitOutcome> BeginInputWaitAsync(string reportJobId, CancellationToken cancellationToken) =>
            inner.BeginInputWaitAsync(reportJobId, cancellationToken);

        public Task<EndInputWaitOutcome> EndInputWaitAsync(string reportJobId, CancellationToken cancellationToken) =>
            inner.EndInputWaitAsync(reportJobId, cancellationToken);

        public Task<IReadOnlyList<string>> FindInputWaitingJobIdsAsync(string uploadId, CancellationToken cancellationToken) =>
            inner.FindInputWaitingJobIdsAsync(uploadId, cancellationToken);

        public Task<TransitionJobResult> TransitionJobAsync(string reportJobId, IReadOnlyList<ReportJobStatus> from, ReportJobStatus to, DateTimeOffset now, CancellationToken cancellationToken) =>
            inner.TransitionJobAsync(reportJobId, from, to, now, cancellationToken);

        public Task<TransitionOutcome> TransitionOutputAsync(string outputId, DateTimeOffset now, CancellationToken cancellationToken) =>
            inner.TransitionOutputAsync(outputId, now, cancellationToken);

        public Task<PartitionCommitResult> CommitPartitionAsync(PartitionRecord partition, CancellationToken cancellationToken) =>
            inner.CommitPartitionAsync(partition, cancellationToken);

        public Task<PartitionRecord?> FindPartitionAsync(string reportJobId, CancellationToken cancellationToken) =>
            inner.FindPartitionAsync(reportJobId, cancellationToken);

        public Task<PartCommitResult> CommitPartAsync(PartCommit commit, CancellationToken cancellationToken) =>
            inner.CommitPartAsync(commit, cancellationToken);

        public Task<PartCommit?> FindPartAsync(string outputId, int blockIndex, CancellationToken cancellationToken) =>
            inner.FindPartAsync(outputId, blockIndex, cancellationToken);

        public Task<PublishResult> PublishAsync(ArtifactRecord artifact, CancellationToken cancellationToken) =>
            inner.PublishAsync(artifact, cancellationToken);

        public Task<JobArtifactsView?> ListArtifactsAsync(string reportJobId, string consumerId, CancellationToken cancellationToken) =>
            inner.ListArtifactsAsync(reportJobId, consumerId, cancellationToken);

        public Task<ArtifactView?> FindArtifactAsync(string reportJobId, string artifactId, string consumerId, CancellationToken cancellationToken) =>
            inner.FindArtifactAsync(reportJobId, artifactId, consumerId, cancellationToken);

        public Task<OutputFailureOutcome> FailOutputAsync(string outputId, string code, string? reason, DateTimeOffset now, CancellationToken cancellationToken) =>
            inner.FailOutputAsync(outputId, code, reason, now, cancellationToken);

        public Task<JobFinalizationResult> FinalizeJobAsync(string reportJobId, DateTimeOffset now, CancellationToken cancellationToken) =>
            inner.FinalizeJobAsync(reportJobId, now, cancellationToken);

        public Task<ReportJobStatusView?> FindStatusAsync(string reportJobId, string consumerId, CancellationToken cancellationToken) =>
            inner.FindStatusAsync(reportJobId, consumerId, cancellationToken);

        public Task<ProgressWriteOutcome> RecordRecordsReadAsync(string reportJobId, long recordsRead, CancellationToken cancellationToken) =>
            inner.RecordRecordsReadAsync(reportJobId, recordsRead, cancellationToken);

        public Task<AttemptCauseOutcome> RecordAttemptCauseAsync(AttemptCause cause, CancellationToken cancellationToken) =>
            inner.RecordAttemptCauseAsync(cause, cancellationToken);

        public Task<AttemptCause?> FindLastAttemptCauseAsync(string outputId, int blockIndex, CancellationToken cancellationToken) =>
            inner.FindLastAttemptCauseAsync(outputId, blockIndex, cancellationToken);
    }
}
