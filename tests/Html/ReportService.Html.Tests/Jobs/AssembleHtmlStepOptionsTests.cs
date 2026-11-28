using ReportService.Workers.Orchestration.Jobs;

namespace ReportService.Workers.Orchestration.Tests.Jobs;

/// <summary>
/// The execution settings of the HTML assembly step and the shape of its step contract
/// (US-008.BOLT-002, SPEC-261125-1224 §8.1, Phase A.1). The oracles come from the approved
/// SPEC, never from the implementation (G06): the HTML output is assembled on its own
/// <c>report-html</c> task queue — distinct from the PDF worker's <c>report-pdf-rendering</c>
/// queue (ADR-012 §3.4, AC-4) — under a bounded start-to-close with a heartbeat, and the same
/// deterministic-refusal discipline as the PDF path (the same package reaches the same verdict,
/// so a refusal is never retried). Nothing here needs an engine.
///
/// DESIGNED PUBLIC API (tester, for the executor — SPEC Phase A.1, §14 is an L2 seam):
///   static class HtmlOutputStepContracts { const string AssembleHtml = "AssembleHtml"; }
///   sealed record AssembleHtmlStepInput(string ReportJobId, string OutputId);
///   sealed record AssembleHtmlStepResult(string ObjectKey, string Sha256, long SizeBytes);
///   interface IAssembleHtmlStep { [Activity(HtmlOutputStepContracts.AssembleHtml)] Task&lt;AssembleHtmlStepResult&gt; RunAsync(AssembleHtmlStepInput input); }
///   static class AssembleHtmlStepOptions { const string Queue = "report-html"; … Create(); }
/// </summary>
public sealed class AssembleHtmlStepOptionsTests
{
    [Fact]
    public void TheAssemblyRunsOnTheHtmlQueueUnderItsOwnBounds()
    {
        var options = AssembleHtmlStepOptions.Create();

        Assert.Equal("report-html", options.TaskQueue);
        Assert.Equal(AssembleHtmlStepOptions.Queue, options.TaskQueue);
        Assert.Equal(TimeSpan.FromSeconds(120), options.StartToCloseTimeout);
        Assert.Equal(TimeSpan.FromSeconds(30), options.HeartbeatTimeout);
        Assert.Equal(3, options.RetryPolicy!.MaximumAttempts);
        Assert.Equal(TimeSpan.FromSeconds(2), options.RetryPolicy.InitialInterval);
        Assert.Equal(2f, options.RetryPolicy.BackoffCoefficient);
        Assert.Equal(TimeSpan.FromSeconds(60), options.RetryPolicy.MaximumInterval);
        // The assembly acknowledges the cancellation before the workflow finalises the job
        // (ADR-012 §3.10(3)).
        Assert.Equal(Temporalio.Workflows.ActivityCancellationType.WaitCancellationCompleted, options.CancellationType);
    }

    [Fact]
    public void TheHtmlQueueIsNotThePdfRenderingQueue()
    {
        // AC-4 at the settings level: the HTML assembly is never scheduled on the PDF worker's
        // Chromium queue, so an HTML-only job schedules no work there and no browser is started.
        Assert.NotEqual("report-pdf-rendering", AssembleHtmlStepOptions.Queue);
        Assert.NotEqual(RenderPdfBlockStepOptions.Queue, AssembleHtmlStepOptions.Queue);
    }

    [Fact]
    public void TheNeverRetriedCodesAreTheDeterministicRefusalsOfTheHtmlPath()
    {
        Assert.Equal(
            ["UNSUPPORTED_CAPABILITY", "TEMPLATE_INVALID", "DATA_INVALID", "QUOTA_EXCEEDED", "EXECUTION_FAILED", "IMAGE_SOURCE_INVALID"],
            AssembleHtmlStepOptions.NonRetryableErrorTypes);
        Assert.Equal(
            AssembleHtmlStepOptions.NonRetryableErrorTypes,
            AssembleHtmlStepOptions.Create().RetryPolicy!.NonRetryableErrorTypes);
    }

    [Fact]
    public void TheStepContractCarriesTheJobAndOutputAndAnswersTheStoredObjectWithNoPageCount()
    {
        // HTML has no pages: the result carries the object's key, hash and size and nothing else.
        var input = new AssembleHtmlStepInput("job-0123456789abcdef0123456789abcdef", "output-0123456789abcdef0123456789abcdef");
        var result = new AssembleHtmlStepResult("outputs/job/output/attempt", new string('a', 64), 2_048);

        Assert.Equal("AssembleHtml", HtmlOutputStepContracts.AssembleHtml);
        Assert.Equal("job-0123456789abcdef0123456789abcdef", input.ReportJobId);
        Assert.Equal("output-0123456789abcdef0123456789abcdef", input.OutputId);
        Assert.Equal("outputs/job/output/attempt", result.ObjectKey);
        Assert.Equal(new string('a', 64), result.Sha256);
        Assert.Equal(2_048, result.SizeBytes);
    }
}
