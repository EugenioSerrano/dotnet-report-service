using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ReportService.ServiceDefaults;
using ReportService.ServiceDefaults.Temporal;
using ReportService.Testing;
using ReportService.Uploads;
using ReportService.Workers.Orchestration;
using ReportService.Workers.Orchestration.Dispatch;
using ReportService.Workers.Orchestration.Telemetry;
using ReportService.Workers.Orchestration.Uploads;

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
    private readonly OrchestrationMetrics _metrics = new();
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

    private IntentDispatcher Dispatcher(
        int batchSize = 50,
        TimeSpan? verificationBound = null) =>
        new(
            _store,
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

        public int Starts { get; private set; }

        public Task<WorkflowStart?> DescribeAsync(string workflowId, CancellationToken cancellationToken)
        {
            var count = ++_describes;
            return Task.FromResult(OnDescribe is null ? Described : OnDescribe(count, workflowId));
        }

        public Task<WorkflowStart> StartAsync(string workflowId, string uploadId, CancellationToken cancellationToken)
        {
            Starts++;
            return OnStart is null
                ? Task.FromResult(new WorkflowStart(workflowId, "run-default"))
                : Task.FromResult(OnStart(workflowId, uploadId));
        }
    }

    // The connection the dispatcher checks for; the starter is scripted, so no client is ever used.
    private sealed class FakeEngineConnection : IEngineConnection
    {
        public Temporalio.Client.ITemporalClient Client => null!;

        public Task<bool> CheckHealthAsync(TimeSpan timeout, CancellationToken cancellationToken) => Task.FromResult(true);

        public Task<string> ServerVersionAsync(CancellationToken cancellationToken) => Task.FromResult("test");
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
    public void BothDeclaredQueuesGetExactlyOneWorkerWithItsOwnRegistration()
    {
        var plan = Plan(["report-orchestration", "report-data"]);

        Assert.Equal(2, plan.Count);
        Assert.Equal("report-orchestration", plan[0].Queue);
        Assert.Single(plan[0].Options.Workflows);
        Assert.Empty(plan[0].Options.Activities);
        Assert.Equal("report-data", plan[1].Queue);
        Assert.Empty(plan[1].Options.Workflows);
        Assert.Single(plan[1].Options.Activities);
        Assert.Equal(3, plan[1].Options.MaxConcurrentActivities);
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
        builder.Configuration[$"{WorkerOptions.SectionPath}:{WorkerOptions.DataActivitySlotsSetting}"] = "3";
        builder.Configuration[$"{UploadVerificationOptions.SectionPath}:{UploadVerificationOptions.StartToCloseSetting}"] = "00:30:00";
        builder.Configuration[$"{VerificationOptions.SectionPath}:{VerificationOptions.VerificationDeadlineSetting}"] = "06:00:00";
        builder.AddValidatedConfiguration<VerificationOptions>(VerificationOptions.SectionPath);
        builder.AddUploadVerificationWork();
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
