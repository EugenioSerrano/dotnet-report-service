using ReportService.Workers.Orchestration.Jobs;

namespace ReportService.Workers.Orchestration.Tests.Jobs;

/// <summary>
/// The execution settings of one step of a job, asserted value by value: the queue it
/// runs on, the bound of one attempt, the waits between attempts, the fact that no
/// attempt limit is set, and the codes the engine must never retry. Nothing here needs
/// an engine.
/// </summary>
public sealed class ReportJobActivityOptionsTests
{
    [Fact]
    public void EveryValueOfOneStepIsTheOneTheDecisionFixed()
    {
        var options = ReportJobActivityOptions.Create();

        Assert.Equal("report-orchestration", options.TaskQueue);
        Assert.Equal(TimeSpan.FromSeconds(30), options.StartToCloseTimeout);
        Assert.Null(options.ScheduleToCloseTimeout);
        Assert.NotNull(options.RetryPolicy);
        Assert.Equal(TimeSpan.FromSeconds(1), options.RetryPolicy!.InitialInterval);
        Assert.Equal(2f, options.RetryPolicy.BackoffCoefficient);
        Assert.Equal(TimeSpan.FromSeconds(30), options.RetryPolicy.MaximumInterval);
        Assert.Equal(0, options.RetryPolicy.MaximumAttempts);
    }

    [Fact]
    public void NoAttemptLimitIsSetSoADatabaseOutageMakesJobsWait()
    {
        // The engine reads an attempt limit of zero as "no maximum": a metadata step is
        // retried until it succeeds, and never fails a job for a cause on the service's side.
        Assert.Equal(0, ReportJobActivityOptions.MaximumAttempts);
        Assert.Equal(0, ReportJobActivityOptions.Create().RetryPolicy!.MaximumAttempts);
    }

    [Fact]
    public void TheNeverRetriedCodesAreTheOnesEveryStepDeclares()
    {
        var options = ReportJobActivityOptions.Create();

        Assert.Equal(
            ["DATA_INVALID", "RECORD_TOO_LARGE", "TEMPLATE_NOT_FOUND", "TEMPLATE_VERSION_CONFLICT", "TEMPLATE_INVALID", "UNSUPPORTED_CAPABILITY", "INPUT_NOT_FOUND", "EXECUTION_FAILED"],
            options.RetryPolicy!.NonRetryableErrorTypes);
    }

    [Fact]
    public void TheSummaryStatesThePolicyTheSessionRecords()
    {
        Assert.Equal("metadata: start-to-close 30s, backoff 1s x2 max 30s, unbounded", ReportJobActivityOptions.RetryPolicySummary);
    }
}
