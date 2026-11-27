using ReportService.Workers.Orchestration.Jobs;

namespace ReportService.Workers.Orchestration.Tests.Jobs;

/// <summary>
/// The execution settings of a job's data step, asserted value by value: the queue it is
/// served from, the beat it must keep, the bound of one attempt, the wait between attempts,
/// how many attempts it gets, the codes the engine must never retry — a refusal of the data
/// and a template that cannot be used among them — and the queue the job is ended on, which
/// is not the data step's. Nothing here needs an engine.
/// </summary>
public sealed class PdfOutputActivityOptionsTests
{
    [Fact]
    public void TheDataStepRunsOnTheDataQueueUnderItsOwnBounds()
    {
        var options = PdfOutputActivityOptions.Create();

        Assert.Equal("report-data", options.TaskQueue);
        Assert.Equal(PdfOutputActivityOptions.DataQueue, options.TaskQueue);
        Assert.Equal(TimeSpan.FromHours(1), options.StartToCloseTimeout);
        Assert.Equal(TimeSpan.FromSeconds(60), options.HeartbeatTimeout);
        Assert.Equal(TimeSpan.FromSeconds(5), PdfOutputActivityOptions.HeartbeatEvery);
        Assert.Equal(TimeSpan.FromSeconds(60), PdfOutputActivityOptions.HeartbeatTimeout);
        Assert.Equal(5, PdfOutputActivityOptions.MaximumAttempts);
        Assert.Equal(5, options.RetryPolicy!.MaximumAttempts);
        Assert.Equal(TimeSpan.FromSeconds(2), options.RetryPolicy.InitialInterval);
        Assert.Equal(2f, options.RetryPolicy.BackoffCoefficient);
        Assert.Equal(TimeSpan.FromSeconds(60), options.RetryPolicy.MaximumInterval);
        // The step waits for its own cancellation to complete before the workflow finalises the
        // job (ADR-012 §3.10(3)); the SDK's activity default would report it immediately.
        Assert.Equal(Temporalio.Workflows.ActivityCancellationType.WaitCancellationCompleted, options.CancellationType);
    }

    [Fact]
    public void TheNeverRetriedCodesAreTheRefusalsOfTheDataAndOfTheTemplate()
    {
        Assert.Equal(
            ["DATA_INVALID", "RECORD_TOO_LARGE", "QUOTA_EXCEEDED", "EXECUTION_FAILED", "TEMPLATE_INVALID"],
            PdfOutputActivityOptions.NonRetryableErrorTypes);
        Assert.Equal(
            PdfOutputActivityOptions.NonRetryableErrorTypes,
            PdfOutputActivityOptions.Create().RetryPolicy!.NonRetryableErrorTypes);
    }

    [Fact]
    public void TheJobIsEndedOnTheQueueTheEndingStepIsRegisteredOn()
    {
        // The data step and the step that ends the job are served from two queues: the
        // reading on report-data, the ending — carried by ReportJobActivities — on
        // report-orchestration. Sending the ending with the data step's settings would send
        // it to a queue where nothing serves it.
        Assert.Equal(ReportJobActivityOptions.Queue, ReportJobActivityOptions.Create().TaskQueue);
        Assert.Equal("report-orchestration", ReportJobActivityOptions.Create().TaskQueue);
        Assert.NotEqual(PdfOutputActivityOptions.Create().TaskQueue, ReportJobActivityOptions.Create().TaskQueue);
    }
}
