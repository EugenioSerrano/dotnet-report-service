using ReportService.Testing;
using ReportService.Workers.Orchestration.Jobs;
using Temporalio.Api.Enums.V1;
using Temporalio.Common;
using Temporalio.Worker;
using Temporalio.Workflows;

namespace ReportService.Workers.Orchestration.Tests.Jobs;

/// <summary>
/// The recorded runs of a job after the exhaustion arm grew: every committed recording is kept
/// if it still replays, and it does, because the arm lives in the output's own execution and
/// the job's history never carries its steps. The read of a recorded cause appears nowhere in
/// a job's history, the refusal arm's recording is unchanged at its measured size, and every
/// history stays inside the budget the engine keeps for a run.
/// </summary>
[Collection(JobWorkflowSettings.Name)]
public sealed class PdfOutputHistoryTests
{
    /// <summary>Every committed recording, by the name the suite reads it by.</summary>
    public static TheoryData<string> Recordings { get; } =
        new(JobHistoryFixtures.All.Select(fixture => fixture.File));

    [Theory]
    [MemberData(nameof(Recordings))]
    public async Task EveryCommittedJobRecordingStillReplays(string file)
    {
        var fixture = JobHistoryFixtures.ByFile(file);
        ReportJobWorkflow.Configure(fixture.Backstop, fixture.ContinueAsNewAfter);

        var options = new WorkflowReplayerOptions();
        options.Workflows.Add(WorkflowDefinition.Create(typeof(ReportJobWorkflow)));
        var replayer = new WorkflowReplayer(options);

        await replayer.ReplayWorkflowAsync(
            WorkflowHistory.FromJson(
                Path.GetFileNameWithoutExtension(file),
                await File.ReadAllTextAsync(JobHistoryFiles.PathOf(file), TestContext.Current.CancellationToken)),
            cancellationToken: TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// The read is the output's step, never the job's: no recorded run of a job schedules it,
    /// whatever ended that run.
    /// </summary>
    [Fact]
    public async Task NoCommittedJobRecordingCarriesTheCauseRead()
    {
        foreach (var fixture in JobHistoryFixtures.All)
        {
            var history = await JobHistoryFiles.ReadAsync(fixture.File);

            Assert.Equal(0, Scheduled(history, AttemptCauseActivities.FindLastAttemptCauseName));
        }
    }

    /// <summary>
    /// The refusal arm's recording is the one captured for it: the data step's refusal ends the
    /// job in one attempt, no child execution starts, and the recording stays at the size it was
    /// measured at, inside a ceiling that is a stop and never a bound to widen.
    /// </summary>
    [Fact]
    public async Task TheRefusalArmKeepsItsMeasuredRecordingAndItsBound()
    {
        var fixture = JobHistoryFixtures.RowBoundRefused;
        var history = await JobHistoryFiles.ReadAsync(fixture.File);

        Assert.Equal(fixture.MeasuredEvents, history.Events.Count);
        Assert.True(
            history.Events.Count <= ReportJobHistoryBoundTests.RefusedPathHistoryCeiling,
            $"{fixture.File} holds {history.Events.Count} events, and the refusal path keeps at most {ReportJobHistoryBoundTests.RefusedPathHistoryCeiling}.");
        Assert.Equal(1, Scheduled(history, JobTransitionActivities.FailOutputName));
        Assert.Equal(0, Scheduled(history, AttemptCauseActivities.FindLastAttemptCauseName));
        Assert.DoesNotContain(
            history.Events,
            entry => entry.EventType == EventType.StartChildWorkflowExecutionInitiated);
    }

    /// <summary>Every recording stays inside the budget a run may take.</summary>
    [Fact]
    public async Task EveryCommittedRecordingStaysInsideTheHistoryBudget()
    {
        foreach (var fixture in JobHistoryFixtures.All)
        {
            var history = await JobHistoryFiles.ReadAsync(fixture.File);

            Assert.True(
                history.Events.Count <= 10_000,
                $"{fixture.File} holds {history.Events.Count} events, and a run may take at most 10 000.");
        }
    }

    private static int Scheduled(WorkflowHistory history, string activityName) =>
        history.Events.Count(entry =>
            entry.EventType == EventType.ActivityTaskScheduled
            && string.Equals(
                entry.ActivityTaskScheduledEventAttributes.ActivityType.Name,
                activityName,
                StringComparison.Ordinal));
}
