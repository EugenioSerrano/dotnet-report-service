using ReportService.Workers.Orchestration.Jobs;
using Temporalio.Exceptions;

namespace ReportService.Workers.Orchestration.Tests.Jobs;

/// <summary>
/// The HTML output's own execution as the workflow type declares it (US-008.BOLT-002,
/// SPEC-261125-1224 §8.1, Phase B): the name and the identifier it is started under — one
/// execution per output, the <c>/output/html</c> suffix under the job's own identifier — and the
/// code an output ends with for each kind of failure. The HTML child mirrors the PDF child's
/// shape but carries the HTML domain code set only (no render-deadline/pagination codes, Phase B:
/// the HTML assembly has no attempt-cause/exhaustion read). The running behaviour — the child
/// catches its own <see cref="ActivityFailureException"/> and ends its own output, an uncaught
/// non-activity throw surfaces to the parent — is asserted at the orchestration-history level in
/// <c>ReportJobWorkflowHtmlBranchTests</c>; what is asserted here is the mapping a history cannot
/// show. The oracles come from the approved intent, never from the implementation (G06).
///
/// DESIGNED PUBLIC API (tester, for the executor — SPEC Phase B):
///   sealed record ReportHtmlOutputInput(string ReportJobId, string OutputId);
///   [Workflow("ReportHtmlOutput")] sealed class ReportHtmlOutputWorkflow {
///       const string WorkflowType = "ReportHtmlOutput";
///       const string WorkflowIdSuffix = "/output/html";
///       const string ExecutionFailed = "EXECUTION_FAILED";
///       static string WorkflowIdFor(string reportJobId);
///       static string CodeOf(Exception? failure);  // HTML domain codes, else EXECUTION_FAILED
///       [WorkflowRun] Task RunAsync(ReportHtmlOutputInput input); }
/// </summary>
public sealed class ReportHtmlOutputWorkflowTests
{
    private const string ReportJobId = "job-0123456789abcdef0123456789abcdef";

    private const string OutputId = "output-0123456789abcdef0123456789abcdef";

    [Fact]
    public void TheWorkflowIsRegisteredUnderTheNameAndIdentifierTheParentStarts()
    {
        Assert.Equal("ReportHtmlOutput", ReportHtmlOutputWorkflow.WorkflowType);
        Assert.Equal(
            "report-job/job-0123456789abcdef0123456789abcdef/output/html",
            ReportHtmlOutputWorkflow.WorkflowIdFor(ReportJobId));
        Assert.StartsWith(
            ReportJobWorkflow.WorkflowIdFor(ReportJobId),
            ReportHtmlOutputWorkflow.WorkflowIdFor(ReportJobId),
            StringComparison.Ordinal);

        // The HTML output's identifier is its own, never the PDF output's: a job may run both
        // children at once, so their identifiers never collide (AC-6, per-output isolation).
        Assert.NotEqual(
            ReportPdfOutputWorkflow.WorkflowIdFor(ReportJobId),
            ReportHtmlOutputWorkflow.WorkflowIdFor(ReportJobId));
    }

    [Theory]
    [InlineData("UNSUPPORTED_CAPABILITY")]
    [InlineData("TEMPLATE_INVALID")]
    [InlineData("DATA_INVALID")]
    [InlineData("QUOTA_EXCEEDED")]
    [InlineData("EXECUTION_FAILED")]
    [InlineData("IMAGE_SOURCE_INVALID")]
    public void ADomainFailureKeepsTheCodeItCarries(string code)
    {
        var failure = new ApplicationFailureException("the step refused", errorType: code, nonRetryable: true);

        Assert.Equal(code, ReportHtmlOutputWorkflow.CodeOf(failure));
    }

    [Fact]
    public void AFailureThatCarriesNoCodeEndsTheOutputWithTheGenericCode()
    {
        Assert.Equal(
            ReportHtmlOutputWorkflow.ExecutionFailed,
            ReportHtmlOutputWorkflow.CodeOf(new InvalidOperationException("the engine lost the step")));
        Assert.Equal(ReportHtmlOutputWorkflow.ExecutionFailed, ReportHtmlOutputWorkflow.CodeOf(null));
        Assert.Equal(
            ReportHtmlOutputWorkflow.ExecutionFailed,
            ReportHtmlOutputWorkflow.CodeOf(new ApplicationFailureException("no code", errorType: null, nonRetryable: false)));
    }

    [Fact]
    public void ARuntimeExceptionTypeIsNotAConsumersCode()
    {
        // The engine reports a .NET exception with its own type name where a code would be;
        // that name is not what a consumer is told the output failed with.
        var failure = new ApplicationFailureException(
            "the step threw", errorType: "InvalidOperationException", nonRetryable: false);

        Assert.Equal(ReportHtmlOutputWorkflow.ExecutionFailed, ReportHtmlOutputWorkflow.CodeOf(failure));
    }

    [Theory]
    [InlineData("PAGINATION_FAILED")]
    [InlineData("RENDER_TIMEOUT")]
    public void APdfOnlyCodeIsNotPartOfTheHtmlDomainSet(string pdfCode)
    {
        // The HTML assembly has no render-deadline or pagination path (Phase B); a code that only
        // the PDF/render path raises is not one the HTML output keeps — it ends with the generic.
        var failure = new ApplicationFailureException("not an html code", errorType: pdfCode, nonRetryable: true);

        Assert.Equal(ReportHtmlOutputWorkflow.ExecutionFailed, ReportHtmlOutputWorkflow.CodeOf(failure));
    }

    [Fact]
    public void TheOutputOfAJobIsTheOneOfTheJobsOwnExecution()
    {
        var input = new ReportHtmlOutputInput(ReportJobId, OutputId);

        Assert.Equal(ReportJobId, input.ReportJobId);
        Assert.Equal(OutputId, input.OutputId);
    }
}
