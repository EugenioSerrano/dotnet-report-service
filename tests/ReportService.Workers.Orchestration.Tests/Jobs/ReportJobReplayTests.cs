using ReportService.Testing;
using ReportService.Workers.Orchestration.Jobs;
using Temporalio.Api.Enums.V1;
using Temporalio.Common;
using Temporalio.Worker;
using Temporalio.Workflows;

namespace ReportService.Workers.Orchestration.Tests.Jobs;

/// <summary>
/// The bounds a job's wait runs under are published to the workflow type once per process,
/// so a case that publishes them and a case that plans the production workers cannot run at
/// the same time: one would see the other's settings.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class JobWorkflowSettings
{
    /// <summary>The name every case that publishes those settings shares.</summary>
    public const string Name = "the job workflow's published settings";
}

/// <summary>
/// Replays the job histories recorded against a real engine, so a change that would make a
/// running execution non-deterministic fails here instead of in production. A history file
/// is a fixture: it is generated once, against an engine, by the opt-in capture case of the
/// integration suite, and committed with the change that captured it. Each recording is
/// replayed under the two settings it was taken with, because those settings decide which
/// commands the recorded run wrote. The fixtures are part of what this work delivers, so a
/// missing one fails the case: nothing was replayed, and a case that had nothing to check
/// is not a result.
/// </summary>
[Collection(JobWorkflowSettings.Name)]
public sealed class ReportJobReplayTests
{
    /// <summary>The directory the captured histories are committed in.</summary>
    public static readonly string HistoriesDirectory = Path.Combine("Histories", "Jobs");

    /// <summary>Every committed recording, by the name the suite reads it by.</summary>
    public static TheoryData<string> Recordings { get; } =
        new(JobHistoryFixtures.All.Select(fixture => fixture.File));

    [Theory]
    [MemberData(nameof(Recordings))]
    public async Task EveryRecordedHistoryReplaysUnderTheSettingsItWasTakenWith(string file)
    {
        var fixture = JobHistoryFixtures.ByFile(file);
        var path = JobHistoryFiles.PathOf(file);
        Assert.True(
            File.Exists(path),
            $"the recorded job history {file} is missing from {Path.GetDirectoryName(path)}: the fixture is the outcome of a run against a real engine and it is part of this work, so there is nothing to replay.");

        // The two settings are the run's own: a recording of a wait that continues as a new
        // run replays only when the workflow is told the same check limit it ran under.
        ReportJobWorkflow.Configure(fixture.Backstop, fixture.ContinueAsNewAfter);

        var options = new WorkflowReplayerOptions();
        options.Workflows.Add(WorkflowDefinition.Create(typeof(ReportJobWorkflow)));
        var replayer = new WorkflowReplayer(options);

        // The identifier passed beside the history only labels the replay result: what the
        // case proves is that the recorded execution replays without a divergence.
        await replayer.ReplayWorkflowAsync(
            WorkflowHistory.FromJson(
                Path.GetFileNameWithoutExtension(file),
                await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken)),
            cancellationToken: TestContext.Current.CancellationToken);
    }

    [Fact]
    public void TheWorkflowIsRegisteredUnderTheNameTheDispatcherStarts()
    {
        Assert.Equal("ReportJob", ReportJobWorkflow.WorkflowType);
        Assert.Equal("report-orchestration", ReportJobWorkflow.OrchestrationQueue);
        Assert.Equal(
            "report-job/job-0123456789abcdef0123456789abcdef",
            ReportJobWorkflow.WorkflowIdFor("job-0123456789abcdef0123456789abcdef"));
    }

    [Fact]
    public void TheStepsRunOnTheOrchestrationQueueUnderTheUnboundedPolicy()
    {
        var options = ReportJobActivityOptions.Create();

        Assert.Equal(ReportJobWorkflow.OrchestrationQueue, options.TaskQueue);
        Assert.Equal(TimeSpan.FromSeconds(30), options.StartToCloseTimeout);
        Assert.Equal(TimeSpan.FromSeconds(1), options.RetryPolicy!.InitialInterval);
        Assert.Equal(TimeSpan.FromSeconds(30), options.RetryPolicy.MaximumInterval);
        Assert.Equal(0, options.RetryPolicy.MaximumAttempts);
    }
}

/// <summary>
/// What every committed job history shows about the run it records: the checks it stayed
/// inside, the stage it left behind, the timer it waited on or was spared, and — where the
/// run continued as a new one — that it did so before anything after the wait was started.
/// The pass limit is asserted as the relationship to the history budget for the value each
/// recording was taken with, never as a number of its own and never for the default alone.
/// </summary>
[Collection(JobWorkflowSettings.Name)]
public sealed class ReportJobHistoryBoundTests
{
    /// <summary>The events the longest path of a job that did not have to wait writes.</summary>
    public const int LongestPathEvents = 17;

    /// <summary>The events the longest path of a job that runs the patched steps writes.</summary>
    public const int LongestPathEventsOfTheFinalizedPath = 31;

    /// <summary>The events one rescheduled workflow task adds.</summary>
    public const int EventsPerLostWorkflowTask = 2;

    /// <summary>
    /// The longest history a run that carries the template step may write; a recording of the
    /// step whose measured count is above it is a stop, never a bound to widen.
    /// </summary>
    public const int TemplateStepHistoryCeiling = 64;

    /// <summary>The recordings of the template step, each with the count measured when it was captured.</summary>
    public static TheoryData<string> OfTheTemplateStep { get; } =
        new(JobHistoryFixtures.OfTheTemplateStep.Select(fixture => fixture.File));

    /// <summary>The recordings of a job whose first check of its upload already answered.</summary>
    public static TheoryData<string> OfAJobThatDidNotWait { get; } =
        new(JobHistoryFixtures.OfAJobThatDidNotWait.Select(fixture => fixture.File));

    /// <summary>The recordings that carry the wait of a job for its upload.</summary>
    public static TheoryData<string> OfAJobThatWaited { get; } =
        new(JobHistoryFixtures.OfAJobThatWaited.Select(fixture => fixture.File));

    /// <summary>The recordings of a run that left the wait with its upload's outcome.</summary>
    public static TheoryData<string> OfARunThatLeftTheWait { get; } =
        new(JobHistoryFixtures.OfARunThatLeftTheWait.Select(fixture => fixture.File));

    [Theory]
    [MemberData(nameof(OfAJobThatDidNotWait))]
    public async Task AJobThatDidNotWaitCarriesNoTimerAndChecksItsInputOnce(string file)
    {
        var history = await JobHistoryFiles.ReadAsync(file);
        var rescheduled = Rescheduled(history);
        var allowed = LongestPathEvents + (EventsPerLostWorkflowTask * rescheduled);

        Assert.True(
            history.Events.Count <= allowed,
            $"{file} holds {history.Events.Count} events, and the longest path of a job that did not wait allows {allowed} ({LongestPathEvents} plus {EventsPerLostWorkflowTask} per rescheduled workflow task, of which it records {rescheduled}).");
        Assert.DoesNotContain(history.Events, entry => entry.EventType == EventType.TimerStarted);
        Assert.Equal(1, Scheduled(history, ReportJobActivities.ResolveInputName));
        Assert.Equal(0, Scheduled(history, InputWaitActivities.BeginName));
        Assert.Equal(0, Scheduled(history, InputWaitActivities.EndName));

        // The two steps of the patched path carry no marker in these recordings, so the
        // patch answers false and neither step is scheduled.
        Assert.Equal(0, Scheduled(history, JobTransitionActivities.TransitionJobName));
        Assert.Equal(0, Scheduled(history, JobTransitionActivities.FinalizeJobName));
    }

    [Theory]
    [InlineData(JobHistoryFixtures.ReadyFile, 0)]
    [InlineData(JobHistoryFixtures.RejectedFile, 1)]
    public async Task TheFailureOfAFirstCheckIsWrittenOnlyWhenTheUploadEndedTheJob(string file, int failures)
    {
        var history = await JobHistoryFiles.ReadAsync(file);

        Assert.Equal(failures, Scheduled(history, ReportJobActivities.FailJobName));
    }

    [Theory]
    [MemberData(nameof(OfAJobThatWaited))]
    public async Task ARunThatWaitedStayedInsideTheLimitItWasTakenWith(string file)
    {
        var fixture = JobHistoryFixtures.ByFile(file);

        // The relationship, not the number: the ceiling is whatever the two counted values
        // make it, and the limit this recording was taken with stays inside it.
        Assert.True(
            InputWaitHistoryBudget.KeepsTheBound(fixture.ContinueAsNewAfter),
            $"{file} was taken with {fixture.ContinueAsNewAfter} checks, and one run writes {InputWaitHistoryBudget.WorstCaseEventsPerPass} events per check over its own {InputWaitHistoryBudget.PrefixEvents}, inside a history of {InputWaitHistoryBudget.HistoryLimit}.");

        var history = await JobHistoryFiles.ReadAsync(file);
        var checks = Scheduled(history, ReportJobActivities.ResolveInputName);
        var passes = checks - 1;

        Assert.True(
            checks >= 2,
            $"{file} holds {checks} input checks: a recording of a wait checks the input once before it waits and at least once more.");
        Assert.True(
            passes <= fixture.ContinueAsNewAfter,
            $"{file} took {passes} checks in one run, and that run was allowed {fixture.ContinueAsNewAfter} before it had to continue as a new one.");
        Assert.Equal(1, Scheduled(history, InputWaitActivities.BeginName));

        var rescheduled = Rescheduled(history);
        Assert.True(
            history.Events.Count <= InputWaitHistoryBudget.DefaultPassesCeiling + (EventsPerLostWorkflowTask * rescheduled),
            $"{file} holds {history.Events.Count} events, and a run keeps at most {InputWaitHistoryBudget.DefaultPassesCeiling} at the default settings ({rescheduled} rescheduled workflow tasks recorded).");
    }

    [Theory]
    [MemberData(nameof(OfARunThatLeftTheWait))]
    public async Task ARunThatLeftTheWaitClearedTheStageOnceBeforeItEnded(string file)
    {
        var history = await JobHistoryFiles.ReadAsync(file);

        Assert.Equal(1, Scheduled(history, InputWaitActivities.EndName));
        var cleared = EventId(history, InputWaitActivities.EndName);
        var failed = EventId(history, ReportJobActivities.FailJobName);
        Assert.True(
            failed is null || cleared < failed,
            $"{file} clears the stage at event {cleared} and fails the job at event {failed}: the wait ends before the outcome step, whatever the outcome.");
    }

    [Theory]
    [InlineData(JobHistoryFixtures.WaitingThenRejectedFile, "DATA_INVALID", "input-rejected")]
    [InlineData(JobHistoryFixtures.WaitingThenExpiredFile, "INPUT_NOT_FOUND", "input-expired")]
    public async Task ARunThatEndedWithItsUploadCarriesItsCodeAndTheReasonContract(
        string file,
        string errorCode,
        string reason)
    {
        var history = await JobHistoryFiles.ReadAsync(file);

        var failure = Assert.Single(
            history.Events,
            entry => entry.EventType == EventType.ActivityTaskScheduled
                     && string.Equals(
                         entry.ActivityTaskScheduledEventAttributes.ActivityType.Name,
                         ReportJobActivities.FailJobName,
                         StringComparison.Ordinal));
        var input = failure.ActivityTaskScheduledEventAttributes.Input.Payloads_[0].Data.ToStringUtf8();

        Assert.Contains(errorCode, input, StringComparison.Ordinal);
        Assert.Contains(reason, input, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheRunThatContinuedEndedWithTheContinuationAndFailedNothing()
    {
        var fixture = JobHistoryFixtures.ContinuedAsNew;
        var history = await JobHistoryFiles.ReadAsync(fixture.File);

        Assert.Equal(EventType.WorkflowExecutionContinuedAsNew, history.Events.Last().EventType);
        Assert.Equal(0, Scheduled(history, ReportJobActivities.FailJobName));
        Assert.Equal(0, Scheduled(history, InputWaitActivities.EndName));
        Assert.Equal(fixture.ContinueAsNewAfter, Scheduled(history, ReportJobActivities.ResolveInputName) - 1);
    }

    [Fact]
    public async Task NoRunContinuedAsANewOneAfterAChildExecutionWasStarted()
    {
        var continued = new List<string>();
        foreach (var fixture in JobHistoryFixtures.All)
        {
            var history = await JobHistoryFiles.ReadAsync(fixture.File);
            var continuation = history.Events.FirstOrDefault(
                entry => entry.EventType == EventType.WorkflowExecutionContinuedAsNew);
            if (continuation is null)
            {
                continue;
            }

            continued.Add(fixture.File);

            // The confinement the continuation lives under: it is thrown from inside the wait,
            // before any step after it has started a child execution of its own.
            var beforeTheContinuation = history.Events
                .Where(entry => entry.EventId < continuation.EventId)
                .ToList();
            Assert.DoesNotContain(beforeTheContinuation, entry => entry.EventType == EventType.StartChildWorkflowExecutionInitiated);
            Assert.DoesNotContain(beforeTheContinuation, entry => entry.EventType == EventType.ChildWorkflowExecutionStarted);
        }

        Assert.NotEmpty(continued);
    }

    [Theory]
    [InlineData(JobHistoryFixtures.WaitingThenReadyAfterANotificationFile)]
    [InlineData(JobHistoryFixtures.ContinuedAfterANotificationFile)]
    public async Task ARunWokenByANotificationCarriesNoTimerOfTheCheckItWoke(string file)
    {
        var history = await JobHistoryFiles.ReadAsync(file);

        var signalled = Assert.Single(history.Events, entry => entry.EventType == EventType.WorkflowExecutionSignaled);
        var woken = history.Events.First(entry =>
            entry.EventType == EventType.ActivityTaskScheduled
            && entry.EventId > signalled.EventId
            && string.Equals(
                entry.ActivityTaskScheduledEventAttributes.ActivityType.Name,
                ReportJobActivities.ResolveInputName,
                StringComparison.Ordinal));

        // The pass starts its timer before the notification arrives, so a recording woken by
        // the notification records that timer's start and its cancellation, never a fire: a
        // fired timer would be the backstop answering, not the notification.
        Assert.DoesNotContain(
            history.Events,
            entry => entry.EventType == EventType.TimerFired
                     && entry.EventId > signalled.EventId
                     && entry.EventId < woken.EventId);
    }

    [Fact]
    public async Task TheRunThatWasWokenByItsOwnTimerCarriesTheFireThatWokeIt()
    {
        var history = await JobHistoryFiles.ReadAsync(JobHistoryFixtures.Verifying.File);
        var fired = Assert.Single(history.Events, entry => entry.EventType == EventType.TimerFired);

        var lastCheck = history.Events.Last(entry =>
            entry.EventType == EventType.ActivityTaskScheduled
            && string.Equals(
                entry.ActivityTaskScheduledEventAttributes.ActivityType.Name,
                ReportJobActivities.ResolveInputName,
                StringComparison.Ordinal));

        Assert.True(
            fired.EventId < lastCheck.EventId,
            $"the recorded timer fired at event {fired.EventId} and the check it woke is at {lastCheck.EventId}.");
    }

    [Fact]
    public async Task TheFinalizedPathRunsBothStepsOnceInsideItsOwnBound()
    {
        var history = await JobHistoryFiles.ReadAsync(JobHistoryFixtures.FinalizedFile);

        Assert.Equal(1, Scheduled(history, JobTransitionActivities.TransitionJobName));
        Assert.Equal(1, Scheduled(history, ValidateAndPartitionActivity.Name));
        Assert.Equal(1, Scheduled(history, JobTransitionActivities.FinalizeJobName));

        // The first stage comes before the data step and the finalising step after it.
        var transition = EventId(history, JobTransitionActivities.TransitionJobName);
        var partition = EventId(history, ValidateAndPartitionActivity.Name);
        var finalize = EventId(history, JobTransitionActivities.FinalizeJobName);
        Assert.NotNull(transition);
        Assert.NotNull(partition);
        Assert.NotNull(finalize);
        Assert.True(transition < partition, $"the first stage is at event {transition} and the data step at {partition}.");
        Assert.True(partition < finalize, $"the data step is at event {partition} and the finalising step at {finalize}.");

        // No child execution is started by this path, and the run completed.
        Assert.DoesNotContain(history.Events, entry => entry.EventType == EventType.StartChildWorkflowExecutionInitiated);
        Assert.Equal(EventType.WorkflowExecutionCompleted, history.Events.Last().EventType);

        var rescheduled = Rescheduled(history);
        var allowed = LongestPathEventsOfTheFinalizedPath + (EventsPerLostWorkflowTask * rescheduled);
        Assert.True(
            history.Events.Count <= allowed,
            $"{JobHistoryFixtures.FinalizedFile} holds {history.Events.Count} events, and the patched path allows {allowed} ({LongestPathEventsOfTheFinalizedPath} plus {EventsPerLostWorkflowTask} per rescheduled workflow task, of which it records {rescheduled}).");
    }

    [Theory]
    [MemberData(nameof(OfTheTemplateStep))]
    public async Task ARecordingOfTheTemplateStepCarriesTheCountMeasuredWhenItWasCaptured(string file)
    {
        var fixture = JobHistoryFixtures.ByFile(file);
        var history = await JobHistoryFiles.ReadAsync(file);

        Assert.True(
            fixture.MeasuredEvents is { } measured,
            $"{file} is a recording of the template step and carries the event count measured when it was captured.");
        Assert.Equal(fixture.MeasuredEvents!.Value, history.Events.Count);
        Assert.True(
            history.Events.Count <= TemplateStepHistoryCeiling,
            $"{file} holds {history.Events.Count} events, and a run that carries the template step keeps at most {TemplateStepHistoryCeiling}.");

        // A recording of a job whose first check already answered: no wait, one check of the
        // input, one preparation of the template and never more than one take.
        Assert.DoesNotContain(history.Events, entry => entry.EventType == EventType.TimerStarted);
        Assert.Equal(1, Scheduled(history, ReportJobActivities.ResolveInputName));
        Assert.Equal(1, Scheduled(history, TemplateResolutionActivities.PrepareTemplateName));
        Assert.InRange(Scheduled(history, TemplateStepContracts.TakeTemplateVersion), 0, 1);
        Assert.Equal(EventType.WorkflowExecutionCompleted, history.Events.Last().EventType);
    }

    [Theory]
    [InlineData(JobHistoryFixtures.TemplateBoundFile, 0, 0, 0, 0, 0)]
    [InlineData(JobHistoryFixtures.TemplateTakenFile, 1, 0, 1, 0, 0)]
    [InlineData(JobHistoryFixtures.TemplateByNameFile, 0, 1, 1, 0, 0)]
    [InlineData(JobHistoryFixtures.TemplateTakeRefusedFile, 1, 0, 0, 0, 1)]
    [InlineData(JobHistoryFixtures.TemplateBaselineFile, 0, 1, 0, 1, 0)]
    public async Task EveryRecordingOfTheTemplateStepShowsThePathItWasTakenOf(
        string file,
        int takes,
        int resolutions,
        int pins,
        int baselinePins,
        int failures)
    {
        var history = await JobHistoryFiles.ReadAsync(file);

        Assert.Equal(takes, Scheduled(history, TemplateStepContracts.TakeTemplateVersion));
        Assert.Equal(resolutions, Scheduled(history, TemplateStepContracts.ResolveTemplateVersion));
        Assert.Equal(pins, Scheduled(history, TemplateResolutionActivities.PinTemplateName));
        Assert.Equal(baselinePins, Scheduled(history, TemplateResolutionActivities.PinBaselineName));
        Assert.Equal(failures, Scheduled(history, ReportJobActivities.FailJobName));

        // A refused take ends the run before any of the steps that follow a pin, and a pinned one
        // carries the two delivered steps of the successful path.
        if (failures > 0)
        {
            Assert.Equal(0, Scheduled(history, JobTransitionActivities.TransitionJobName));
            Assert.Equal(0, Scheduled(history, ValidateAndPartitionActivity.Name));
        }
        else
        {
            Assert.Equal(1, Scheduled(history, JobTransitionActivities.TransitionJobName));
            Assert.Equal(1, Scheduled(history, ValidateAndPartitionActivity.Name));
        }
    }

    private static int Scheduled(WorkflowHistory history, string activityName) =>
        history.Events.Count(entry =>
            entry.EventType == EventType.ActivityTaskScheduled
            && string.Equals(
                entry.ActivityTaskScheduledEventAttributes.ActivityType.Name,
                activityName,
                StringComparison.Ordinal));
    private static long? EventId(WorkflowHistory history, string activityName) =>
        history.Events
            .Where(entry => entry.EventType == EventType.ActivityTaskScheduled
                            && string.Equals(
                                entry.ActivityTaskScheduledEventAttributes.ActivityType.Name,
                                activityName,
                                StringComparison.Ordinal))
            .Select(entry => (long?)entry.EventId)
            .FirstOrDefault();

    private static int Rescheduled(WorkflowHistory history) =>
        history.Events.Count(entry => entry.EventType == EventType.WorkflowTaskTimedOut);
}

/// <summary>The committed recordings as files of this suite.</summary>
internal static class JobHistoryFiles
{
    /// <summary>The full path of the recording <paramref name="file"/>.</summary>
    /// <param name="file">The name the suite reads the recording by.</param>
    public static string PathOf(string file) =>
        Path.Combine(AppContext.BaseDirectory, ReportJobReplayTests.HistoriesDirectory, file);

    /// <summary>Reads one committed recording.</summary>
    /// <param name="file">The name the suite reads the recording by.</param>
    public static async Task<WorkflowHistory> ReadAsync(string file) =>
        WorkflowHistory.FromJson(
            Path.GetFileNameWithoutExtension(file),
            await File.ReadAllTextAsync(PathOf(file), TestContext.Current.CancellationToken));
}
