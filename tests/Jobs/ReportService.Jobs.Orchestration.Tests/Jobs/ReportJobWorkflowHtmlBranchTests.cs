using ReportService.Workers.Orchestration.Jobs;

namespace ReportService.Workers.Orchestration.Tests.Jobs;

/// <summary>
/// The job workflow's HTML branch (US-008.BOLT-002, SPEC-261125-1224 §8.1, Phase D). The branch
/// that starts an output child per requested output under the <c>html-output-path-v1</c> patch is
/// asserted here. The oracles come from the approved intent (AC-4/5/6, ADR-048, ADR-012 §3.1),
/// never from the implementation (G06).
///
/// SURFACED L2 SEAM — SPEC §15 (M1). The behavioural observables the SPEC assigns to this file —
/// an HTML-only job runs the HTML child and NO PDF child; a html+pdf job runs both children
/// independently; a failed/throwing HTML child never cancels a running/succeeded PDF child; the
/// job rolls up (html+pdf HTML-fail → PartiallySucceeded, HTML-only fail → Failed); and, for AC-4,
/// the in-process Temporal test-environment history carries NO <c>ReportPdfOutput</c> child and NO
/// activity on <c>report-pdf-rendering</c> — all require running the real <see cref="ReportJobWorkflow"/>
/// end to end in a Temporal time-skipping environment with every upstream activity stubbed. This
/// repository has NO hermetic Temporal-worker precedent in its test suite (the determinism tests
/// use recorded-history replay; running behaviour is proven at the integration/e2e level), and the
/// full-job-workflow time-skip harness (stubbing ~10 upstream activities + the two output children,
/// plus the fault-injection technique of a stubbed/terminated HTML child) is a test-infrastructure
/// seam the tester surfaces to the human under L2 rather than guessing — exactly the §15 (M1) stop
/// (do NOT add a Temporal client to the e2e project as a fallback, §9). Until that seam is decided
/// with the executor's workflow shape, this file pins the branch oracles that are derivable WITHOUT
/// a running worker. The AC-6 rollup is covered hermetically by the store contract
/// (<c>JobStoreContract</c> R0–R4 via <c>InMemoryJobStoreContractTests</c>); AC-5 and the AC-4
/// no-PDF-artifact job-level observable are covered at the e2e level.
/// </summary>
public sealed class ReportJobWorkflowHtmlBranchTests
{
    private const string ReportJobId = "job-0123456789abcdef0123456789abcdef";

    [Fact]
    public void TheHtmlBranchIsGatedByItsOwnPatchDistinctFromThePrintingPatch()
    {
        // The HTML branch is added behind a new patch so recorded PDF-only runs replay unchanged
        // (ADR-012 §3.1); it is not the PDF printing patch.
        Assert.Equal("html-output-path-v1", ReportJobWorkflow.HtmlOutputPathPatch);
        Assert.NotEqual(ReportJobWorkflow.PrintingPathPatch, ReportJobWorkflow.HtmlOutputPathPatch);
    }

    [Fact]
    public void EachOutputChildOfAJobHasItsOwnIdentitySoBothRunConcurrentlyWithoutCollision()
    {
        // The HTML and PDF children run independently and in parallel (AC-6, Phase D.0): their
        // identifiers are both the job's own id plus their per-format suffix, and never collide.
        var html = ReportHtmlOutputWorkflow.WorkflowIdFor(ReportJobId);
        var pdf = ReportPdfOutputWorkflow.WorkflowIdFor(ReportJobId);

        Assert.StartsWith(ReportJobWorkflow.WorkflowIdFor(ReportJobId), html, StringComparison.Ordinal);
        Assert.StartsWith(ReportJobWorkflow.WorkflowIdFor(ReportJobId), pdf, StringComparison.Ordinal);
        Assert.EndsWith("/output/html", html, StringComparison.Ordinal);
        Assert.EndsWith("/output/pdf", pdf, StringComparison.Ordinal);
        Assert.NotEqual(html, pdf);
    }
}
