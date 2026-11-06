extern alias orchestration;

using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using ReportService.Api.Consumers;
using ReportService.Api.Http;
using ReportService.Api.Jobs;
using ReportService.Api.Uploads;
using ReportService.Jobs;
using ReportService.Migrations;
using ReportService.ServiceDefaults;
using ReportService.ServiceDefaults.Temporal;
using ReportService.Storage;
using ReportService.Storage.Filesystem;
using ReportService.Testing;
using ReportService.Uploads;
using Temporalio.Api.Enums.V1;
using Temporalio.Client;
using Temporalio.Common;
using OrchestrationWorker = orchestration::ReportService.Workers.Orchestration.OrchestrationWorkerProgram;
using UploadVerificationActivityOptions = orchestration::ReportService.Workers.Orchestration.Uploads.UploadVerificationActivityOptions;
using InputWaitOptions = orchestration::ReportService.Workers.Orchestration.Jobs.InputWaitOptions;
using InputWaitHistoryBudget = orchestration::ReportService.Workers.Orchestration.Jobs.InputWaitHistoryBudget;
using UploadExpiryOptions = orchestration::ReportService.Workers.Orchestration.Uploads.UploadExpiryOptions;
using UploadVerificationOptions = orchestration::ReportService.Workers.Orchestration.Uploads.UploadVerificationOptions;
using VerificationOptions = orchestration::ReportService.Workers.Orchestration.Uploads.VerificationOptions;
using ReportJobActivities = orchestration::ReportService.Workers.Orchestration.Jobs.ReportJobActivities;
using ReportJobWorkflow = orchestration::ReportService.Workers.Orchestration.Jobs.ReportJobWorkflow;
using InputWaitActivities = orchestration::ReportService.Workers.Orchestration.Jobs.InputWaitActivities;

namespace ReportService.IntegrationTests.Jobs;

/// <summary>
/// A job that waits for the upload it reports on, against real PostgreSQL, a real engine
/// and real storage: a job accepted on a confirming upload is queued with the waiting stage
/// and starts nothing else; a finished verification nudges it and no timer fires; a lost
/// nudge costs one timer; a rejected upload ends the job with the upload's own code; a
/// deadline that passes ends it as not found, swept or not; a session that ends while the
/// upload still verifies changes nothing; a restarted worker keeps the wait; two jobs of one
/// upload both follow it; and a wait that outlasts its checks continues as a new run.
/// <para>
/// The API and the worker are the product's own registrations hosted in this process, and
/// the verification's work item is withheld behind a gate the case opens when it wants the
/// upload settled — the same way a deployment that has not started the data work behaves.
/// </para>
/// </summary>
public sealed class JobInputWaitTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private const string Consumer = "dev-consumer";

    private static readonly TimeSpan ReadyDeadline = TimeSpan.FromSeconds(90);

    private static readonly TimeSpan DispatchDeadline = TimeSpan.FromSeconds(90);

    private static readonly TimeSpan WaitDeadline = TimeSpan.FromSeconds(120);

    private readonly PostgresFixture _fixture;

    private readonly PinnedImage _engineImage = CompositionPins.Load().WorkflowEngine;

    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "report-service-job-input-wait",
        Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));

    private int _hostPort;

    private EngineStart.Engine? _engine;

    private string? _database;

    private string? _connectionString;

    private NpgsqlDataSource? _dataSource;

    private RenderWorkerHost? _render;

    /// <summary>Runs against the class's server.</summary>
    /// <param name="fixture">The container this class owns.</param>
    public JobInputWaitTests(PostgresFixture fixture) => _fixture = fixture;

    /// <inheritdoc />
    public async ValueTask InitializeAsync()
    {
        try
        {
            await OpenAsync();
        }
        catch
        {
            await DisposeAsync();
            throw;
        }
    }

    private async Task OpenAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        Directory.CreateDirectory(_directory);
        _database = await _fixture.CreateDatabaseAsync(cancellationToken);
        _connectionString = new NpgsqlConnectionStringBuilder(_fixture.Application(_database))
        {
            Timeout = 5,
            CommandTimeout = PostgresJobStore.CommandTimeoutSeconds,
        }.ConnectionString;

        var migration = await MigrationRunner.RunAsync(_connectionString, MigrationCatalog.Scripts, cancellationToken);
        Assert.True(migration.Succeeded, string.Join("; ", migration.ErrorLines));
        _dataSource = NpgsqlDataSource.Create(_connectionString);

        _engine = await EngineStart.StartAsync(_engineImage, "temporal-job-input-wait", cancellationToken);
        _hostPort = _engine.HostPort;

        _render = await RenderWorkerHost.StartAsync(
            _hostPort,
            _connectionString!,
            StorageRoot(),
            "pilot-grouped",
            ReadyDeadline,
            null,
            cancellationToken);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_render is not null)
        {
            await _render.DisposeAsync();
        }

        if (_dataSource is not null)
        {
            await _dataSource.DisposeAsync();
        }

        if (_engine is not null)
        {
            await _engine.Container.DisposeAsync();
        }

        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temporary directory is not a test failure.
        }
    }

    [Fact]
    public async Task AJobOnAConfirmingUploadIsQueuedWithoutAStageAndThenWaitsAndStartsNothingElse()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var uploadId = await GivenUploadAsync(ValidBody(), UploadContentEncoding.None, deadline: TimeSpan.FromHours(6), cancellationToken);
        await WithdrawVerificationRequestAsync(uploadId, cancellationToken);
        var reportJobId = await AcceptJobAsync(uploadId, cancellationToken);
        var workflowId = ReportJobWorkflow.WorkflowIdFor(reportJobId);

        // The API answered 202 while nothing hosts the start: the job is queued with no stage
        // yet, its start request is still pending and no execution exists.
        var before = await JobAsync(reportJobId, cancellationToken);
        Assert.Equal("QUEUED", before!.Status);
        Assert.Null(before.Stage);
        Assert.Equal("PENDING", (await IntentAsync(reportJobId, cancellationToken))!.Status);
        Assert.Null(await ExecutionAsync(workflowId, cancellationToken));

        await using var worker = Worker(new Settings().WithBackstop(TimeSpan.FromMinutes(5)));
        using var workerClient = worker.CreateClient();
        await WaitForStatusAsync(workerClient, HttpStatusCode.OK, cancellationToken);
        await WaitForStageAsync(reportJobId, InputWaitStage.WaitingForInput, cancellationToken);

        Assert.Equal("QUEUED", (await JobAsync(reportJobId, cancellationToken))!.Status);
        Assert.NotNull(await ExecutionAsync(workflowId, cancellationToken));
        var history = await HistoryAsync(workflowId, cancellationToken);
        var scheduled = ActivitiesOf(history);
        Assert.Contains(InputWaitActivities.BeginName, scheduled);
        Assert.DoesNotContain(ReportJobActivities.FailJobName, scheduled);

        // Nothing of the steps that follow the wait is scheduled while the upload verifies.
        Assert.All(
            scheduled,
            name => Assert.True(
                name is ReportJobActivities.ResolveInputName or InputWaitActivities.BeginName or InputWaitActivities.EndName,
                $"the run scheduled '{name}' while the upload was still verifying"));
    }

    [Fact]
    public async Task AFinishedVerificationNudgesTheWaitAndNoTimerFires()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var uploadId = await GivenUploadAsync(ValidBody(), UploadContentEncoding.None, deadline: TimeSpan.FromHours(6), cancellationToken);
        var reportJobId = await AcceptJobAsync(uploadId, cancellationToken);
        var workflowId = ReportJobWorkflow.WorkflowIdFor(reportJobId);

        var settings = new Settings().WithBackstop(TimeSpan.FromMinutes(5));
        await using var worker = Worker(settings);
        using var workerClient = worker.CreateClient();
        await WaitForStatusAsync(workerClient, HttpStatusCode.OK, cancellationToken);
        await WaitForStageAsync(reportJobId, InputWaitStage.WaitingForInput, cancellationToken);

        // Hosting the verification's work is what settles the upload: the workflow schedules
        // the nudge of its waiting jobs, and that nudge is what ends the wait.
        worker.ReleaseVerification();
        await WaitForUploadAsync(uploadId, UploadStatus.Ready, cancellationToken);
        await WaitForStageAsync(reportJobId, stage: null, cancellationToken);
        await WaitForUploadToSettleAsync(uploadId, cancellationToken);

        var history = await HistoryAsync(workflowId, cancellationToken);
        var signal = history.Events.Single(entry => entry.EventType == EventType.WorkflowExecutionSignaled);
        var readAfterTheSignal = history.Events
            .Where(entry => entry.EventType == EventType.ActivityTaskScheduled)
            .Where(entry => entry.EventId > signal.EventId)
            .Where(entry => entry.ActivityTaskScheduledEventAttributes.ActivityType.Name == ReportJobActivities.ResolveInputName)
            .Select(entry => entry.EventId)
            .DefaultIfEmpty(long.MaxValue)
            .First();

        // The timer of the pass started before the nudge arrived, and it never fired: the check
        // that follows the signal was caused by the signal. Five minutes could not have passed.
        Assert.DoesNotContain(
            history.Events,
            entry => entry.EventType == EventType.TimerFired && entry.EventId > signal.EventId && entry.EventId < readAfterTheSignal);
        Assert.Single(
            history.Events,
            entry => entry.EventType == EventType.ActivityTaskScheduled
                && entry.ActivityTaskScheduledEventAttributes.ActivityType.Name == InputWaitActivities.EndName);
        Assert.DoesNotContain(
            history.Events,
            entry => entry.EventType == EventType.ActivityTaskScheduled
                && entry.ActivityTaskScheduledEventAttributes.ActivityType.Name == ReportJobActivities.FailJobName);
    }

    [Fact]
    public async Task ALostNotificationCostsExactlyOneTimer()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var uploadId = await GivenUploadAsync(ValidBody(), UploadContentEncoding.None, deadline: TimeSpan.FromHours(6), cancellationToken);

        // The verification's request is withdrawn, so nothing will ever send a nudge: the only
        // thing that can end this wait is the timer the wait itself starts.
        await WithdrawVerificationRequestAsync(uploadId, cancellationToken);
        var reportJobId = await AcceptJobAsync(uploadId, cancellationToken);
        var workflowId = ReportJobWorkflow.WorkflowIdFor(reportJobId);

        await using var worker = Worker(new Settings().WithBackstop(TimeSpan.FromSeconds(1)));
        using var workerClient = worker.CreateClient();
        await WaitForStatusAsync(workerClient, HttpStatusCode.OK, cancellationToken);
        await WaitForStageAsync(reportJobId, InputWaitStage.WaitingForInput, cancellationToken);

        // The upload settles through the product's own record operation, as a verification that
        // could not notify would leave it; no nudge is sent.
        Assert.Equal(SnapshotCommitResult.Committed, await CommitReadyAsync(uploadId, cancellationToken));
        await WaitForStageAsync(reportJobId, stage: null, cancellationToken);

        var history = await HistoryAsync(workflowId, cancellationToken);
        var fired = history.Events.Where(entry => entry.EventType == EventType.TimerFired).Select(entry => entry.EventId).ToList();
        var reads = history.Events
            .Where(entry => entry.EventType == EventType.ActivityTaskScheduled
                && entry.ActivityTaskScheduledEventAttributes.ActivityType.Name == ReportJobActivities.ResolveInputName)
            .Select(entry => entry.EventId)
            .ToList();
        Assert.NotEmpty(fired);
        Assert.Contains(fired, firedId => reads.Any(readId => readId > firedId));
        Assert.Equal("QUEUED", (await JobAsync(reportJobId, cancellationToken))!.Status);
    }

    [Fact]
    public async Task ARejectedUploadEndsTheJobWithTheUploadsOwnCode()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        // A compressed body whose trailer is cut: the attempt refuses it as DATA_INVALID and the
        // upload ends rejected, which is the code the job must carry.
        var uploadId = await GivenUploadAsync(CutGzipBody(), UploadContentEncoding.Gzip, deadline: TimeSpan.FromHours(6), cancellationToken);
        var reportJobId = await AcceptJobAsync(uploadId, cancellationToken);

        await using var worker = Worker(new Settings().WithBackstop(TimeSpan.FromMinutes(5)));
        using var workerClient = worker.CreateClient();
        await WaitForStatusAsync(workerClient, HttpStatusCode.OK, cancellationToken);
        await WaitForStageAsync(reportJobId, InputWaitStage.WaitingForInput, cancellationToken);
        worker.ReleaseVerification();
        await WaitForOutcomeAsync(reportJobId, cancellationToken);

        var job = await JobAsync(reportJobId, cancellationToken);
        Assert.Equal("FAILED", job!.Status);
        Assert.Equal("DATA_INVALID", job.ErrorCode);
        Assert.Equal("input-rejected", job.ErrorReason);
        Assert.Null(job.Stage);

        var output = await OutputAsync(reportJobId, cancellationToken);
        Assert.Equal("FAILED", output!.Status);
        Assert.Equal("DATA_INVALID", output.ErrorCode);
        Assert.Equal(0, await ArtifactsAsync(reportJobId, cancellationToken));
    }

    [Fact]
    public async Task ADeadlineThatPassesEndsTheJobWithoutTheSweep()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var uploadId = await GivenUploadAsync(ValidBody(), UploadContentEncoding.None, deadline: TimeSpan.FromSeconds(3), cancellationToken);

        // The sweep is left out of the window (one hour between rounds) and the verification is
        // never hosted: only the job's own read of an overdue upload can end this wait.
        var settings = new Settings()
            .WithBackstop(TimeSpan.FromSeconds(1))
            .WithDeadline(TimeSpan.FromSeconds(3))
            .WithStartToClose(TimeSpan.FromSeconds(1))
            .WithSweepInterval(TimeSpan.FromHours(1));
        var reportJobId = await AcceptJobAsync(uploadId, cancellationToken);

        await using var worker = Worker(settings);
        using var workerClient = worker.CreateClient();
        await WaitForStatusAsync(workerClient, HttpStatusCode.OK, cancellationToken);
        await WaitForOutcomeAsync(reportJobId, cancellationToken);

        var job = await JobAsync(reportJobId, cancellationToken);
        Assert.Equal("FAILED", job!.Status);
        Assert.Equal("INPUT_NOT_FOUND", job.ErrorCode);
        Assert.Equal("input-expired", job.ErrorReason);
        Assert.Equal("VERIFYING", await UploadStatusAsync(uploadId, cancellationToken));
    }

    [Fact]
    public async Task TheSweepExpiresTheUploadAndTheJobEndsAsNotFound()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var uploadId = await GivenUploadAsync(ValidBody(), UploadContentEncoding.None, deadline: TimeSpan.FromSeconds(3), cancellationToken);

        var settings = new Settings()
            .WithBackstop(TimeSpan.FromSeconds(1))
            .WithDeadline(TimeSpan.FromSeconds(3))
            .WithStartToClose(TimeSpan.FromSeconds(1))
            .WithSweepInterval(TimeSpan.FromSeconds(1));
        var reportJobId = await AcceptJobAsync(uploadId, cancellationToken);

        await using var worker = Worker(settings);
        using var workerClient = worker.CreateClient();
        await WaitForStatusAsync(workerClient, HttpStatusCode.OK, cancellationToken);
        await WaitForOutcomeAsync(reportJobId, cancellationToken);

        var job = await JobAsync(reportJobId, cancellationToken);
        Assert.Equal("FAILED", job!.Status);
        Assert.Equal("INPUT_NOT_FOUND", job.ErrorCode);
        Assert.Equal("input-expired", job.ErrorReason);
        Assert.Equal("EXPIRED", await UploadStatusAsync(uploadId, cancellationToken));
    }

    [Fact]
    public async Task ASessionThatEndsWhileTheUploadStillVerifiesDoesNotEndTheWait()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var settings = new Settings()
            .WithSessionLifetime(TimeSpan.FromSeconds(10))
            .WithDeadline(TimeSpan.FromSeconds(60))
            .WithStartToClose(TimeSpan.FromSeconds(1))
            .WithBackstop(TimeSpan.FromSeconds(1))
            .WithSweepInterval(TimeSpan.FromSeconds(1));

        // The upload is created, streamed and confirmed through the API on the shared clock, so
        // its session ends ten seconds after its confirmation — the product's own lifetime, with
        // no stored value written by the test.
        await using var worker = Worker(settings, clock);
        using var workerClient = worker.CreateClient();
        await WaitForStatusAsync(workerClient, HttpStatusCode.OK, cancellationToken);
        var (uploadId, reportJobId) = await StreamConfirmAndAcceptAsync(clock, settings, cancellationToken);
        await WaitForStageOnTheClockAsync(clock, reportJobId, InputWaitStage.WaitingForInput, cancellationToken);

        // Past the session, still before the verification deadline: the sweep runs, the upload
        // keeps verifying and the job keeps waiting.
        clock.Advance(TimeSpan.FromSeconds(11));
        await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
        var state = await UploadAsync(uploadId, cancellationToken);
        Assert.NotNull(state);
        Assert.Equal("VERIFYING", state!.Status);
        Assert.True(
            state.ExpiresAt < clock.GetUtcNow().UtcDateTime,
            "the session had to be over for the case to mean anything");
        Assert.Equal("QUEUED", (await JobAsync(reportJobId, cancellationToken))!.Status);
        Assert.Equal(InputWaitStage.WaitingForInput, (await JobAsync(reportJobId, cancellationToken))!.Stage);
        var reads = await ResolveInputCountAsync(ReportJobWorkflow.WorkflowIdFor(reportJobId), cancellationToken);
        Assert.True(reads > 1, $"the wait had to check its upload more than once, and it checked it {reads} time(s)");

        // Hosting the verification work afterwards ends the wait with the upload's own outcome.
        worker.ReleaseVerification();
        await WaitForStageAsync(reportJobId, stage: null, cancellationToken);
        Assert.Equal("READY", await UploadStatusAsync(uploadId, cancellationToken));
    }

    [Fact]
    public async Task ARestartedWorkerKeepsTheWaitAndItsOutcomeStillEndsTheJob()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var uploadId = await GivenUploadAsync(ValidBody(), UploadContentEncoding.None, deadline: TimeSpan.FromHours(6), cancellationToken);
        var reportJobId = await AcceptJobAsync(uploadId, cancellationToken);
        var workflowId = ReportJobWorkflow.WorkflowIdFor(reportJobId);

        var settings = new Settings().WithBackstop(TimeSpan.FromMinutes(5));
        var first = Worker(settings);
        using (var client = first.CreateClient())
        {
            await WaitForStatusAsync(client, HttpStatusCode.OK, cancellationToken);
        }

        await WaitForStageAsync(reportJobId, InputWaitStage.WaitingForInput, cancellationToken);
        await first.DisposeAsync();

        await using var second = Worker(settings);
        using var secondClient = second.CreateClient();
        await WaitForStatusAsync(secondClient, HttpStatusCode.OK, cancellationToken);
        await WaitForStageAsync(reportJobId, InputWaitStage.WaitingForInput, cancellationToken);

        Assert.Single(
            (await HistoryAsync(workflowId, cancellationToken)).Events,
            entry => entry.EventType == EventType.WorkflowExecutionStarted);

        second.ReleaseVerification();
        await WaitForStageAsync(reportJobId, stage: null, cancellationToken);
        Assert.Equal("READY", await UploadStatusAsync(uploadId, cancellationToken));
    }

    [Fact]
    public async Task TwoJobsOfOneUploadBothFollowItAndANudgeAfterTheWaitChangesNothing()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var uploadId = await GivenUploadAsync(ValidBody(), UploadContentEncoding.None, deadline: TimeSpan.FromHours(6), cancellationToken);
        var first = await AcceptJobAsync(uploadId, cancellationToken);
        var second = await AcceptJobAsync(uploadId, cancellationToken);

        await using var worker = Worker(new Settings().WithBackstop(TimeSpan.FromMinutes(5)));
        using var workerClient = worker.CreateClient();
        await WaitForStatusAsync(workerClient, HttpStatusCode.OK, cancellationToken);
        await WaitForStageAsync(first, InputWaitStage.WaitingForInput, cancellationToken);
        await WaitForStageAsync(second, InputWaitStage.WaitingForInput, cancellationToken);

        // The two executions that hold the waits, taken while they hold them: the nudge below is
        // judged on the first of these runs, not on whatever run the chain holds later.
        var firstWorkflowId = ReportJobWorkflow.WorkflowIdFor(first);
        var secondWorkflowId = ReportJobWorkflow.WorkflowIdFor(second);
        var firstRun = (await ExecutionAsync(firstWorkflowId, cancellationToken))!.Value.RunId;
        var secondRun = (await ExecutionAsync(secondWorkflowId, cancellationToken))!.Value.RunId;

        worker.ReleaseVerification();
        await WaitForStageAsync(first, stage: null, cancellationToken);
        await WaitForStageAsync(second, stage: null, cancellationToken);

        // The wait has ended and the delivered build takes both jobs past it, so the nudge is
        // judged where it can be: the run that held the wait has closed, the engine refuses to
        // deliver to it, and a closed run cannot gain an event - the history is read once before
        // and once after, with nothing in between to wait for.
        var client = await EngineClientAsync(cancellationToken);
        await WaitUntilRunClosedAsync(client, firstWorkflowId, firstRun, cancellationToken);
        await WaitUntilRunClosedAsync(client, secondWorkflowId, secondRun, cancellationToken);
        var before = await HistoryAsync(firstWorkflowId, firstRun, cancellationToken);
        var refusal = await Assert.ThrowsAsync<Temporalio.Exceptions.RpcException>(
            () => client.GetWorkflowHandle<ReportJobWorkflow>(firstWorkflowId, firstRun)
                .SignalAsync((ReportJobWorkflow workflow) => workflow.OnInputChangedAsync()));
        Assert.Equal(Temporalio.Exceptions.RpcException.StatusCode.NotFound, refusal.Code);
        var after = await HistoryAsync(firstWorkflowId, firstRun, cancellationToken);

        Assert.Equal(
            before.Events.Count(entry => entry.EventType == EventType.ActivityTaskScheduled),
            after.Events.Count(entry => entry.EventType == EventType.ActivityTaskScheduled));
        Assert.Equal(1, after.Events.Count(entry => entry.EventType == EventType.ActivityTaskScheduled
            && entry.ActivityTaskScheduledEventAttributes.ActivityType.Name == InputWaitActivities.EndName));

        // Both jobs followed the upload out of the wait and ended on their own run's outcome.
        var firstJob = (await JobAsync(first, cancellationToken))!;
        var secondJob = (await JobAsync(second, cancellationToken))!;
        Assert.Contains(firstJob.Status, new[] { "SUCCEEDED", "FAILED" });
        Assert.NotNull(firstJob.FinishedAt);
        Assert.Contains(secondJob.Status, new[] { "SUCCEEDED", "FAILED" });
        Assert.NotNull(secondJob.FinishedAt);
    }

    [Fact]
    public async Task AWaitThatOutlastsItsChecksContinuesAsANewRunAndIsStillWoken()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var uploadId = await GivenUploadAsync(ValidBody(), UploadContentEncoding.None, deadline: TimeSpan.FromHours(6), cancellationToken);
        var reportJobId = await AcceptJobAsync(uploadId, cancellationToken);
        var workflowId = ReportJobWorkflow.WorkflowIdFor(reportJobId);

        var settings = new Settings()
            .WithBackstop(TimeSpan.FromSeconds(1))
            .WithContinueAsNewAfter(2);
        await using var worker = Worker(settings);
        using var workerClient = worker.CreateClient();
        await WaitForStatusAsync(workerClient, HttpStatusCode.OK, cancellationToken);
        await WaitForStageAsync(reportJobId, InputWaitStage.WaitingForInput, cancellationToken);

        var description = (RunId: string.Empty, FirstRunId: await FirstRunIdAsync(workflowId, cancellationToken));
        await Wait.UntilAsync(
            $"the run of {workflowId} to continue as a new one",
            async token =>
            {
                var client = await EngineClientAsync(token);
                var current = await client.GetWorkflowHandle(workflowId).DescribeAsync().WaitAsync(token);
                description = (current.RunId, description.FirstRunId);
                return !string.Equals(current.RunId, description.FirstRunId, StringComparison.Ordinal);
            },
            WaitDeadline,
            TimeSpan.FromMilliseconds(250),
            cancellationToken);

        Assert.NotEqual(description.FirstRunId, description.RunId);
        Assert.Equal("QUEUED", (await JobAsync(reportJobId, cancellationToken))!.Status);
        Assert.Equal(InputWaitStage.WaitingForInput, (await JobAsync(reportJobId, cancellationToken))!.Stage);

        // Each run's history stays inside the relationship the configured limit promises, and no
        // run continued after another run was started.
        foreach (var runId in new[] { description.FirstRunId, description.RunId })
        {
            var run = await HistoryAsync(workflowId, runId, cancellationToken);
            Assert.True(run.Events.Count <= (2 * InputWaitHistoryBudget.WorstCaseEventsPerPass) + InputWaitHistoryBudget.PrefixEvents);
        }

        // The nudge across the continuation is not lost: the continued run still leaves the wait.
        worker.ReleaseVerification();
        await WaitForStageAsync(reportJobId, stage: null, cancellationToken);
    }

    private static List<string> ActivitiesOf(WorkflowHistory history) =>
    [
        .. history.Events
            .Where(entry => entry.EventType == EventType.ActivityTaskScheduled)
            .Select(entry => entry.ActivityTaskScheduledEventAttributes.ActivityType.Name),
    ];

    private static byte[] ValidBody() =>
        Encoding.UTF8.GetBytes("""{"header":{},"items":[{"id":1,"v":17},{"id":2,"v":34}]}""");

    private static byte[] CutGzipBody()
    {
        using var buffer = new MemoryStream();
        using (var gzip = new GZipStream(buffer, CompressionLevel.Fastest, leaveOpen: true))
        {
            gzip.Write(Encoding.UTF8.GetBytes("""{"header":{},"items":[{"id":1,"v":17}]}"""));
        }

        return [.. buffer.ToArray()[..^4]];
    }

    private Task<TemporalClient> EngineClientAsync(CancellationToken cancellationToken) =>
        TemporalClient.ConnectAsync(new TemporalClientConnectOptions($"127.0.0.1:{_hostPort}") { Namespace = "default" })
            .WaitAsync(cancellationToken);

    private async Task<string> GivenUploadAsync(byte[] stored, UploadContentEncoding encoding, TimeSpan deadline, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var uploadId = UploadIdentifier.Prefix + Convert.ToHexStringLower(Guid.NewGuid().ToByteArray());
        var objects = FilesystemObjectStore.Open(
            new FilesystemStorageOptions { Root = StorageRoot(), CreateIfMissing = true },
            StorageRoot(),
            TimeProvider.System,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<FilesystemObjectStore>.Instance);
        var stagingKey = ObjectKey.FromSegments("staging", uploadId, "content");
        var written = await objects.WriteAsync(stagingKey, new MemoryStream(stored, writable: false), cancellationToken);
        Assert.True(written.Succeeded, "the body of the case had to be staged");

        var uploads = new PostgresUploadStore(_dataSource!);
        var record = new UploadRecord(
            uploadId,
            Consumer,
            UploadMode.ApiStream,
            DataProfile.HeaderItemsV1,
            UploadStatus.Created,
            stagingKey,
            now,
            now.AddHours(1));
        await uploads.CreateAsync(record, cancellationToken);
        Assert.True(await uploads.TryStartTransferAsync(uploadId, Consumer, encoding, now, cancellationToken));
        Assert.True(await uploads.RecordTransferAsync(
            uploadId, UploadContentOutcome.Complete, written.Value!.Size, written.Value.Size, written.Value.Sha256, now, cancellationToken));

        // The upload is confirmed with the deadline of the case; the verification deadline is the
        // confirmation's own, which is what bounds the wait.
        Assert.True(await uploads.ConfirmAsync(uploadId, Consumer, null, now, now, now.Add(deadline), cancellationToken));
        return uploadId;
    }

    // The confirmation left a verification request behind; withdrawing it keeps the upload
    // confirming for as long as the case needs, exactly as a deployment without the data work
    // would leave it.
    private async Task WithdrawVerificationRequestAsync(string uploadId, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource!.OpenConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM report_service.intents WHERE kind = 'start-upload-verification' AND subject_id = @UploadId",
            new { UploadId = uploadId },
            cancellationToken: cancellationToken));
    }

    private async Task<SnapshotCommitResult> CommitReadyAsync(string uploadId, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var uploads = new PostgresUploadStore(_dataSource!);
        return await uploads.CommitReadyAsync(
            new SnapshotRecord(
                uploadId,
                Consumer,
                ObjectKey.FromSegments("snapshots", uploadId, "a"),
                UploadContentEncoding.None,
                DataProfile.HeaderItemsV1,
                new string('a', 64),
                900,
                new string('a', 64),
                900,
                now),
            now,
            cancellationToken);
    }

    private async Task<(string UploadId, string ReportJobId)> StreamConfirmAndAcceptAsync(
        ManualTimeProvider clock,
        Settings settings,
        CancellationToken cancellationToken)
    {
        await using var api = Api(clock, settings);
        using var client = api.CreateClient();

        var uploadId = await CreateUploadAsync(client, cancellationToken);
        using (var content = new ByteArrayContent(ValidBody()))
        {
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(CreateUploadRequestReader.JsonMediaType);
            using var request = new HttpRequestMessage(
                HttpMethod.Put,
                new Uri($"{UploadEndpoints.RoutePrefix}/{uploadId}/content", UriKind.Relative))
            {
                Content = content,
            };
            request.Headers.TryAddWithoutValidation(ConsumerIdentity.HeaderName, Consumer);
            using var sent = await client.SendAsync(request, cancellationToken);
            Assert.Equal(HttpStatusCode.OK, sent.StatusCode);
        }

        using (var completed = await client.SendAsync(
            new HttpRequestMessage(HttpMethod.Post, new Uri($"{UploadEndpoints.RoutePrefix}/{uploadId}/complete", UriKind.Relative))
            {
                Content = new StringContent("{}", Encoding.UTF8, CreateUploadRequestReader.JsonMediaType),
                Headers = { { ConsumerIdentity.HeaderName, Consumer } },
            },
            cancellationToken))
        {
            Assert.Equal(HttpStatusCode.Accepted, completed.StatusCode);
        }

        var reportJobId = await AcceptJobAsync(client, uploadId, cancellationToken);
        return (uploadId, reportJobId);
    }

    private async Task<string> CreateUploadAsync(HttpClient client, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(UploadEndpoints.RoutePrefix, UriKind.Relative))
        {
            Content = new StringContent(
                """{"profile":"header-items-v1","mode":"API_STREAM"}""",
                Encoding.UTF8,
                CreateUploadRequestReader.JsonMediaType),
        };
        request.Headers.TryAddWithoutValidation(ConsumerIdentity.HeaderName, Consumer);
        using var response = await client.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("uploadId").GetString()!;
    }

    private async Task<string> AcceptJobAsync(string uploadId, CancellationToken cancellationToken)
    {
        await using var api = Api(clock: null, settings: new Settings());
        using var client = api.CreateClient();
        return await AcceptJobAsync(client, uploadId, cancellationToken);
    }

    private static async Task<string> AcceptJobAsync(HttpClient client, string uploadId, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(CreateReportJobEndpoint.RoutePrefix, UriKind.Relative))
        {
            Content = new StringContent(
                $$"""{"template":{"id":"pilot-grouped"},"data":{"uploadId":"{{uploadId}}","profile":"header-items-v1"},"outputs":["pdf"]}""",
                Encoding.UTF8,
                "application/json"),
        };
        request.Headers.TryAddWithoutValidation(ConsumerIdentity.HeaderName, Consumer);
        using var response = await client.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        using var document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("reportJobId").GetString()!;
    }

    private string StorageRoot() => Path.Combine(_directory, "storage");

    private WebApplicationFactory<Program> Api(ManualTimeProvider? clock, Settings settings)
    {
        var registry = Path.Combine(_directory, "consumers.json");
        File.WriteAllText(registry, """{ "consumers": [ { "consumerId": "dev-consumer", "active": true } ] }""");
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting(ConsumerRegistryHosting.FileSetting, registry);
            builder.UseSetting("Storage:Filesystem:Root", StorageRoot());
            builder.UseSetting(DatabaseOptions.ConnectionStringSetting, _connectionString);
            builder.UseSetting(DatabaseOptions.ExpectedSchemaVersionSetting, MigrationCatalog.ExpectedVersion);
            builder.UseSetting(
                $"{UploadOptions.SectionPath}:{UploadOptions.SessionLifetimeSetting}",
                settings.SessionLifetimeText);
            foreach (var (setting, value) in settings.Values)
            {
                builder.UseSetting(setting, value);
            }

            if (clock is not null)
            {
                builder.ConfigureTestServices(services => services
                    .RemoveAll<TimeProvider>()
                    .AddSingleton<TimeProvider>(clock));
            }
        });
    }

    private JobWorker Worker(Settings settings, ManualTimeProvider? clock = null) => new(this, settings, clock);

    private async Task WaitForStatusAsync(HttpClient client, HttpStatusCode expected, CancellationToken cancellationToken) =>
        await Wait.UntilAsync(
            $"/health answering {(int)expected}",
            async token =>
            {
                using var response = await client.GetAsync(new Uri("/health", UriKind.Relative), token);
                return response.StatusCode == expected;
            },
            ReadyDeadline,
            TimeSpan.FromMilliseconds(500),
            cancellationToken);

    private Task WaitForStageAsync(string reportJobId, string? stage, CancellationToken cancellationToken) =>
        Wait.UntilAsync(
            $"the stage of {reportJobId} to be {(stage ?? "cleared")}",
            async token => string.Equals((await JobAsync(reportJobId, token))?.Stage, stage, StringComparison.Ordinal),
            WaitDeadline,
            TimeSpan.FromMilliseconds(250),
            cancellationToken);

    // The dispatcher's poll is a timer on the case's own clock, so a case that runs the worker
    // on that clock has to move it for the pending start to be claimed. It moves by one poll
    // interval at a time and no more than four times, so the case stays well inside the
    // upload's session and its verification deadline.
    private async Task WaitForStageOnTheClockAsync(ManualTimeProvider clock, string reportJobId, string? stage, CancellationToken cancellationToken)
    {
        var steps = 4;
        await Wait.UntilAsync(
            $"the stage of {reportJobId} to be {(stage ?? "cleared")}",
            async token =>
            {
                if (steps > 0)
                {
                    steps--;
                    clock.Advance(TimeSpan.FromSeconds(1));
                }

                return string.Equals((await JobAsync(reportJobId, token))?.Stage, stage, StringComparison.Ordinal);
            },
            WaitDeadline,
            TimeSpan.FromMilliseconds(250),
            cancellationToken);
    }

    private Task WaitForOutcomeAsync(string reportJobId, CancellationToken cancellationToken) =>
        Wait.UntilAsync(
            $"the job {reportJobId} to end",
            async token => (await JobAsync(reportJobId, token))?.Status is "FAILED" or "SUCCEEDED" or "CANCELLED",
            WaitDeadline,
            TimeSpan.FromMilliseconds(250),
            cancellationToken);

    // The upload left the wait: the notification reached the job's run.
    private Task WaitForUploadToSettleAsync(string uploadId, CancellationToken cancellationToken) =>
        Wait.UntilAsync(
            $"the upload {uploadId} to settle",
            async token => await UploadStatusAsync(uploadId, token) is "READY" or "REJECTED" or "EXPIRED",
            WaitDeadline,
            TimeSpan.FromMilliseconds(250),
            cancellationToken);

    private Task WaitForUploadAsync(string uploadId, UploadStatus status, CancellationToken cancellationToken) =>
        Wait.UntilAsync(
            $"the upload {uploadId} to be {status}",
            async token => string.Equals(await UploadStatusAsync(uploadId, token), UploadWireValues.ToStoredText(status), StringComparison.Ordinal),
            WaitDeadline,
            TimeSpan.FromMilliseconds(250),
            cancellationToken);

    // The run closes when its own outcome has been written; a closed run refuses a signal with
    // NotFound, and its history no longer changes. The wait is on that state, never on a clock.
    private static async Task WaitUntilRunClosedAsync(
        TemporalClient client,
        string workflowId,
        string runId,
        CancellationToken cancellationToken) =>
        await Wait.UntilAsync(
            $"the run {runId} of {workflowId} to close",
            async token =>
            {
                var description = await client.GetWorkflowHandle<ReportJobWorkflow>(workflowId, runId)
                    .DescribeAsync().WaitAsync(token);
                return description.Status != Temporalio.Api.Enums.V1.WorkflowExecutionStatus.Running;
            },
            DispatchDeadline,
            TimeSpan.FromMilliseconds(250),
            cancellationToken);

    private async Task<(string WorkflowId, string RunId)?> ExecutionAsync(string workflowId, CancellationToken cancellationToken)
    {
        var client = await EngineClientAsync(cancellationToken);
        try
        {
            var description = await client.GetWorkflowHandle(workflowId).DescribeAsync().WaitAsync(cancellationToken);
            return (workflowId, description.RunId);
        }
        catch (Temporalio.Exceptions.RpcException exception) when ((int)exception.Code == 5)
        {
            return null;
        }
    }

    private async Task<WorkflowHistory> HistoryAsync(string workflowId, CancellationToken cancellationToken)
    {
        var client = await EngineClientAsync(cancellationToken);
        return await client.GetWorkflowHandle(workflowId)
            .FetchHistoryAsync(new WorkflowHistoryEventFetchOptions())
            .WaitAsync(cancellationToken);
    }

    private async Task<WorkflowHistory> HistoryAsync(string workflowId, string runId, CancellationToken cancellationToken)
    {
        var client = await EngineClientAsync(cancellationToken);
        return await client.GetWorkflowHandle<ReportJobWorkflow>(workflowId, runId)
            .FetchHistoryAsync(new WorkflowHistoryEventFetchOptions())
            .WaitAsync(cancellationToken);
    }

    private async Task<int> ResolveInputCountAsync(string workflowId, CancellationToken cancellationToken) =>
        (await HistoryAsync(workflowId, cancellationToken)).Events.Count(entry => entry.EventType == EventType.ActivityTaskScheduled
            && entry.ActivityTaskScheduledEventAttributes.ActivityType.Name == ReportJobActivities.ResolveInputName);

    private async Task<JobRow?> JobAsync(string reportJobId, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource!.OpenConnectionAsync(cancellationToken);
        return await connection.QuerySingleOrDefaultAsync<JobRow>(new CommandDefinition(
            """
            SELECT report_job_id AS "ReportJobId", status AS "Status", stage AS "Stage",
                   error_code AS "ErrorCode", error_reason AS "ErrorReason", finished_at AS "FinishedAt"
              FROM report_service.report_jobs
             WHERE report_job_id = @ReportJobId
            """,
            new { ReportJobId = reportJobId },
            cancellationToken: cancellationToken));
    }

    private async Task<OutputRow?> OutputAsync(string reportJobId, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource!.OpenConnectionAsync(cancellationToken);
        return await connection.QuerySingleOrDefaultAsync<OutputRow>(new CommandDefinition(
            """
            SELECT output_id AS "OutputId", status AS "Status", error_code AS "ErrorCode"
              FROM report_service.report_outputs
             WHERE report_job_id = @ReportJobId
            """,
            new { ReportJobId = reportJobId },
            cancellationToken: cancellationToken));
    }

    private async Task<UploadRow?> UploadAsync(string uploadId, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource!.OpenConnectionAsync(cancellationToken);
        return await connection.QuerySingleOrDefaultAsync<UploadRow>(new CommandDefinition(
            """
            SELECT upload_id AS "UploadId", status AS "Status", expires_at AS "ExpiresAt",
                   verification_deadline AS "VerificationDeadline"
              FROM report_service.uploads
             WHERE upload_id = @UploadId
            """,
            new { UploadId = uploadId },
            cancellationToken: cancellationToken));
    }

    private async Task<string?> UploadStatusAsync(string uploadId, CancellationToken cancellationToken) =>
        (await UploadAsync(uploadId, cancellationToken))?.Status;

    private async Task<long> ArtifactsAsync(string reportJobId, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource!.OpenConnectionAsync(cancellationToken);
        return await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            """
            SELECT count(*)
              FROM report_service.artifacts
             WHERE report_job_id = @ReportJobId
            """,
            new { ReportJobId = reportJobId },
            cancellationToken: cancellationToken));
    }

    private async Task<IntentRow?> IntentAsync(string reportJobId, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource!.OpenConnectionAsync(cancellationToken);
        return await connection.QuerySingleOrDefaultAsync<IntentRow>(new CommandDefinition(
            """
            SELECT intent_id AS "IntentId", status AS "Status"
              FROM report_service.intents
             WHERE kind = 'start-job' AND subject_id = @ReportJobId
            """,
            new { ReportJobId = reportJobId },
            cancellationToken: cancellationToken));
    }

    // The run the dispatcher recorded when it started the job, which is the first run of the
    // execution: a continuation keeps the workflow identifier and changes the run.
    private async Task<string> FirstRunIdAsync(string workflowId, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource!.OpenConnectionAsync(cancellationToken);
        return await connection.ExecuteScalarAsync<string>(new CommandDefinition(
            """
            SELECT run_id
              FROM report_service.execution_references
             WHERE workflow_id = @WorkflowId
             ORDER BY recorded_at, intent_id
             LIMIT 1
            """,
            new { WorkflowId = workflowId },
            cancellationToken: cancellationToken)) ?? string.Empty;
    }

    /// <summary>
    /// The worker on its real registrations, with the verification's data work held behind a
    /// gate: the upload stays confirming until <see cref="ReleaseVerification"/> is called,
    /// which is a deployment that has not started the data work yet.
    /// </summary>
    private sealed class JobWorker : IAsyncDisposable
    {
        private readonly JobInputWaitTests _owner;
        private readonly WebApplicationFactory<OrchestrationWorker> _factory;
        private readonly DataQueueGate _gate = new();

        public JobWorker(JobInputWaitTests owner, Settings settings, ManualTimeProvider? clock)
        {
            _owner = owner;
            _factory = new WebApplicationFactory<OrchestrationWorker>().WithWebHostBuilder(builder =>
            {
                builder.UseSetting("Temporal:Address", $"127.0.0.1:{owner._hostPort}");
                builder.UseSetting("Temporal:Namespace", "default");
                builder.UseSetting("Storage:Provider", "filesystem");
                builder.UseSetting("Storage:Filesystem:Root", owner.StorageRoot());
                builder.UseSetting("Storage:Filesystem:CreateIfMissing", "true");
                builder.UseSetting(DatabaseOptions.ConnectionStringSetting, owner._connectionString);
                builder.UseSetting(DatabaseOptions.ExpectedSchemaVersionSetting, MigrationCatalog.ExpectedVersion);
                foreach (var (setting, value) in settings.Values)
                {
                    builder.UseSetting(setting, value);
                }

                builder.ConfigureTestServices(services => services
                    .RemoveAll<EngineWorkerFactory>()
                    .AddSingleton<EngineWorkerFactory>((connection, plan) =>
                        plan.Queue == UploadVerificationActivityOptions.DataQueue
                            ? new GatedWorker(new TemporalEngineWorker(connection, plan), _gate)
                            : new TemporalEngineWorker(connection, plan)));

                if (clock is not null)
                {
                    builder.ConfigureTestServices(services => services
                        .RemoveAll<TimeProvider>()
                        .AddSingleton<TimeProvider>(clock));
                }
            });
        }

        public HttpClient CreateClient() => _factory.CreateClient();

        public void ReleaseVerification() => _gate.Release();

        public ValueTask DisposeAsync() => _factory.DisposeAsync();
    }

    /// <summary>The gate the verification's data work waits on until the case opens it.</summary>
    private sealed class DataQueueGate
    {
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release() => _released.TrySetResult();

        public Task Released() => _released.Task;
    }

    private sealed class GatedWorker(IRunningEngineWorker inner, DataQueueGate gate) : IRunningEngineWorker
    {
        public async Task RunAsync(CancellationToken cancellationToken)
        {
            await gate.Released().WaitAsync(cancellationToken).ConfigureAwait(false);
            await inner.RunAsync(cancellationToken).ConfigureAwait(false);
        }

        public void UseConnection(IEngineConnection connection) => inner.UseConnection(connection);
    }

    /// <summary>The settings a case runs the worker under, at the process's own defaults.</summary>
    private sealed class Settings
    {
        public TimeSpan SessionLifetime { get; private set; } = TimeSpan.FromHours(1);

        public string SessionLifetimeText => SessionLifetime.ToString("c", CultureInfo.InvariantCulture);

        public Dictionary<string, string> Values { get; } = new(StringComparer.Ordinal);

        public Settings WithBackstop(TimeSpan backstop) =>
            Set($"{InputWaitOptions.SectionPath}:{InputWaitOptions.BackstopSetting}", backstop.ToString("c", CultureInfo.InvariantCulture));

        public Settings WithContinueAsNewAfter(int checks) =>
            Set($"{InputWaitOptions.SectionPath}:{InputWaitOptions.ContinueAsNewAfterSetting}", checks.ToString(CultureInfo.InvariantCulture));

        public Settings WithDeadline(TimeSpan deadline) =>
            Set($"{VerificationOptions.SectionPath}:{VerificationOptions.VerificationDeadlineSetting}", deadline.ToString("c", CultureInfo.InvariantCulture));

        public Settings WithSweepInterval(TimeSpan interval) =>
            Set($"{UploadExpiryOptions.SectionPath}:{UploadExpiryOptions.SweepIntervalSetting}", interval.ToString("c", CultureInfo.InvariantCulture));

        public Settings WithStartToClose(TimeSpan startToClose) =>
            Set($"{UploadVerificationOptions.SectionPath}:{UploadVerificationOptions.StartToCloseSetting}", startToClose.ToString("c", CultureInfo.InvariantCulture));

        public Settings WithSessionLifetime(TimeSpan lifetime)
        {
            SessionLifetime = lifetime;
            return this;
        }

        private Settings Set(string setting, string value)
        {
            Values[setting] = value;
            return this;
        }
    }

    /// <summary>One job row, as the table holds it.</summary>
    private sealed class JobRow
    {
        public string ReportJobId { get; set; } = string.Empty;

        public string Status { get; set; } = string.Empty;

        public string? Stage { get; set; }

        public string? ErrorCode { get; set; }

        public string? ErrorReason { get; set; }

        public DateTime? FinishedAt { get; set; }
    }

    /// <summary>One output row.</summary>
    private sealed class OutputRow
    {
        public string OutputId { get; set; } = string.Empty;

        public string Status { get; set; } = string.Empty;

        public string? ErrorCode { get; set; }
    }

    /// <summary>One upload row, as the table holds it.</summary>
    private sealed class UploadRow
    {
        public string UploadId { get; set; } = string.Empty;

        public string Status { get; set; } = string.Empty;

        public DateTime ExpiresAt { get; set; }

        public DateTime? VerificationDeadline { get; set; }
    }

    /// <summary>One start request.</summary>
    private sealed class IntentRow
    {
        public long IntentId { get; set; }

        public string Status { get; set; } = string.Empty;
    }
}
