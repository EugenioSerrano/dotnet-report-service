using ReportService.Workers.Orchestration.Jobs;

namespace ReportService.Workers.Orchestration.Tests.Jobs;

/// <summary>
/// The execution settings of the two steps that record and clear a job's waiting
/// stage: the orchestration queue, one attempt bounded at half a minute, the waits
/// between attempts, and no attempt limit at all — a database that is briefly
/// unreachable makes a job wait longer, never fail.
/// </summary>
public sealed class InputWaitActivityOptionsTests
{
    [Fact]
    public void TheStepsRunOnTheOrchestrationQueue()
    {
        Assert.Equal("report-orchestration", InputWaitActivityOptions.Queue);
        Assert.Equal(ReportJobWorkflow.OrchestrationQueue, InputWaitActivityOptions.Create().TaskQueue);
    }

    [Fact]
    public void OneAttemptIsBoundedAtHalfAMinute()
    {
        Assert.Equal(TimeSpan.FromSeconds(30), InputWaitActivityOptions.Create().StartToCloseTimeout);
    }

    [Fact]
    public void FailuresAreRetriedWithoutAnAttemptLimit()
    {
        var policy = InputWaitActivityOptions.Create().RetryPolicy;

        Assert.NotNull(policy);
        Assert.Equal(TimeSpan.FromSeconds(1), policy!.InitialInterval);
        Assert.Equal(2f, policy.BackoffCoefficient);
        Assert.Equal(TimeSpan.FromSeconds(30), policy.MaximumInterval);
        Assert.Equal(0, policy.MaximumAttempts);
        Assert.Empty(policy.NonRetryableErrorTypes ?? []);
    }

    [Fact]
    public void EveryCallBuildsItsOwnSettings()
    {
        var first = InputWaitActivityOptions.Create();
        var second = InputWaitActivityOptions.Create();

        Assert.NotSame(first, second);
        Assert.NotSame(first.RetryPolicy, second.RetryPolicy);
    }

    [Fact]
    public void TheNotificationKeepsTheMetadataRetryClassAndGivesUpAfterThreeAttempts()
    {
        var options = InputWaitActivityOptions.CreateSignal();

        Assert.Equal("report-orchestration", options.TaskQueue);
        Assert.Equal(TimeSpan.FromSeconds(30), options.StartToCloseTimeout);
        Assert.NotNull(options.RetryPolicy);
        Assert.Equal(TimeSpan.FromSeconds(1), options.RetryPolicy!.InitialInterval);
        Assert.Equal(2f, options.RetryPolicy.BackoffCoefficient);
        Assert.Equal(TimeSpan.FromSeconds(30), options.RetryPolicy.MaximumInterval);
        Assert.Equal(3, options.RetryPolicy.MaximumAttempts);
        Assert.Empty(options.RetryPolicy.NonRetryableErrorTypes ?? []);
    }
}
