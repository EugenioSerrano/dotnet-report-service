using Temporalio.Api.Enums.V1;
using Temporalio.Common;
using Temporalio.Worker;
using Temporalio.Workflows;
using ReportService.Workers.Orchestration.Jobs;

namespace ReportService.Workers.Orchestration.Tests.Jobs;

/// <summary>
/// Replays the job histories captured against a real engine, so a change that would
/// make a running execution non-deterministic fails here instead of in production. A
/// history file is a fixture: it is generated once, against an engine, by the opt-in
/// capture case of the integration suite, and committed with the change that captured
/// it. The fixtures are part of what this work delivers, so a missing one fails the
/// case: nothing was replayed, and a case that had nothing to check is not a result.
/// </summary>
public sealed class ReportJobReplayTests
{
    /// <summary>The directory the captured histories are committed in.</summary>
    public static readonly string HistoriesDirectory = Path.Combine("Histories", "Jobs");

    /// <summary>A job whose input was ready when the workflow ran.</summary>
    public const string ReadyHistory = "report-job-ready.json";

    /// <summary>A job whose input had been rejected when the workflow ran.</summary>
    public const string RejectedHistory = "report-job-rejected.json";

    /// <summary>A job whose input was still confirming when the workflow ran.</summary>
    public const string VerifyingHistory = "report-job-verifying.json";

    /// <summary>Every committed history of one job run, in the order the cases name them.</summary>
    public static readonly string[] Histories = [ReadyHistory, RejectedHistory, VerifyingHistory];

    [Theory]
    [InlineData(ReadyHistory)]
    [InlineData(RejectedHistory)]
    [InlineData(VerifyingHistory)]
    public async Task EveryCapturedHistoryReplaysWithoutADivergence(string file)
    {
        var path = Path.Combine(AppContext.BaseDirectory, HistoriesDirectory, file);
        Assert.True(
            File.Exists(path),
            $"the recorded job history {file} is missing from {Path.Combine(AppContext.BaseDirectory, HistoriesDirectory)}: the fixture is the outcome of a run against a real engine and it is part of this work, so there is nothing to replay.");

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
/// The bound every job history must stay inside: the longest path of this build is
/// the input check followed by the failure, seventeen events, and a workflow task the
/// engine had to reschedule adds two more. The run starts no timer at all, because it
/// never waits, so a history that carries one is a different design than the one that
/// was decided.
/// </summary>
public sealed class ReportJobHistoryBoundTests
{
    /// <summary>The events the longest path of this build writes.</summary>
    public const int LongestPathEvents = 17;

    /// <summary>The events one rescheduled workflow task adds.</summary>
    public const int EventsPerLostWorkflowTask = 2;

    [Theory]
    [InlineData(ReportJobReplayTests.ReadyHistory)]
    [InlineData(ReportJobReplayTests.RejectedHistory)]
    [InlineData(ReportJobReplayTests.VerifyingHistory)]
    public async Task EveryHistoryStaysInsideTheBoundAndCarriesNoTimer(string file)
    {
        var history = await ReadAsync(file);
        var rescheduled = history.Events.Count(entry => entry.EventType == EventType.WorkflowTaskTimedOut);
        var allowed = LongestPathEvents + (EventsPerLostWorkflowTask * rescheduled);

        Assert.True(
            history.Events.Count <= allowed,
            $"{file} holds {history.Events.Count} events, and the longest path allows {allowed} ({LongestPathEvents} plus {EventsPerLostWorkflowTask} per rescheduled workflow task, of which it records {rescheduled}).");
        Assert.DoesNotContain(history.Events, entry => entry.EventType == EventType.TimerStarted);
    }

    [Theory]
    [InlineData(ReportJobReplayTests.ReadyHistory)]
    [InlineData(ReportJobReplayTests.RejectedHistory)]
    [InlineData(ReportJobReplayTests.VerifyingHistory)]
    public async Task EveryHistoryChecksTheInputExactlyOnce(string file)
    {
        var history = await ReadAsync(file);

        var checks = history.Events
            .Where(entry => entry.EventType == EventType.ActivityTaskScheduled)
            .Count(entry => string.Equals(
                entry.ActivityTaskScheduledEventAttributes.ActivityType.Name,
                ReportJobActivities.ResolveInputName,
                StringComparison.Ordinal));

        Assert.Equal(1, checks);
    }

    [Fact]
    public async Task ARejectedInputEndsTheJobWithTheFailureStep()
    {
        var history = await ReadAsync(ReportJobReplayTests.RejectedHistory);

        var failures = history.Events
            .Where(entry => entry.EventType == EventType.ActivityTaskScheduled)
            .Count(entry => string.Equals(
                entry.ActivityTaskScheduledEventAttributes.ActivityType.Name,
                ReportJobActivities.FailJobName,
                StringComparison.Ordinal));

        Assert.Equal(1, failures);
    }

    [Theory]
    [InlineData(ReportJobReplayTests.ReadyHistory)]
    [InlineData(ReportJobReplayTests.VerifyingHistory)]
    public async Task AnInputThatIsNotRejectedRunsNoFailure(string file)
    {
        var history = await ReadAsync(file);

        var failures = history.Events
            .Where(entry => entry.EventType == EventType.ActivityTaskScheduled)
            .Count(entry => string.Equals(
                entry.ActivityTaskScheduledEventAttributes.ActivityType.Name,
                ReportJobActivities.FailJobName,
                StringComparison.Ordinal));

        Assert.Equal(0, failures);
    }

    private static async Task<WorkflowHistory> ReadAsync(string file) =>
        WorkflowHistory.FromJson(
            Path.GetFileNameWithoutExtension(file),
            await File.ReadAllTextAsync(
                Path.Combine(AppContext.BaseDirectory, ReportJobReplayTests.HistoriesDirectory, file),
                TestContext.Current.CancellationToken));
}
