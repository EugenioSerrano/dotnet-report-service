using ReportService.Jobs;
using ReportService.Testing;
using ReportService.Workers.Orchestration.Jobs;

namespace ReportService.Workers.Orchestration.Tests.Jobs;

/// <summary>
/// What a job ends with when one of its outputs exhausted its attempts: the output's code and
/// the reason it recorded reach the job through the finalising step, and a job whose other
/// output finished keeps that output's outcome in its own row - when every output failed the
/// job is failed with that code, and when one output succeeded beside a failure the job rolls
/// up to partially_succeeded (ADR-048 R2) with no job-level code, each output left as it ended.
/// </summary>
public sealed class ExhaustionFinalisationTests
{
    private static readonly DateTimeOffset Now = ReportJobFixture.Now;

    private const string Hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public async Task TheJobCarriesTheFailedOutputsCodeAndReason()
    {
        var stores = ReportJobFixture.Stores();
        var acceptance = await ReportJobFixture.AcceptedAsync(stores);
        var cancellationToken = TestContext.Current.CancellationToken;
        Assert.Equal(
            TransitionOutcome.Applied,
            await stores.Jobs.TransitionOutputAsync(acceptance.OutputId, Now, cancellationToken));
        Assert.Equal(
            OutputFailureOutcome.Applied,
            await stores.Jobs.FailOutputAsync(
                acceptance.OutputId,
                "RENDER_TIMEOUT",
                "exhausted cause=guard-deadline",
                Now,
                cancellationToken));

        var closed = await stores.Jobs.FinalizeJobAsync(acceptance.ReportJobId, Now.AddMinutes(1), cancellationToken);
        var state = await stores.Jobs.FindForWorkflowAsync(acceptance.ReportJobId, cancellationToken);

        Assert.Equal(JobFinalizationOutcome.Applied, closed.Outcome);
        Assert.Equal(ReportJobStatus.Failed, closed.Status);
        Assert.Equal("RENDER_TIMEOUT", closed.ErrorCode);
        Assert.Equal("RENDER_TIMEOUT", state!.Job.ErrorCode);
        Assert.Equal("exhausted cause=guard-deadline", state.Job.ErrorReason);
    }

    [Fact]
    public async Task ACrashCarriesTheGenericCodeAndTheNoneToken()
    {
        var stores = ReportJobFixture.Stores();
        var acceptance = await ReportJobFixture.AcceptedAsync(stores);
        var cancellationToken = TestContext.Current.CancellationToken;
        await stores.Jobs.TransitionOutputAsync(acceptance.OutputId, Now, cancellationToken);
        Assert.Equal(
            OutputFailureOutcome.Applied,
            await stores.Jobs.FailOutputAsync(
                acceptance.OutputId,
                ReportPdfOutputWorkflow.ExecutionFailed,
                "exhausted cause=none",
                Now,
                cancellationToken));

        var closed = await stores.Jobs.FinalizeJobAsync(acceptance.ReportJobId, Now.AddMinutes(1), cancellationToken);
        var state = await stores.Jobs.FindForWorkflowAsync(acceptance.ReportJobId, cancellationToken);

        Assert.Equal(ReportJobStatus.Failed, closed.Status);
        Assert.Equal(ReportPdfOutputWorkflow.ExecutionFailed, closed.ErrorCode);
        Assert.Equal("exhausted cause=none", state!.Job.ErrorReason);
        Assert.True(AttemptCauseVocabulary.IsExhaustionReason(state.Job.ErrorReason));
    }

    [Fact]
    public async Task AJobWithAnotherFinishedOutputKeepsEachOutputsOwnOutcome()
    {
        var stores = ReportJobFixture.Stores();
        var acceptance = await ReportJobFixture.AcceptedAsync(stores);
        var cancellationToken = TestContext.Current.CancellationToken;
        const string HtmlOutputId = "output-22222222222222222222222222222222";
        stores.Jobs.SeedOutput(acceptance.ReportJobId, HtmlOutputId, OutputFormat.Html);

        Assert.Equal(
            PublishOutcome.Published,
            (await stores.Jobs.PublishAsync(
                new ArtifactRecord(
                    "artifact-22222222222222222222222222222222",
                    acceptance.ReportJobId,
                    HtmlOutputId,
                    OutputFormat.Html,
                    "text/html",
                    $"outputs/{acceptance.ReportJobId}/{HtmlOutputId}/attempt",
                    Hash,
                    2_048,
                    null,
                    Now,
                    Now.AddDays(7)),
                cancellationToken)).Outcome);

        await stores.Jobs.TransitionOutputAsync(acceptance.OutputId, Now, cancellationToken);
        await stores.Jobs.FailOutputAsync(
            acceptance.OutputId,
            "RENDER_TIMEOUT",
            "exhausted cause=watchdog-deadline",
            Now,
            cancellationToken);
        await stores.Jobs.FinalizeJobAsync(acceptance.ReportJobId, Now.AddMinutes(1), cancellationToken);

        var state = await stores.Jobs.FindForWorkflowAsync(acceptance.ReportJobId, cancellationToken);
        var pdf = Assert.Single(state!.Outputs, output => output.Format == OutputFormat.Pdf);
        var html = Assert.Single(state.Outputs, output => output.Format == OutputFormat.Html);

        // ADR-048 R2 (§3.1): one output succeeded (HTML) and one failed (PDF), so S>=1 and
        // F>=1 rolls the job up to partially_succeeded - not failed. §4 decides a
        // partially_succeeded job carries no job-level error code or reason (the per-output
        // failure keeps its own code on its own row). Derived from the ADR, not the FinalizeJob code.
        Assert.Equal(ReportJobStatus.PartiallySucceeded, state.Job.Status);
        Assert.Null(state.Job.ErrorCode);
        Assert.Null(state.Job.ErrorReason);
        Assert.Equal(OutputStatus.Failed, pdf.Status);
        Assert.Equal("RENDER_TIMEOUT", pdf.ErrorCode);
        Assert.Equal(OutputStatus.Succeeded, html.Status);
        Assert.Null(html.ErrorCode);
        Assert.Null(html.ErrorReason);
    }
}
