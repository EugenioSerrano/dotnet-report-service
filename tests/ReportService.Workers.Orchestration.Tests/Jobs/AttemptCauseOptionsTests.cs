using ReportService.Workers.Orchestration.Jobs;

namespace ReportService.Workers.Orchestration.Tests.Jobs;

/// <summary>
/// The settings of the read of a block's last recorded cause, asserted value by value: it runs
/// on the queue the job's own steps run on, under the metadata class - a 30 s bound on one
/// attempt, the waits between attempts growing to 30 s, and no attempt limit at all, so a
/// database outage makes the output wait rather than fail. Nothing here needs an engine.
/// </summary>
public sealed class AttemptCauseOptionsTests
{
    [Fact]
    public void TheReadRunsOnTheOrchestrationQueueUnderTheMetadataClass()
    {
        var options = AttemptCauseActivities.Options();

        Assert.Equal(ReportJobWorkflow.OrchestrationQueue, options.TaskQueue);
        Assert.Equal(TimeSpan.FromSeconds(30), options.StartToCloseTimeout);
        Assert.Null(options.ScheduleToCloseTimeout);
        Assert.NotNull(options.RetryPolicy);
        Assert.Equal(TimeSpan.FromSeconds(1), options.RetryPolicy!.InitialInterval);
        Assert.Equal(2f, options.RetryPolicy.BackoffCoefficient);
        Assert.Equal(TimeSpan.FromSeconds(30), options.RetryPolicy.MaximumInterval);
        Assert.Equal(0, options.RetryPolicy.MaximumAttempts);
    }

    [Fact]
    public void NoAttemptLimitIsSetSoADatabaseOutageMakesTheOutputWait()
    {
        // The engine reads an attempt limit of zero as "no maximum": the read is retried until
        // it succeeds, and a database outage never fails the output through it.
        Assert.Equal(0, AttemptCauseActivities.Options().RetryPolicy!.MaximumAttempts);
    }

    [Fact]
    public void TheReadIsTheStepTheRegistrationServes()
    {
        Assert.Equal("FindLastAttemptCause", AttemptCauseActivities.FindLastAttemptCauseName);
    }
}
