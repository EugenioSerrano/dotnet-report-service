using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ReportService.Jobs;
using ReportService.ServiceDefaults;
using ReportService.ServiceDefaults.Temporal;
using ReportService.Testing;
using ReportService.Storage;
using ReportService.TemplatePackages.Records;
using ReportService.Uploads;
using ReportService.Workers.Orchestration;
using ReportService.Workers.Orchestration.Dispatch;
using ReportService.Workers.Orchestration.Jobs;
using ReportService.Workers.Orchestration.Telemetry;
using ReportService.Workers.Orchestration.Uploads;
using ReportService.Workers.Orchestration.Tests.Jobs;

namespace ReportService.Workers.Orchestration.Tests;

/// <summary>
/// The execution settings of one verification attempt, asserted value by value: the
/// queue it runs on, its two bounds, its bounded retry policy and the codes the
/// engine must never retry. Nothing here needs an engine.
/// </summary>
public sealed class UploadVerificationActivityOptionsTests
{
    [Fact]
    public void EveryValueOfTheAttemptIsTheOneTheDeploymentAgreed()
    {
        var options = UploadVerificationActivityOptionsTests_Options(TimeSpan.FromHours(2));

        var activity = UploadVerificationActivityOptions.Create(options.StartToClose, TimeSpan.FromHours(6));

        Assert.Equal("report-data", activity.TaskQueue);
        Assert.Equal(TimeSpan.FromHours(2), activity.StartToCloseTimeout);
        Assert.Equal(TimeSpan.FromHours(6), activity.ScheduleToCloseTimeout);
        Assert.Equal(TimeSpan.FromSeconds(60), activity.HeartbeatTimeout);
        Assert.NotNull(activity.RetryPolicy);
        Assert.Equal(TimeSpan.FromSeconds(2), activity.RetryPolicy!.InitialInterval);
        Assert.Equal(2f, activity.RetryPolicy.BackoffCoefficient);
        Assert.Equal(TimeSpan.FromSeconds(60), activity.RetryPolicy.MaximumInterval);
        Assert.Equal(5, activity.RetryPolicy.MaximumAttempts);
        Assert.Equal(Temporalio.Workflows.ActivityCancellationType.WaitCancellationCompleted, activity.CancellationType);
    }

    [Fact]
    public void TheNeverRetriedCodesAreTheOnesTheDecisionFixed()
    {
        var activity = UploadVerificationActivityOptions.Create(TimeSpan.FromHours(1), TimeSpan.FromHours(6));

        Assert.NotNull(activity.RetryPolicy);
        Assert.Equal(
            ["DATA_INVALID", "RECORD_TOO_LARGE", "TEMPLATE_NOT_FOUND", "TEMPLATE_VERSION_CONFLICT", "TEMPLATE_INVALID", "UNSUPPORTED_CAPABILITY", "INPUT_NOT_FOUND", "EXECUTION_FAILED"],
            activity.RetryPolicy!.NonRetryableErrorTypes);
    }

    [Fact]
    public void APositiveBoundIsRequired()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => UploadVerificationActivityOptions.Create(TimeSpan.Zero, TimeSpan.FromHours(6)));
        Assert.Throws<ArgumentOutOfRangeException>(() => UploadVerificationActivityOptions.Create(TimeSpan.FromSeconds(-1), TimeSpan.FromHours(6)));
    }

    private static UploadVerificationOptions UploadVerificationActivityOptionsTests_Options(TimeSpan bound) =>
        new() { StartToClose = bound };
}

/// <summary>
/// The identity of one attempt: the hash of what the engine says it is, the same
/// for that attempt and different for every other, and a key that names the upload
/// and the attempt inside the snapshot prefix.
/// </summary>
public sealed class AttemptIdentityTests
{
    private const string UploadId = "upload-0123456789abcdef0123456789abcdef";

    [Fact]
    public void TwoAttemptsOfOneVerificationHaveDifferentIdentifiers()
    {
        var first = UploadVerificationActivities.AttemptId("upload/" + UploadId, "run-1", "activity-1", 1);
        var second = UploadVerificationActivities.AttemptId("upload/" + UploadId, "run-1", "activity-1", 2);

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void OneAttemptAlwaysHasTheSameIdentifier()
    {
        var first = UploadVerificationActivities.AttemptId("upload/" + UploadId, "run-1", "activity-1", 1);
        var again = UploadVerificationActivities.AttemptId("upload/" + UploadId, "run-1", "activity-1", 1);

        Assert.Equal(first, again);
        Assert.Matches("^[0-9a-f]{64}$", first);
    }

    [Fact]
    public void TheKeyNamesTheUploadAndTheAttemptUnderTheSnapshotPrefix()
    {
        var attemptId = UploadVerificationActivities.AttemptId("upload/" + UploadId, "run-1", "activity-1", 1);

        var key = UploadVerificationActivities.AttemptKey(UploadId, attemptId);

        Assert.Equal($"snapshots/{UploadId}/{attemptId}", key.ToString());
    }

    [Fact]
    public void AnAttemptNumberBelowOneIsRefused() =>
        Assert.Throws<ArgumentOutOfRangeException>(
            () => UploadVerificationActivities.AttemptId("upload/" + UploadId, "run-1", "activity-1", 0));
}

/// <summary>
/// The dispatcher's own rules, with the engine replaced by a scripted seam and the
/// clock moved by the test: what is dispatched, what is retried with which wait,
/// what is never failed, and what a poll leaves for the next one.
/// </summary>
public sealed class IntentDispatcherTests : IDisposable
{
    private const string UploadId = "upload-0123456789abcdef0123456789abcdef";
    private static readonly DateTimeOffset Now = new(2026, 11, 1, 6, 0, 0, TimeSpan.Zero);

    private readonly InMemoryIntentStore _store = new();
    private readonly ScriptedWorkflowStarter _starter = new();
    private readonly TemporalClientHolder _holder = new();
    private readonly ManualTimeProvider _clock = new(Now);
    private readonly OrchestrationMetrics _metrics = new(MeterIdentity.ForHost(TelemetryDefaults.MeterName));
    private readonly LogCapture _logs = new();
    private readonly LoggerFactory _loggerFactory;

    public IntentDispatcherTests()
    {
        _loggerFactory = new LoggerFactory([_logs]);
        _holder.Replace(new FakeEngineConnection());
    }

    public void Dispose()
    {
        _loggerFactory.Dispose();
        _metrics.Dispose();
    }

    [Fact]
    public async Task AnExecutionThatAlreadyExistsIsUsedAndNothingIsStarted()
    {
        _store.Enqueue(IntentKinds.StartUploadVerification, UploadId, Now);
        _starter.Described = new WorkflowStart($"upload/{UploadId}", "run-existing");

        await Dispatcher().DispatchDueAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, _starter.Starts);
        Assert.Equal("DISPATCHED", _store.StatusOf(1));
        Assert.Equal("run-existing", Assert.Single(_store.References).RunId);
    }

    [Fact]
    public async Task AnUnknownWorkflowIsStartedOnceAndRecorded()
    {
        _store.Enqueue(IntentKinds.StartUploadVerification, UploadId, Now);
        _starter.OnStart = (workflowId, _) => new WorkflowStart(workflowId, "run-started");

        await Dispatcher().DispatchDueAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, _starter.Starts);
        Assert.Equal("DISPATCHED", _store.StatusOf(1));
        Assert.Equal($"upload/{UploadId}", Assert.Single(_store.References).WorkflowId);
        Assert.Contains(_logs.Lines, line => line.Contains("dispatched as", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AStartThatCollidesIsServedByTheExecutionAlreadyThere()
    {
        _store.Enqueue(IntentKinds.StartUploadVerification, UploadId, Now);
        _starter.OnStart = (_, _) => throw new InvalidOperationException("already started");
        _starter.OnDescribe = (count, _) => count == 1 ? null : new WorkflowStart($"upload/{UploadId}", "run-other");

        await Dispatcher().DispatchDueAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, _starter.Starts);
        Assert.Equal("DISPATCHED", _store.StatusOf(1));
        Assert.Equal("run-other", Assert.Single(_store.References).RunId);
    }

    [Fact]
    public async Task AFailureWaitsTwoSecondsThenFourAndTheRequestStaysPending()
    {
        _store.Enqueue(IntentKinds.StartUploadVerification, UploadId, Now);
        _starter.OnDescribe = (_, _) => throw new TimeoutException("the engine did not answer");
        _starter.OnStart = (_, _) => throw new TimeoutException("the engine did not answer");
        var dispatcher = Dispatcher();

        await dispatcher.DispatchDueAsync(TestContext.Current.CancellationToken);

        Assert.Equal("PENDING", _store.StatusOf(1));
        Assert.Equal(1, Assert.Single(_store.Requests).Attempts);
        Assert.Contains(_logs.Lines, line => line.Contains("TimeoutException", StringComparison.Ordinal) && !line.Contains("did not answer", StringComparison.Ordinal));

        _clock.Advance(TimeSpan.FromSeconds(1));
        await dispatcher.DispatchDueAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, Assert.Single(_store.Requests).Attempts);

        _clock.Advance(TimeSpan.FromSeconds(1));
        await dispatcher.DispatchDueAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, Assert.Single(_store.Requests).Attempts);
    }

    [Fact]
    public void TheBackoffDoublesAndIsCapped()
    {
        Assert.Equal(TimeSpan.FromSeconds(2), IntentDispatcher.BackoffDelay(0));
        Assert.Equal(TimeSpan.FromSeconds(4), IntentDispatcher.BackoffDelay(1));
        Assert.Equal(TimeSpan.FromSeconds(8), IntentDispatcher.BackoffDelay(2));
        Assert.Equal(TimeSpan.FromSeconds(60), IntentDispatcher.BackoffDelay(6));
        Assert.Equal(TimeSpan.FromSeconds(60), IntentDispatcher.BackoffDelay(30));
    }

    [Fact]
    public async Task AKindThisProcessDoesNotServeIsNeverDispatchedAndNeverFailed()
    {
        _store.Enqueue("start-something-else", UploadId, Now);

        await Dispatcher().DispatchDueAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, _starter.Starts);
        Assert.Equal("PENDING", _store.StatusOf(1));
        Assert.Equal(0, Assert.Single(_store.Requests).Attempts);
    }

    [Fact]
    public async Task OnePollClaimsNoMoreThanItsBatch()
    {
        _store.Enqueue(IntentKinds.StartUploadVerification, UploadId, Now);
        _store.Enqueue(IntentKinds.StartUploadVerification, "upload-11111111111111111111111111111111", Now);
        _starter.OnStart = (workflowId, _) => new WorkflowStart(workflowId, "run");

        await Dispatcher(batchSize: 1).DispatchDueAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, _starter.Starts);
        Assert.Equal("DISPATCHED", _store.StatusOf(1));
        Assert.Equal("PENDING", _store.StatusOf(2));
    }

    [Fact]
    public async Task ARequestThatWaitsTooLongIsReportedOnceAndPerProcess()
    {
        _store.Enqueue(IntentKinds.StartUploadVerification, UploadId, Now.AddMinutes(-10), createdAt: Now.AddMinutes(-10));
        _starter.OnDescribe = (_, _) => throw new TimeoutException("the engine did not answer");
        var dispatcher = Dispatcher();

        await dispatcher.DispatchDueAsync(TestContext.Current.CancellationToken);
        await dispatcher.DispatchDueAsync(TestContext.Current.CancellationToken);

        Assert.Single(_logs.Lines, line => line.Contains("pending for more than", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheBudgetEndsThePollAndTheRestIsLeftForTheNextOne()
    {
        _store.Enqueue(IntentKinds.StartUploadVerification, UploadId, Now);
        _store.Enqueue(IntentKinds.StartUploadVerification, "upload-11111111111111111111111111111111", Now);
        _starter.OnStart = (workflowId, _) =>
        {
            _clock.Advance(TimeSpan.FromSeconds(31));
            return new WorkflowStart(workflowId, "run");
        };

        await Dispatcher().DispatchDueAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, _starter.Starts);

        // The first request's decision was committed; the second was left untouched.
        Assert.Equal("DISPATCHED", _store.StatusOf(1));
        Assert.Equal("PENDING", _store.StatusOf(2));
        Assert.Equal(0, Assert.Single(_store.Requests, request => request.IntentId == 2).Attempts);
    }

    [Fact]
    public async Task TheRecordedReferenceCarriesThePolicyAndTheBuild()
    {
        _store.Enqueue(IntentKinds.StartUploadVerification, UploadId, Now);
        _starter.OnStart = (workflowId, _) => new WorkflowStart(workflowId, "run-9");

        await Dispatcher(verificationBound: TimeSpan.FromMinutes(30)).DispatchDueAsync(TestContext.Current.CancellationToken);

        var reference = Assert.Single(_store.References);
        Assert.Contains("start-to-close 1800s", reference.RetryPolicy, StringComparison.Ordinal);
        Assert.Contains("5 attempts", reference.RetryPolicy, StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(reference.WorkerBuild));
        Assert.Equal(Now, reference.RecordedAt);
    }

    [Fact]
    public async Task AStartJobRequestIsStartedAsTheJobWorkflowOfItsOwnIdentifier()
    {
        const string ReportJobId = "job-0123456789abcdef0123456789abcdef";
        _store.Enqueue(IntentKinds.StartJob, ReportJobId, Now);
        _starter.OnStart = (workflowId, _) => new WorkflowStart(workflowId, "run-job");

        await Dispatcher().DispatchDueAsync(TestContext.Current.CancellationToken);

        Assert.Equal($"report-job/{ReportJobId}", Assert.Single(_starter.Started));
        Assert.Equal("DISPATCHED", _store.StatusOf(1));
        var reference = Assert.Single(_store.References);
        Assert.Equal($"report-job/{ReportJobId}", reference.WorkflowId);
        Assert.Equal("run-job", reference.RunId);
        Assert.Contains("metadata: start-to-close 30s", reference.RetryPolicy, StringComparison.Ordinal);
        Assert.Contains("unbounded", reference.RetryPolicy, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AJobsRequestsAreDispatchedInTheOrderTheyWereRecorded()
    {
        const string FirstJob = "job-00000000000000000000000000000001";
        const string SecondJob = "job-00000000000000000000000000000002";
        _store.Enqueue(IntentKinds.StartJob, FirstJob, Now);
        _store.Enqueue(IntentKinds.StartJob, SecondJob, Now);
        _starter.OnStart = (workflowId, _) => new WorkflowStart(workflowId, "run");

        await Dispatcher().DispatchDueAsync(TestContext.Current.CancellationToken);

        Assert.Equal([$"report-job/{FirstJob}", $"report-job/{SecondJob}"], _starter.Started);
        Assert.Equal("DISPATCHED", _store.StatusOf(1));
        Assert.Equal("DISPATCHED", _store.StatusOf(2));
    }

    [Fact]
    public async Task AJobThatIsAlreadyStartedIsNotStartedAgainAndItsReferenceIsKept()
    {
        const string ReportJobId = "job-0123456789abcdef0123456789abcdef";
        _store.Enqueue(IntentKinds.StartJob, ReportJobId, Now);
        _starter.Described = new WorkflowStart($"report-job/{ReportJobId}", "run-existing");

        await Dispatcher().DispatchDueAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, _starter.Starts);
        Assert.Equal("DISPATCHED", _store.StatusOf(1));
        Assert.Equal("run-existing", Assert.Single(_store.References).RunId);
    }

    // ----- US-010 AC-2/AC-3 + ADR-012 §3.10(2): the cancel-job plan. The oracles are the approved
    //       ADR (a cancellation request is delivered to report-job/{id}; a request to a closed
    //       execution, or to one that never existed, counts as delivered; a delivery failure is
    //       retried and never recorded as a failure state), never the implementation (G06).

    [Fact]
    public async Task ACancelJobRequestCancelsTheJobsExecutionAndIsDispatched()
    {
        const string ReportJobId = "job-0123456789abcdef0123456789abcdef";
        _store.Enqueue(IntentKinds.CancelJob, ReportJobId, Now);
        _starter.Described = new WorkflowStart($"report-job/{ReportJobId}", "run-running");

        await Dispatcher().DispatchDueAsync(TestContext.Current.CancellationToken);

        // ADR-012 §3.10(2): the request a cancellation is delivered as, aimed at the job's own
        // execution; the intent is DISPATCHED with that execution's reference.
        Assert.Equal([$"report-job/{ReportJobId}"], _starter.Cancelled);
        Assert.Equal(0, _starter.Starts);
        Assert.Equal("DISPATCHED", _store.StatusOf(1));
        var reference = Assert.Single(_store.References);
        Assert.Equal($"report-job/{ReportJobId}", reference.WorkflowId);
        Assert.Equal("run-running", reference.RunId);
        Assert.Equal(IntentDispatcher.CancellationRetryPolicySummary, reference.RetryPolicy);
        Assert.DoesNotContain(_logs.Lines, line => line.Contains("does not serve", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ACancelJobRequestWithNoExecutionIsDeliveredAndWritesNoReference()
    {
        const string ReportJobId = "job-0123456789abcdef0123456789abcdef";
        _store.Enqueue(IntentKinds.CancelJob, ReportJobId, Now);
        _starter.Described = null;

        await Dispatcher().DispatchDueAsync(TestContext.Current.CancellationToken);

        // ADR-012 §3.10(2): a cancellation aimed at an execution that never started counts as
        // delivered. It leaves the pending set, so the pending-age signal keeps meaning "not
        // delivered", and it writes no reference because there is no run to reference.
        Assert.Empty(_starter.Cancelled);
        Assert.Equal(0, _starter.Starts);
        Assert.Equal("DISPATCHED", _store.StatusOf(1));
        Assert.Empty(_store.References);
        Assert.Equal(1, Assert.Single(_store.Requests).Attempts);
    }

    [Fact]
    public async Task ACancelJobRequestOfAClosedExecutionIsDelivered()
    {
        const string ReportJobId = "job-0123456789abcdef0123456789abcdef";
        _store.Enqueue(IntentKinds.CancelJob, ReportJobId, Now);
        _starter.Described = new WorkflowStart($"report-job/{ReportJobId}", "run-closed");

        await Dispatcher().DispatchDueAsync(TestContext.Current.CancellationToken);

        // The dispatcher never inspects liveness: an execution the engine can describe is asked to
        // cancel, and an engine that accepts the request of a closed execution serves it. The
        // request is delivered exactly as the running one.
        Assert.Equal([$"report-job/{ReportJobId}"], _starter.Cancelled);
        Assert.Equal("DISPATCHED", _store.StatusOf(1));
        Assert.Equal("run-closed", Assert.Single(_store.References).RunId);
    }

    [Fact]
    public async Task ACancelJobWhoseEngineFailsIsRetriedWithTheBackoffAndNeverFails()
    {
        const string ReportJobId = "job-0123456789abcdef0123456789abcdef";
        _store.Enqueue(IntentKinds.CancelJob, ReportJobId, Now);
        _starter.Described = new WorkflowStart($"report-job/{ReportJobId}", "run-running");
        _starter.OnCancel = _ => throw new TimeoutException("the engine did not answer");
        var dispatcher = Dispatcher();

        await dispatcher.DispatchDueAsync(TestContext.Current.CancellationToken);

        // A delivery that failed is an attempt with a wait, never a failed state: the request
        // stays pending and is tried again after the dispatcher's backoff.
        Assert.Equal("PENDING", _store.StatusOf(1));
        Assert.Empty(_store.References);
        Assert.Equal(1, Assert.Single(_store.Requests).Attempts);

        _starter.OnCancel = null;
        _clock.Advance(TimeSpan.FromSeconds(1));
        await dispatcher.DispatchDueAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, Assert.Single(_store.Requests).Attempts);

        _clock.Advance(TimeSpan.FromSeconds(1));
        await dispatcher.DispatchDueAsync(TestContext.Current.CancellationToken);
        Assert.Equal("DISPATCHED", _store.StatusOf(1));
        Assert.Equal(2, Assert.Single(_store.Requests).Attempts);
    }

    [Fact]
    public async Task TheStartPlansKeepTheirBehaviour()
    {
        const string Upload = "upload-0123456789abcdef0123456789abcdef";
        const string ReportJobId = "job-0123456789abcdef0123456789abcdef";
        _store.Enqueue(IntentKinds.StartUploadVerification, Upload, Now);
        _store.Enqueue(IntentKinds.StartJob, ReportJobId, Now);
        _starter.OnStart = (workflowId, _) => new WorkflowStart(workflowId, "run-started");

        await Dispatcher().DispatchDueAsync(TestContext.Current.CancellationToken);

        // The two start kinds are unchanged: they start their own workflow, they never ask the
        // engine to cancel anything, and each records its reference.
        Assert.Equal([$"upload/{Upload}", $"report-job/{ReportJobId}"], _starter.Started);
        Assert.Empty(_starter.Cancelled);
        Assert.Equal(2, _store.References.Count);
    }

    /// <summary>
    /// US-010 AC-7 / ADR-012 §3.2(4): the claim of a poll is one unit of work committed after the
    /// poll, so a process that dies mid-poll leaves every request it claimed exactly as it was. The
    /// case models the death by making the claim's commit fail — the one act that would have stored
    /// the delivery — and asserts the <c>cancel-job</c> row is still <c>PENDING</c>, with its attempt
    /// count and its next attempt unchanged, and that no execution reference was written.
    /// </summary>
    [Fact]
    public async Task APollThatDiesBeforeItsCommitLeavesTheCancelRequestPending()
    {
        const string ReportJobId = "job-0123456789abcdef0123456789abcdef";
        _store.Enqueue(IntentKinds.CancelJob, ReportJobId, Now);
        _starter.Described = new WorkflowStart($"report-job/{ReportJobId}", "run-running");
        var dying = new DyingBeforeCommitIntentStore(_store);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Dispatcher(store: dying).DispatchDueAsync(TestContext.Current.CancellationToken));

        // The delivery was attempted and nothing of it was stored: the row is pending, its attempt
        // count did not move, and no reference names the execution.
        Assert.Equal([$"report-job/{ReportJobId}"], _starter.Cancelled);
        Assert.Equal("PENDING", _store.StatusOf(1));
        Assert.Equal(0, Assert.Single(_store.Requests).Attempts);
        Assert.Empty(_store.References);
    }

    /// <summary>
    /// US-010 AC-7 / ADR-012 §3.10(2): the request the dying poll left pending is delivered by the
    /// next poll — of the restart's own process — and the repeat has no second effect: exactly one
    /// <c>cancel-job</c> row, <c>DISPATCHED</c> once, with exactly one execution reference.
    /// </summary>
    [Fact]
    public async Task TheRequestAPollLeftPendingIsDeliveredOnceByTheNextPoll()
    {
        const string ReportJobId = "job-0123456789abcdef0123456789abcdef";
        _store.Enqueue(IntentKinds.CancelJob, ReportJobId, Now);
        _starter.Described = new WorkflowStart($"report-job/{ReportJobId}", "run-running");
        var dying = new DyingBeforeCommitIntentStore(_store);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Dispatcher(store: dying).DispatchDueAsync(TestContext.Current.CancellationToken));
        await Dispatcher().DispatchDueAsync(TestContext.Current.CancellationToken);

        // The restart's poll delivered the request the dead one could not store, and the row it
        // wrote is the one and only: the engine was asked to cancel the same execution twice, and
        // the durable records show a single delivered request with a single reference.
        Assert.Equal([$"report-job/{ReportJobId}", $"report-job/{ReportJobId}"], _starter.Cancelled);
        Assert.Single(_store.Requests);
        Assert.Equal("DISPATCHED", _store.StatusOf(1));
        var reference = Assert.Single(_store.References);
        Assert.Equal($"report-job/{ReportJobId}", reference.WorkflowId);
        Assert.Equal("run-running", reference.RunId);
    }

    private IntentDispatcher Dispatcher(
        int batchSize = 50,
        TimeSpan? verificationBound = null,
        IIntentStore? store = null) =>
        new(
            store ?? _store,
            _starter,
            _holder,
            Options.Create(new DispatcherOptions
            {
                PollInterval = TimeSpan.FromSeconds(1),
                BatchSize = batchSize,
                AlertAfter = TimeSpan.FromMinutes(5),
                ClaimBudget = TimeSpan.FromSeconds(30),
            }),
            Options.Create(new UploadVerificationOptions { StartToClose = verificationBound ?? TimeSpan.FromHours(1) }),
            _clock,
            _metrics,
            _loggerFactory.CreateLogger<IntentDispatcher>());

    private sealed class ScriptedWorkflowStarter : IWorkflowStarter
    {
        private int _describes;

        public WorkflowStart? Described { get; set; }

        public Func<int, string, WorkflowStart?>? OnDescribe { get; set; }

        public Func<string, string, WorkflowStart>? OnStart { get; set; }

        public Func<string, Task>? OnCancel { get; set; }

        public int Starts { get; private set; }

        public List<string> Started { get; } = [];

        public List<string> Cancelled { get; } = [];

        public Task<WorkflowStart?> DescribeAsync(string workflowId, CancellationToken cancellationToken)
        {
            var count = ++_describes;
            return Task.FromResult(OnDescribe is null ? Described : OnDescribe(count, workflowId));
        }

        public Task<WorkflowStart> StartUploadVerificationAsync(string workflowId, string uploadId, CancellationToken cancellationToken) =>
            Start(workflowId, uploadId);

        public Task<WorkflowStart> StartReportJobAsync(string workflowId, string reportJobId, CancellationToken cancellationToken) =>
            Start(workflowId, reportJobId);

        public Task CancelWorkflowAsync(string workflowId, CancellationToken cancellationToken)
        {
            Cancelled.Add(workflowId);
            return OnCancel is null ? Task.CompletedTask : OnCancel(workflowId);
        }

        private Task<WorkflowStart> Start(string workflowId, string subjectId)
        {
            Starts++;
            Started.Add(workflowId);
            return Task.FromResult(OnStart is null
                ? new WorkflowStart(workflowId, "run-default")
                : OnStart(workflowId, subjectId));
        }
    }

    // The connection the dispatcher checks for; the starter is scripted, so no client is ever used.
    private sealed class FakeEngineConnection : IEngineConnection
    {
        public Temporalio.Client.ITemporalClient Client => null!;

        public Task<bool> CheckHealthAsync(TimeSpan timeout, CancellationToken cancellationToken) => Task.FromResult(true);

        public Task<string> ServerVersionAsync(CancellationToken cancellationToken) => Task.FromResult("test");
    }

    // The process-death model of the store: a poll claims exactly as the real store does, and the
    // one act that would have stored its decisions fails instead — the process ended before its
    // commit, so the claim is disposed uncommitted and the requests stay exactly as they were.
    private sealed class DyingBeforeCommitIntentStore(IIntentStore inner) : IIntentStore
    {
        public async Task<IIntentClaim> ClaimDueAsync(DateTimeOffset now, int batchSize, CancellationToken cancellationToken) =>
            new DyingClaim(await inner.ClaimDueAsync(now, batchSize, cancellationToken).ConfigureAwait(false));

        public Task<TimeSpan?> OldestPendingAgeAsync(DateTimeOffset now, CancellationToken cancellationToken) =>
            inner.OldestPendingAgeAsync(now, cancellationToken);

        private sealed class DyingClaim(IIntentClaim inner) : IIntentClaim
        {
            public IReadOnlyList<IntentRecord> Intents => inner.Intents;

            public Task MarkDispatchedAsync(long intentId, ExecutionReference reference, DateTimeOffset now, CancellationToken cancellationToken) =>
                inner.MarkDispatchedAsync(intentId, reference, now, cancellationToken);

            public Task MarkDeliveredAsync(long intentId, DateTimeOffset now, CancellationToken cancellationToken) =>
                inner.MarkDeliveredAsync(intentId, now, cancellationToken);

            public Task RecordFailureAsync(long intentId, DateTimeOffset now, DateTimeOffset nextAttemptAt, CancellationToken cancellationToken) =>
                inner.RecordFailureAsync(intentId, now, nextAttemptAt, cancellationToken);

            public Task CommitAsync(CancellationToken cancellationToken) =>
                throw new InvalidOperationException("the process died before its poll committed");

            public ValueTask DisposeAsync() => inner.DisposeAsync();
        }
    }
}

/// <summary>The intent store contract, run against the in-memory store.</summary>
public sealed class InMemoryIntentStoreContractTests : IntentStoreContract
{
    private readonly InMemoryIntentStore _store = new();

    /// <summary>The store this class's cases run against.</summary>
    protected override Task<IIntentStore> CreateStoreAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IIntentStore>(_store);

    /// <summary>Records a pending request in the store.</summary>
    protected override Task<long> EnqueueAsync(string subjectId, DateTimeOffset due, DateTimeOffset createdAt, CancellationToken cancellationToken) =>
        Task.FromResult(_store.Enqueue(Kind, subjectId, due, createdAt));
}

/// <summary>
/// What the worker registers on the queues it declares: exactly one engine worker
/// per queue that has work, in declaration order, with the registration it added and
/// the concurrency bound it configured — and a registration on a queue the process
/// does not declare stops start-up rather than being silently dropped.
/// </summary>
[Collection(JobWorkflowSettings.Name)]
public sealed class OrchestrationWorkerEngineWorkerTests : IDisposable
{
    private readonly VerifierRig _rig = new();
    private IHost? _host;

    public void Dispose()
    {
        _host?.Dispose();
        _rig.Dispose();
    }

    [Fact]
    public void BothDeclaredQueuesGetExactlyOneWorkerWithItsRegistrations()
    {
        var plan = Plan(["report-orchestration", "report-data", "report-html"]);

        Assert.Equal(3, plan.Count);
        Assert.Equal("report-orchestration", plan[0].Queue);
        Assert.Equal(4, plan[0].Options.Workflows.Count);
        Assert.Equal(19, plan[0].Options.Activities.Count);
        Assert.Contains(
            plan[0].Options.Activities,
            activity => activity.Name == JobTransitionActivities.ReadJobStateName);
        Assert.Contains(
            plan[0].Options.Activities,
            activity => activity.Name == InputWaitActivities.BeginName);
        Assert.Contains(
            plan[0].Options.Activities,
            activity => activity.Name == InputWaitActivities.EndName);
        Assert.Contains(
            plan[0].Options.Activities,
            activity => activity.Name == InputWaitSignalActivities.Name);
        Assert.Equal("report-data", plan[1].Queue);
        Assert.Empty(plan[1].Options.Workflows);
        Assert.Equal(2, plan[1].Options.Activities.Count);
        Assert.Equal(3, plan[1].Options.MaxConcurrentActivities);

        // The HTML assembly runs on its own queue, in this process: exactly one activity
        // (AssembleHtml), no workflow — an HTML-only job schedules no PDF-queue work (AC-4).
        Assert.Equal("report-html", plan[2].Queue);
        Assert.Empty(plan[2].Options.Workflows);
        Assert.Single(plan[2].Options.Activities);
        Assert.Contains(
            plan[2].Options.Activities,
            activity => activity.Name == HtmlOutputStepContracts.AssembleHtml);
    }

    [Fact]
    public void ARegistrationOnAQueueTheProcessDoesNotDeclareStopsStartUp()
    {
        var error = Assert.Throws<ConfigurationError>(() => Plan(["report-orchestration", "report-data"], "report-pdf-rendering"));

        Assert.Contains("report-pdf-rendering", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("3", error.Message, StringComparison.Ordinal);
    }

    private IReadOnlyList<PlannedWorker> Plan(IReadOnlyList<string> declared, params string[] extraQueues)
    {
        var registrations = Registrations(extraQueues);
        return WorkerPlan.Plan(declared, registrations);
    }

    private IReadOnlyList<TaskQueueWork> Registrations(params string[] extraQueues)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton(_rig.Verifier);
        builder.Services.AddSingleton<TimeProvider>(_rig.Clock);
        builder.Services.AddSingleton<IJobStore>(new InMemoryJobStore(_rig.Uploads));
        builder.Services.AddSingleton<IUploadStore>(_rig.Uploads);
        builder.Services.AddSingleton<IObjectStore>(_rig.Objects);
        builder.Services.AddSingleton<ITemplateVersionStore>(new InMemoryTemplateVersionStore());
        builder.Services.AddSingleton(MeterIdentity.ForHost(TelemetryDefaults.MeterName));
        builder.Services.AddSingleton<OrchestrationMetrics>();
        builder.Configuration[$"{WorkerOptions.SectionPath}:{WorkerOptions.DataActivitySlotsSetting}"] = "3";
        builder.Configuration[$"{UploadVerificationOptions.SectionPath}:{UploadVerificationOptions.StartToCloseSetting}"] = "00:30:00";
        builder.Configuration[$"{VerificationOptions.SectionPath}:{VerificationOptions.VerificationDeadlineSetting}"] = "06:00:00";
        builder.AddValidatedConfiguration<VerificationOptions>(VerificationOptions.SectionPath);

        // The notification of waiting jobs goes through the engine connection the worker holds,
        // and this container is built without the engine host: the holder is what the seam needs.
        builder.Services.TryAddSingleton<TemporalClientHolder>();
        builder.AddUploadVerificationWork();
        builder.AddReportJobWork();
        foreach (var queue in extraQueues)
        {
            builder.Services.AddTaskQueueWork(queue, options => options.MaxConcurrentActivities = 1);
        }

        _host = builder.Build();
        return [.. _host.Services.GetRequiredService<IEnumerable<TaskQueueWork>>()];
    }
}

/// <summary>
/// The settings the dispatcher and the verification bring: each at, below and above
/// its bound, and absent — always refused by naming the setting and never the value.
/// </summary>
public sealed class OrchestrationWorkerOptionsTests
{
    [Fact]
    public void TheVerificationBoundIsRequiredAndCapped()
    {
        Assert.NotEmpty(new UploadVerificationOptions().Validate());
        Assert.NotEmpty(new UploadVerificationOptions { StartToClose = TimeSpan.Zero }.Validate());
        Assert.NotEmpty(new UploadVerificationOptions { StartToClose = TimeSpan.FromDays(2) }.Validate());
        Assert.Empty(new UploadVerificationOptions { StartToClose = TimeSpan.FromHours(1) }.Validate());
    }

    [Fact]
    public void TheDataSlotsAreBounded()
    {
        Assert.NotEmpty(new WorkerOptions { DataActivitySlots = 0 }.Validate());
        Assert.NotEmpty(new WorkerOptions { DataActivitySlots = 65 }.Validate());
        Assert.Empty(new WorkerOptions { DataActivitySlots = 2 }.Validate());
    }

    [Theory]
    [InlineData(0, 50, "00:05:00", "00:00:30")]
    [InlineData(1, 0, "00:05:00", "00:00:30")]
    [InlineData(1, 501, "00:05:00", "00:00:30")]
    [InlineData(1, 50, "00:00:00", "00:00:30")]
    [InlineData(1, 50, "00:05:00", "00:00:04")]
    [InlineData(1, 50, "00:05:00", "00:01:00")]
    public void EveryDispatcherSettingIsValidatedAtItsBounds(int seconds, int batchSize, string alertAfter, string claimBudget)
    {
        var options = new DispatcherOptions
        {
            PollInterval = seconds == 0 ? TimeSpan.Zero : TimeSpan.FromSeconds(1),
            BatchSize = batchSize,
            AlertAfter = TimeSpan.Parse(alertAfter, System.Globalization.CultureInfo.InvariantCulture),
            ClaimBudget = TimeSpan.Parse(claimBudget, System.Globalization.CultureInfo.InvariantCulture),
        };

        Assert.NotEmpty(options.Validate());
    }

    [Fact]
    public void ValuesInRangeAreAccepted()
    {
        var options = new DispatcherOptions
        {
            PollInterval = TimeSpan.FromSeconds(1),
            BatchSize = 50,
            AlertAfter = TimeSpan.FromMinutes(5),
            ClaimBudget = TimeSpan.FromSeconds(30),
        };

        Assert.Empty(options.Validate());
        Assert.NotEmpty(new DispatcherOptions { PollInterval = TimeSpan.FromMilliseconds(50) }.Validate());
        Assert.NotEmpty(new DispatcherOptions { PollInterval = TimeSpan.FromMinutes(2) }.Validate());
    }
}

/// <summary>
/// The template step's registration and the graph it leaves: its three metadata activities
/// run inside the one <c>report-orchestration</c> registration, nothing new is registered on
/// the PDF worker's queue, and neither the orchestration worker nor the job library can
/// reach the template source - the version that is not taken is the PDF worker's take, and
/// this process only ever writes what that answer said.
/// </summary>
public sealed class TemplateStepRegistrationTests
{
    [Fact]
    public void TheThreeMetadataStepsAreInsideTheOneOrchestrationRegistration()
    {
        var host = Host.CreateApplicationBuilder();
        host.Services.AddSingleton<IJobStore>(new InMemoryJobStore(new InMemoryUploadStore()));
        host.Services.AddSingleton<IUploadStore>(new InMemoryUploadStore());
        host.Services.AddSingleton<ITemplateVersionStore>(new InMemoryTemplateVersionStore());
        host.Services.AddSingleton(TimeProvider.System);
        host.Services.AddSingleton(MeterIdentity.ForHost(TelemetryDefaults.MeterName));
        host.Services.AddSingleton<OrchestrationMetrics>();
        host.Configuration[$"{WorkerOptions.SectionPath}:{WorkerOptions.DataActivitySlotsSetting}"] = "3";
        host.Services.TryAddSingleton<TemporalClientHolder>();
        host.AddReportJobWork();

        using var built = host.Build();
        var registrations = built.Services.GetRequiredService<IEnumerable<TaskQueueWork>>().ToArray();
        var orchestration = registrations.Single(registration =>
            string.Equals(registration.Queue, ReportJobWorkflow.OrchestrationQueue, StringComparison.Ordinal));
        var options = new Temporalio.Worker.TemporalWorkerOptions(orchestration.Queue);
        orchestration.Configure(options);

        var names = options.Activities.Select(activity => activity.Name).ToArray();
        Assert.Contains(TemplateResolutionActivities.PrepareTemplateName, names);
        Assert.Contains(TemplateResolutionActivities.PinTemplateName, names);
        Assert.Contains(TemplateResolutionActivities.PinBaselineName, names);

        // The template step registers nothing on the PDF worker's queue: the take and the
        // resolution are that worker's own work, and this one only calls them by name.
        Assert.DoesNotContain(
            registrations,
            registration => string.Equals(registration.Queue, TakeTemplateStepOptions.Queue, StringComparison.Ordinal));
    }

    [Fact]
    public void TheJobLibraryAndTheOrchestrationWorkerReachNoSourceClientAndNoEngineOfAnotherProcess()
    {
        var jobLibrary = typeof(IJobStore).Assembly.GetReferencedAssemblies().Select(reference => reference.Name).ToArray();
        var orchestration = typeof(TemplateResolutionActivities).Assembly.GetReferencedAssemblies().Select(reference => reference.Name).ToArray();

        Assert.Contains("ReportService.TemplatePackages", jobLibrary);
        Assert.DoesNotContain("ReportService.TemplateSource", jobLibrary);
        Assert.DoesNotContain("ReportService.TemplateSource", orchestration);
        Assert.DoesNotContain("ReportService.Workers.Pdf", orchestration);
    }
}
