using ReportService.Workers.Orchestration.Jobs;
using Temporalio.Workflows;

namespace ReportService.Workers.Orchestration.Tests.Jobs;

/// <summary>
/// The execution settings with which the orchestration worker schedules one block render on the
/// PDF worker's queue, asserted value by value. The block render is one of the long steps a
/// cancellation waits for (ADR-012 §3.10(3)): the workflow is told about the cancellation only
/// after the render has acknowledged it — its page, context and attempt directory released — so
/// the job's finalisation never runs while the render is still cleaning up. Nothing here needs
/// an engine.
/// </summary>
public sealed class PdfOutputStepOptionsTests
{
    [Fact]
    public void TheBlockRenderRunsUnderItsOwnBoundsAndWaitsForItsCancellation()
    {
        var options = RenderPdfBlockStepOptions.Create();

        Assert.Equal("report-pdf-rendering", options.TaskQueue);
        Assert.Equal(RenderPdfBlockStepOptions.Queue, options.TaskQueue);
        Assert.Equal(TimeSpan.FromSeconds(300), options.StartToCloseTimeout);
        Assert.Equal(TimeSpan.FromSeconds(30), options.HeartbeatTimeout);
        Assert.Equal(3, options.RetryPolicy!.MaximumAttempts);
        Assert.Equal(TimeSpan.FromSeconds(2), options.RetryPolicy.InitialInterval);
        Assert.Equal(2f, options.RetryPolicy.BackoffCoefficient);
        Assert.Equal(TimeSpan.FromSeconds(60), options.RetryPolicy.MaximumInterval);
        Assert.Equal(RenderPdfBlockStepOptions.NonRetryableErrorTypes, options.RetryPolicy.NonRetryableErrorTypes);

        // The SDK's activity default is TryCancel, which reports the cancellation without waiting
        // for the render's own cleanup; the wind-down cannot hold on that.
        Assert.Equal(ActivityCancellationType.WaitCancellationCompleted, options.CancellationType);
    }

    [Fact]
    public void TheDeclaredPolicySummaryIsTheRendersOwn()
    {
        Assert.Equal(
            "render: heartbeat 5s/30s, start-to-close 300s, backoff 2s x2 max 60s, 3 attempts",
            RenderPdfBlockStepOptions.RetryPolicySummary);
    }
}
