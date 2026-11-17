extern alias orchestration;

using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
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
using Temporalio.Api.History.V1;
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
using JobTransitionActivities = orchestration::ReportService.Workers.Orchestration.Jobs.JobTransitionActivities;
using TemplateResolutionActivities = orchestration::ReportService.Workers.Orchestration.Jobs.TemplateResolutionActivities;

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
        await WaitForInputWaitToEndAsync(reportJobId, cancellationToken);
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
        var ended = Assert.Single(
            history.Events,
            entry => entry.EventType == EventType.ActivityTaskScheduled
                && entry.ActivityTaskScheduledEventAttributes.ActivityType.Name == InputWaitActivities.EndName).EventId;
        Assert.DoesNotContain(
            history.Events,
            entry => entry.EventType == EventType.ActivityTaskScheduled
                && entry.EventId < ended
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
        await WaitForInputWaitToEndAsync(reportJobId, cancellationToken);

        var history = await HistoryAsync(workflowId, cancellationToken);
        var fired = history.Events.Where(entry => entry.EventType == EventType.TimerFired).Select(entry => entry.EventId).ToList();
        var reads = history.Events
            .Where(entry => entry.EventType == EventType.ActivityTaskScheduled
                && entry.ActivityTaskScheduledEventAttributes.ActivityType.Name == ReportJobActivities.ResolveInputName)
            .Select(entry => entry.EventId)
            .ToList();
        Assert.NotEmpty(fired);
        Assert.Contains(fired, firedId => reads.Any(readId => readId > firedId));

        // The lost nudge left the job where it was: the wait ended on its own timer, and
        // nothing that writes the job's status, takes it past the wait or prepares its
        // template was scheduled before the wait's own end step.
        var ended = Assert.Single(
            history.Events,
            entry => entry.EventType == EventType.ActivityTaskScheduled
                && entry.ActivityTaskScheduledEventAttributes.ActivityType.Name == InputWaitActivities.EndName).EventId;
        Assert.Contains(fired, firedId => ended > firedId);
        Assert.DoesNotContain(
            history.Events,
            entry => entry.EventType == EventType.WorkflowExecutionSignaled && entry.EventId < ended);
        Assert.DoesNotContain(
            history.Events,
            entry => entry.EventType == EventType.ActivityTaskScheduled
                && entry.EventId < ended
                && entry.ActivityTaskScheduledEventAttributes.ActivityType.Name is JobTransitionActivities.TransitionJobName
                    or JobTransitionActivities.FinalizeJobName
                    or TemplateResolutionActivities.PrepareTemplateName
                    or TemplateResolutionActivities.PinTemplateName
                    or TemplateResolutionActivities.PinBaselineName
                    or ReportJobActivities.FailJobName);
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
        await WaitForInputWaitToEndAsync(reportJobId, cancellationToken);
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
        await WaitForInputWaitToEndAsync(reportJobId, cancellationToken);
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
        await WaitForInputWaitToEndAsync(first, cancellationToken);
        await WaitForInputWaitToEndAsync(second, cancellationToken);

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

        // The confirmation wrote the verification request; withdrawing it before any worker
        // exists holds the upload where the subject needs it — verifying, with nothing able to
        // claim the request — and the nudge is sent only when the case asks for it.
        await WithdrawVerificationRequestAsync(uploadId, cancellationToken);

        var settings = new Settings()
            .WithBackstop(TimeSpan.FromSeconds(1))
            .WithContinueAsNewAfter(2);

        // The job's run is executed by the product's own orchestration worker in a process of
        // its own: the wait's bounds are process-wide, so only a process of its own can hold a
        // bound no other host of this test process publishes over.
        await using var worker = await OrchestrationWorkerProcess.StartAsync(
            _hostPort,
            _connectionString!,
            StorageRoot(),
            settings,
            ReadyDeadline,
            WorkerLogPath(),
            cancellationToken);
        await WaitForStatusAsync(worker.Client, HttpStatusCode.OK, cancellationToken);

        var reportJobId = await AcceptJobAsync(uploadId, cancellationToken);
        var workflowId = ReportJobWorkflow.WorkflowIdFor(reportJobId);
        await WaitForStageAsync(reportJobId, InputWaitStage.WaitingForInput, cancellationToken);

        var client = await EngineClientAsync(cancellationToken);
        try
        {
            // The dispatcher records the execution reference after it starts the run, so the
            // baseline is waited for, bounded, and an unreadable reference fails the case: a
            // changed run id alone is never evidence of a continuation.
            var firstRunId = await FirstRunIdAsync(workflowId, cancellationToken);
            Assert.NotNull(firstRunId);

            // The write a neighbour host performs when it starts, forced inside the first run's
            // window: the bound the process publishes is the delivered default, and the run in
            // the child must still take the case's own two checks.
            PublishTheDeliveredDefaults("the first run's window");

            var currentRunId = await WaitForAContinuationAsync(client, workflowId, firstRunId, cancellationToken);

            // The same write again, inside the continued run's wait — and then the release,
            // before the histories are read, so the verification's nudge arrives inside the
            // window the continued run's own bound leaves open.
            PublishTheDeliveredDefaults("the continued run's wait");
            await RequestVerificationAsync(uploadId, cancellationToken);

            // The continuation is the engine's record and nothing else: the first run closed by
            // one continuation naming the current run, with the case's two checks before it.
            var firstRun = await HistoryAsync(workflowId, firstRunId, cancellationToken);
            var continuation = AssertTheContinuedAsNewEvent(firstRun, currentRunId);
            AssertTheTwoChecksBeforeTheContinuation(firstRun, continuation.EventId);
            AssertInsideTheHistoryBound(firstRun);

            // The second run began from the first one, under the same workflow identifier.
            var secondRun = await HistoryAsync(workflowId, currentRunId, cancellationToken);
            AssertTheContinuationStarted(secondRun, firstRunId);
            AssertInsideTheHistoryBound(secondRun);

            Assert.Equal("QUEUED", (await JobAsync(reportJobId, cancellationToken))!.Status);
            Assert.Equal(InputWaitStage.WaitingForInput, (await JobAsync(reportJobId, cancellationToken))!.Stage);

            await WriteTheContinuationEvidenceAsync(workflowId, firstRunId, currentRunId, firstRun, secondRun, continuation, cancellationToken);

            // The nudge across the continuation is not lost: the continued run leaves the wait,
            // and the read it made is the one the workflow task that consumed the signal scheduled.
            await WaitForInputWaitToEndAsync(reportJobId, cancellationToken);
            var woken = await HistoryAsync(workflowId, currentRunId, cancellationToken);
            var wakeUp = AssertTheWakeUpAcrossTheContinuation(woken);
            await WriteTheWakeUpEvidenceAsync(woken, wakeUp, cancellationToken);
        }
        finally
        {
            // The child is about to be gone, so no later case of the class may inherit a run
            // whose worker no longer exists. The termination is cleanup and no evidence.
            await TerminateTheRunAsync(client, workflowId);
        }
    }

    // The exact publication a neighbour orchestration worker host performs when it starts,
    // with the delivered defaults. The assertion proves the write took effect in this process,
    // so the child's bound is the only one its run can use.
    private static void PublishTheDeliveredDefaults(string window)
    {
        ReportJobWorkflow.Configure(InputWaitOptions.DefaultBackstop, InputWaitOptions.DefaultContinueAsNewAfter);
        var published = ReportJobWorkflow.CurrentSettings();
        Assert.True(
            published == (InputWaitOptions.DefaultBackstop, InputWaitOptions.DefaultContinueAsNewAfter),
            $"the publication a neighbour worker performs did not take effect in {window}: "
            + $"the process reads {published.Backstop} / {published.ContinueAsNewAfter.ToString(CultureInfo.InvariantCulture)}");
    }

    private async Task<string> WaitForAContinuationAsync(
        TemporalClient client,
        string workflowId,
        string firstRunId,
        CancellationToken cancellationToken)
    {
        var currentRunId = firstRunId;
        await Wait.UntilAsync(
            $"the run of {workflowId} to continue as a new one",
            async token =>
            {
                var current = await client.GetWorkflowHandle(workflowId).DescribeAsync().WaitAsync(token);
                currentRunId = current.RunId;
                return !string.Equals(current.RunId, firstRunId, StringComparison.Ordinal);
            },
            WaitDeadline,
            TimeSpan.FromMilliseconds(250),
            cancellationToken);
        return currentRunId;
    }

    // The first run closed by exactly one continuation, naming the run the engine now reports,
    // and by nothing else.
    private static HistoryEvent AssertTheContinuedAsNewEvent(WorkflowHistory firstRun, string currentRunId)
    {
        var continuation = Assert.Single(firstRun.Events, entry => entry.EventType == EventType.WorkflowExecutionContinuedAsNew);
        Assert.Equal(currentRunId, continuation.WorkflowExecutionContinuedAsNewEventAttributes.NewExecutionRunId);
        Assert.DoesNotContain(firstRun.Events, entry => entry.EventType is EventType.WorkflowExecutionCompleted
            or EventType.WorkflowExecutionFailed
            or EventType.WorkflowExecutionCanceled
            or EventType.WorkflowExecutionTerminated);
        return continuation;
    }

    // The run used the case's own bound: exactly two loop checks sit between the step that
    // records the wait and the continuation. The run's initial resolution precedes the step.
    private static void AssertTheTwoChecksBeforeTheContinuation(WorkflowHistory firstRun, long continuationEventId)
    {
        var begun = Assert.Single(firstRun.Events, entry => entry.EventType == EventType.ActivityTaskScheduled
            && entry.ActivityTaskScheduledEventAttributes.ActivityType.Name == InputWaitActivities.BeginName);
        var checks = firstRun.Events.Count(entry => entry.EventType == EventType.ActivityTaskScheduled
            && entry.ActivityTaskScheduledEventAttributes.ActivityType.Name == ReportJobActivities.ResolveInputName
            && entry.EventId > begun.EventId
            && entry.EventId < continuationEventId);
        Assert.Equal(2, checks);
    }

    // The second run is a continuation of the first: the engine names the run it continued from,
    // under the same workflow identifier.
    private static void AssertTheContinuationStarted(WorkflowHistory secondRun, string firstRunId)
    {
        var started = Assert.Single(secondRun.Events, entry => entry.EventType == EventType.WorkflowExecutionStarted);
        Assert.Equal(EventType.WorkflowExecutionStarted, secondRun.Events.First().EventType);
        Assert.Equal(firstRunId, started.WorkflowExecutionStartedEventAttributes.ContinuedExecutionRunId);
    }

    private static void AssertInsideTheHistoryBound(WorkflowHistory run) =>
        Assert.True(
            run.Events.Count <= (2 * InputWaitHistoryBudget.WorstCaseEventsPerPass) + InputWaitHistoryBudget.PrefixEvents,
            $"the run carries {run.Events.Count} events, past the relationship its configured checks promise");

    // The nudge reached the continued run and its own workflow task made the check that ended
    // the wait: the read is scheduled by the task that consumed the signal, and the end step
    // follows it and completes. A timer of the run's own backstop may land in that same task;
    // it is recorded, never failed on.
    private static (long SignalEventId, IReadOnlyList<long> TimersBeforeTheRead) AssertTheWakeUpAcrossTheContinuation(
        WorkflowHistory woken)
    {
        var signal = Assert.Single(woken.Events, entry => entry.EventType == EventType.WorkflowExecutionSignaled
            && string.Equals(
                entry.WorkflowExecutionSignaledEventAttributes.SignalName,
                ReportJobWorkflow.InputChangedSignal,
                StringComparison.Ordinal));

        var read = woken.Events.FirstOrDefault(entry => entry.EventId > signal.EventId
            && entry.EventType == EventType.ActivityTaskScheduled
            && entry.ActivityTaskScheduledEventAttributes.ActivityType.Name == ReportJobActivities.ResolveInputName);
        Assert.NotNull(read);

        var consumer = woken.Events.First(entry => entry.EventId > signal.EventId && entry.EventType == EventType.WorkflowTaskCompleted);
        var producer = woken.Events.Last(entry => entry.EventId < read!.EventId && entry.EventType == EventType.WorkflowTaskCompleted);
        Assert.Equal(consumer.EventId, producer.EventId);

        var timers = woken.Events
            .Where(entry => entry.EventId > signal.EventId
                && entry.EventId < read!.EventId
                && entry.EventType == EventType.TimerFired)
            .Select(entry => entry.EventId)
            .ToArray();

        var ended = woken.Events.First(entry => entry.EventId > read!.EventId
            && entry.EventType == EventType.ActivityTaskScheduled
            && entry.ActivityTaskScheduledEventAttributes.ActivityType.Name == InputWaitActivities.EndName);
        Assert.Contains(woken.Events, entry => entry.EventType == EventType.ActivityTaskCompleted
            && entry.ActivityTaskCompletedEventAttributes.ScheduledEventId == ended.EventId);

        return (signal.EventId, timers);
    }

    private async Task WriteTheContinuationEvidenceAsync(
        string workflowId,
        string firstRunId,
        string currentRunId,
        WorkflowHistory firstRun,
        WorkflowHistory secondRun,
        HistoryEvent continuation,
        CancellationToken cancellationToken)
    {
        await File.WriteAllTextAsync(EvidencePath("continue-as-new-first-run.json"), firstRun.ToJson(), cancellationToken);
        await File.WriteAllTextAsync(EvidencePath("continue-as-new-second-run.json"), secondRun.ToJson(), cancellationToken);
        var summary = string.Join(
            Environment.NewLine,
            $"workflow-id: {workflowId}",
            $"first-run-id: {firstRunId}",
            $"second-run-id: {currentRunId}",
            $"continuation-event: {continuation.EventId} new-execution-run-id: {continuation.WorkflowExecutionContinuedAsNewEventAttributes.NewExecutionRunId}",
            $"first-run-events: {firstRun.Events.Count}",
            $"second-run-events-at-the-continuation: {secondRun.Events.Count}");
        await File.WriteAllTextAsync(EvidencePath("continue-as-new-continuation.txt"), summary + Environment.NewLine, cancellationToken);
        TestContext.Current.TestOutputHelper?.WriteLine(summary);
    }

    private async Task WriteTheWakeUpEvidenceAsync(
        WorkflowHistory woken,
        (long SignalEventId, IReadOnlyList<long> TimersBeforeTheRead) wakeUp,
        CancellationToken cancellationToken)
    {
        await File.WriteAllTextAsync(EvidencePath("continue-as-new-woken-run.json"), woken.ToJson(), cancellationToken);
        var timers = wakeUp.TimersBeforeTheRead.Count == 0
            ? "(none)"
            : string.Join(", ", wakeUp.TimersBeforeTheRead.Select(entry => entry.ToString(CultureInfo.InvariantCulture)));
        var summary = string.Join(
            Environment.NewLine,
            $"signal-event: {wakeUp.SignalEventId} signal: {ReportJobWorkflow.InputChangedSignal}",
            $"timer-fired-before-the-read: {timers}",
            $"woken-run-events: {woken.Events.Count}");
        await File.WriteAllTextAsync(EvidencePath("continue-as-new-wake-up.txt"), summary + Environment.NewLine, cancellationToken);
        TestContext.Current.TestOutputHelper?.WriteLine(summary);
    }

    // The run's own evidence goes into the run's folder when the runner gave the process one,
    // and into the class's temporary directory when a case is run by hand.
    private string EvidencePath(string fileName)
    {
        var folder = _directory;
        if (RunEnvironment.RunDirectory is { } run)
        {
            folder = Path.Combine(run, RunEnvironment.EvidenceFolderName);
            Directory.CreateDirectory(folder);
        }

        return Path.Combine(folder, fileName);
    }

    private string WorkerLogPath() => Path.Combine(_directory, "continue-as-new-worker.log");

    // Cleanup, never evidence: the run's worker process is about to be gone, and a later case of
    // the class polls the class's own engine.
    private static async Task TerminateTheRunAsync(TemporalClient client, string workflowId)
    {
        try
        {
            await client.GetWorkflowHandle<ReportJobWorkflow>(workflowId)
                .TerminateAsync("the case ended and no worker of its own remains");
        }
        catch (Temporalio.Exceptions.RpcException exception) when ((int)exception.Code == 5)
        {
            // The run has already ended: nothing is left to clean up.
        }
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

    // The release, through the product's own record: the row the confirmation writes, inserted
    // again once the continuation has been observed. The worker's dispatcher claims it, the
    // verification runs, and its own signaller nudges the job.
    private async Task RequestVerificationAsync(string uploadId, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource!.OpenConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO report_service.intents (kind, subject_id, status, attempts, created_at, next_attempt_at)
            VALUES ('start-upload-verification', @UploadId, 'PENDING', 0, @CompletedAt, @CompletedAt)
            ON CONFLICT (kind, subject_id) DO NOTHING
            """,
            new { UploadId = uploadId, CompletedAt = DateTimeOffset.UtcNow },
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

    private Task WaitForStageAsync(string reportJobId, string stage, CancellationToken cancellationToken) =>
        Wait.UntilAsync(
            $"the stage of {reportJobId} to read {stage}",
            async token => string.Equals((await JobAsync(reportJobId, token))?.Stage, stage, StringComparison.Ordinal),
            WaitDeadline,
            TimeSpan.FromMilliseconds(250),
            cancellationToken);

    // The wait of a job ends when its own end step completes. The history keeps that step once
    // it has run and never removes it, so the probe cannot be sampled before it and cannot miss
    // it afterwards; the completed step is paired with the scheduled one it names.
    private Task WaitForInputWaitToEndAsync(string reportJobId, CancellationToken cancellationToken) =>
        Wait.UntilAsync(
            $"the input wait of {reportJobId} to end",
            async token =>
            {
                var history = await HistoryAsync(ReportJobWorkflow.WorkflowIdFor(reportJobId), token);
                var ended = history.Events
                    .Where(entry => entry.EventType == EventType.ActivityTaskScheduled
                        && entry.ActivityTaskScheduledEventAttributes.ActivityType.Name == InputWaitActivities.EndName)
                    .Select(entry => entry.EventId)
                    .ToHashSet();
                return history.Events.Any(entry => entry.EventType == EventType.ActivityTaskCompleted
                    && ended.Contains(entry.ActivityTaskCompletedEventAttributes.ScheduledEventId));
            },
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
    // execution: a continuation keeps the workflow identifier and changes the run. The
    // reference is written after the start, so the case waits for it, bounded, and answers
    // null when it never appears — an unreadable reference is a failure, never a run.
    private async Task<string?> FirstRunIdAsync(string workflowId, CancellationToken cancellationToken)
    {
        string? runId = null;
        await Wait.UntilAsync(
            $"the execution reference of {workflowId} in report_service.execution_references",
            async token => (runId = await ReadFirstRunIdAsync(workflowId, token)) is not null,
            DispatchDeadline,
            TimeSpan.FromMilliseconds(250),
            cancellationToken);
        return runId;
    }

    private async Task<string?> ReadFirstRunIdAsync(string workflowId, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource!.OpenConnectionAsync(cancellationToken);
        return await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
            """
            SELECT run_id
              FROM report_service.execution_references
             WHERE workflow_id = @WorkflowId
             ORDER BY recorded_at, intent_id
             LIMIT 1
            """,
            new { WorkflowId = workflowId },
            cancellationToken: cancellationToken));
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
                builder.UseSetting("Retention:Outputs", "01:00:00");
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

    /// <summary>
    /// The product's own orchestration worker as a process of its own, started with the case's
    /// settings: the wait's bounds are process-wide, so the job's run is executed where no other
    /// host of this test process can publish over them. The child loads the worker project's own
    /// settings (the test output carries the API's), is killed tree-wide on disposal, and writes
    /// its log into the class's directory and, when the runner gave the process one, into the
    /// run's evidence folder.
    /// </summary>
    private sealed class OrchestrationWorkerProcess : IAsyncDisposable
    {
        private const string AssemblyName = "ReportService.Workers.Orchestration.dll";

        private readonly Process _process;
        private readonly HttpClient _client;
        private readonly string _logPath;
        private readonly LogSink _log;

        private OrchestrationWorkerProcess(Process process, HttpClient client, string logPath, LogSink log)
        {
            _process = process;
            _client = client;
            _logPath = logPath;
            _log = log;
        }

        public HttpClient Client => _client;

        public static async Task<OrchestrationWorkerProcess> StartAsync(
            int enginePort,
            string connectionString,
            string storageRoot,
            Settings settings,
            TimeSpan readyDeadline,
            string logPath,
            CancellationToken cancellationToken)
        {
            var port = FreeLoopbackPort();
            var start = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = AppContext.BaseDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, AssemblyName));
            start.Environment["ASPNETCORE_URLS"] = $"http://127.0.0.1:{port.ToString(CultureInfo.InvariantCulture)}";

            // The worker's own settings file is at the project, not beside the test's assembly:
            // the output's appsettings.json belongs to the API and carries no dispatcher.
            start.Environment["ASPNETCORE_CONTENTROOT"] = Path.Combine(RepositoryPaths.Root, "src", "ReportService.Workers.Orchestration");
            start.Environment["Temporal__Address"] = $"127.0.0.1:{enginePort.ToString(CultureInfo.InvariantCulture)}";
            start.Environment["Temporal__Namespace"] = "default";
            start.Environment["Storage__Provider"] = "filesystem";
            start.Environment["Storage__Filesystem__Root"] = storageRoot;
            start.Environment["Storage__Filesystem__CreateIfMissing"] = "true";
            start.Environment["Retention__Outputs"] = "01:00:00";
            start.Environment[EnvironmentKey(DatabaseOptions.ConnectionStringSetting)] = connectionString;
            start.Environment[EnvironmentKey(DatabaseOptions.ExpectedSchemaVersionSetting)] = MigrationCatalog.ExpectedVersion;
            foreach (var (setting, value) in settings.Values)
            {
                start.Environment[EnvironmentKey(setting)] = value;
            }

            // The release of the verification is claimed at once instead of up to a second later.
            start.Environment["Dispatcher__PollInterval"] = "00:00:00.100";
            start.Environment.Remove("OTEL_EXPORTER_OTLP_ENDPOINT");
            start.Environment.Remove("OTEL_EXPORTER_OTLP_PROTOCOL");
            start.Environment.Remove("Telemetry__OtlpEndpoint");

            var log = new LogSink(logPath);
            var process = new Process { StartInfo = start };
            process.OutputDataReceived += (_, entry) => log.Write(entry.Data);
            process.ErrorDataReceived += (_, entry) => log.Write(entry.Data);

            HttpClient? client = null;
            try
            {
                if (!process.Start())
                {
                    throw new InvalidOperationException("the orchestration worker process did not start");
                }

                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                client = new HttpClient
                {
                    BaseAddress = new Uri($"http://127.0.0.1:{port.ToString(CultureInfo.InvariantCulture)}", UriKind.Absolute),
                    Timeout = TimeSpan.FromSeconds(30),
                };
                var probe = client;
                await Wait.UntilAsync(
                    "the orchestration worker process answering its health check",
                    async token =>
                    {
                        using var response = await probe.GetAsync(new Uri("/health", UriKind.Relative), token);
                        return response.StatusCode == HttpStatusCode.OK;
                    },
                    readyDeadline,
                    TimeSpan.FromMilliseconds(500),
                    cancellationToken);
                return new OrchestrationWorkerProcess(process, client, logPath, log);
            }
            catch (Exception exception)
            {
                Kill(process);
                client?.Dispose();
                log.Dispose();
                if (exception is OperationCanceledException)
                {
                    throw;
                }

                throw new InvalidOperationException(
                    $"the orchestration worker process did not answer its health check: {exception.Message}"
                    + $"{Environment.NewLine}{LogTail(logPath)}",
                    exception);
            }
        }

        public async ValueTask DisposeAsync()
        {
            Kill(_process);
            try
            {
                await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            }
            catch (Exception exception) when (exception is TimeoutException or InvalidOperationException)
            {
                // The killed process did not report its exit; the class is ending either way.
            }

            _process.Dispose();
            _client.Dispose();
            _log.Dispose();

            if (RunEnvironment.RunDirectory is { } run)
            {
                var evidence = Path.Combine(run, RunEnvironment.EvidenceFolderName);
                Directory.CreateDirectory(evidence);
                try
                {
                    File.Copy(_logPath, Path.Combine(evidence, "continue-as-new-worker.log"), overwrite: true);
                }
                catch (IOException)
                {
                    // A log that cannot be copied costs the record, never the case.
                }
            }
        }

        private static string EnvironmentKey(string setting) => setting.Replace(":", "__", StringComparison.Ordinal);

        private static int FreeLoopbackPort()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }

        private static void Kill(Process process)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(30_000);
                }
            }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
            {
                // The process was already gone: nothing is left to stop.
            }
        }

        private static string LogTail(string path)
        {
            try
            {
                return File.Exists(path)
                    ? string.Join(Environment.NewLine, File.ReadAllLines(path).TakeLast(20))
                    : "(the child wrote no log)";
            }
            catch (IOException)
            {
                return "(the child's log could not be read)";
            }
        }

        /// <summary>The file the child's own output is appended to, written from its readers.</summary>
        private sealed class LogSink : IDisposable
        {
            private readonly StreamWriter _writer;
            private readonly object _gate = new();
            private bool _closed;

            public LogSink(string path)
            {
                _writer = new StreamWriter(
                    new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read),
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
                {
                    AutoFlush = true,
                };
            }

            public void Write(string? line)
            {
                if (line is null)
                {
                    return;
                }

                lock (_gate)
                {
                    if (_closed)
                    {
                        return;
                    }

                    _writer.WriteLine(line);
                }
            }

            public void Dispose()
            {
                lock (_gate)
                {
                    if (_closed)
                    {
                        return;
                    }

                    _closed = true;
                    _writer.Dispose();
                }
            }
        }
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
