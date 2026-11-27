using ReportService.Workers.Pdf.Rendering;

namespace ReportService.Workers.Pdf.Tests.Rendering;

/// <summary>
/// The PDF block class's delivered attributes: the heartbeat every 5 s with a 30 s timeout,
/// the 300 s start-to-close, the waits between attempts as 2 s x 2 up to 60 s, the three
/// attempts, the codes that are never retried - and the one that is not among them,
/// <c>RENDER_TIMEOUT</c>, because a guard's deadline is counted against the budget (the
/// metadata class of the exhaustion read is asserted where that activity is added).
/// </summary>
public sealed class PdfOptionsTests
{
    [Fact]
    public void ThePdfClassKeepsTheDeliveredAttributes()
    {
        Assert.Equal("report-pdf-rendering", RenderPdfBlockOptions.Queue);
        Assert.Equal(TimeSpan.FromSeconds(5), RenderPdfBlockOptions.HeartbeatEvery);
        Assert.Equal(TimeSpan.FromSeconds(30), RenderPdfBlockOptions.HeartbeatTimeout);
        Assert.Equal(TimeSpan.FromSeconds(300), RenderPdfBlockOptions.StartToClose);
        Assert.Equal(TimeSpan.FromSeconds(2), RenderPdfBlockOptions.InitialInterval);
        Assert.Equal(2f, RenderPdfBlockOptions.BackoffCoefficient);
        Assert.Equal(TimeSpan.FromSeconds(60), RenderPdfBlockOptions.MaximumInterval);
        Assert.Equal(3, RenderPdfBlockOptions.MaximumAttempts);
    }

    [Fact]
    public void ThePdfPolicyLetsAGuardDeadlineBeRetriedAndRefusesEveryDomainCode()
    {
        var options = RenderPdfBlockOptions.Create();

        Assert.NotNull(options.RetryPolicy);
        Assert.Equal(3, options.RetryPolicy!.MaximumAttempts);
        Assert.Equal(TimeSpan.FromSeconds(2), options.RetryPolicy.InitialInterval);
        Assert.Equal(2f, options.RetryPolicy.BackoffCoefficient);
        Assert.Equal(TimeSpan.FromSeconds(60), options.RetryPolicy.MaximumInterval);
        Assert.Equal(
            ["PAGINATION_FAILED", "UNSUPPORTED_CAPABILITY", "QUOTA_EXCEEDED", "EXECUTION_FAILED", "OUTPUT_LIMIT_EXCEEDED"],
            options.RetryPolicy.NonRetryableErrorTypes);
        Assert.DoesNotContain("RENDER_TIMEOUT", RenderPdfBlockOptions.NonRetryableErrorTypes);

        Assert.Equal(
            "render: heartbeat 5s/30s, start-to-close 300s, backoff 2s x2 max 60s, 3 attempts",
            RenderPdfBlockOptions.RetryPolicySummary);
    }
}
