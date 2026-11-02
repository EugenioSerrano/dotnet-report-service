using ReportService.Jobs;
using ReportService.Workers.Orchestration.Jobs;

namespace ReportService.Workers.Orchestration.Tests.Jobs;

/// <summary>
/// The states a job is moved forward from: exactly the ones its path passes through, and
/// nothing for a state no step of the path writes.
/// </summary>
public sealed class JobTransitionActivitiesTests
{
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
}
