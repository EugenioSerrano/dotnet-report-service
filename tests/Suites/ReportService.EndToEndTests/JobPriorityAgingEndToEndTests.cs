using System.Globalization;
using ReportService.Testing;
using ReportService.Workers.Orchestration.Jobs;
using ReportService.Workers.Orchestration.Telemetry;
using Temporalio.Worker;

namespace ReportService.EndToEndTests;

/// <summary>
/// Aging over the running composition, with a short configured wait. The low class's render
/// worker is stopped, so a low job's blocks stay scheduled and not started; once its pending
/// time reaches the wait the job is promoted: the blocks and the assembly dispatched after the
/// promotion run on the normal class's queues, every block already offered to the low queue
/// stays there and completes once the low worker is back, the status reads declared low and
/// effective normal, and the promotion is counted once. A single-block low job is promoted and
/// counted with nothing moved. A job with a PDF and an HTML output is promoted once. An
/// orchestration worker restarted during the accumulation still promotes the job, and the
/// recorded output history replays without a non-determinism error.
/// </summary>
public sealed class JobPriorityAgingEndToEndTests : IDisposable
{
    private static readonly TimeSpan JobDeadline = TimeSpan.FromMinutes(20);

    private static readonly TimeSpan PromotionDeadline = TimeSpan.FromMinutes(3);

    private readonly PriorityComposition _priority = new();

    /// <inheritdoc />
    public void Dispose() => _priority.Dispose();

    [Fact]
    public async Task AStarvedLowJobIsPromotedAndOnlyItsLaterWorkMovesToNormal()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var composition = await _priority.Stage.StartAsync(cancellationToken);
        await _priority.Stage.TakeTemplateAsync(composition, cancellationToken);
        var engine = await PriorityComposition.EngineAsync(composition.App);
        await RestartHarness.StopAsync(composition.App, PriorityComposition.LowRenderWorker, cancellationToken);

        var low = await _priority.SubmitAsync(composition, "low", records: null, cancellationToken);
        await PriorityComposition.WaitForEffectiveAsync(composition, low, "normal", PromotionDeadline, cancellationToken);
        await RestartHarness.StartAsync(composition.App, PriorityComposition.LowRenderWorker, cancellationToken);

        var ended = await _priority.Stage.WaitForEndAsync(composition, low, cancellationToken, JobDeadline);
        Assert.Equal("SUCCEEDED", ended.Status);
        Assert.Equal(("low", "normal"), await PriorityComposition.PriorityOfAsync(composition, low, cancellationToken));

        var output = await PriorityComposition.ScheduledAsync(engine, ReportPdfOutputWorkflow.WorkflowIdFor(low), cancellationToken);
        var renders = output.Where(activity => activity.Type == PdfOutputStepContracts.RenderPdfBlock).ToList();
        var onLow = renders.Where(render => render.Queue == "report-pdf-rendering-low").ToList();
        var onNormal = renders.Where(render => render.Queue == "report-pdf-rendering").ToList();
        Assert.NotEmpty(onLow);
        Assert.NotEmpty(onNormal);
        Assert.Equal(renders.Count, onLow.Count + onNormal.Count);

        // Offered work never moves: every low block was scheduled before every normal one.
        Assert.True(onLow.Max(render => render.Scheduled) <= onNormal.Min(render => render.Scheduled), "a low block was scheduled after the promotion");
        Assert.All(renders, render => Assert.NotNull(render.Completed));
        Assert.Equal("report-pdf-merge", Assert.Single(output, activity => activity.Type == AssemblePdfStepContracts.AssemblePdf).Queue);

        var promotions = await PriorityComposition.CaptureAsync(composition, RestartHarness.OrchestrationWorkerName, OrchestrationMetrics.PriorityPromotionsInstrumentName, cancellationToken);
        Assert.Equal(1, promotions.Sum(point => point.Value));
        Assert.All(promotions, point => Assert.Equal(["from", "to"], point.Tags.Keys.Order(StringComparer.Ordinal)));

        await ReplayAsync(engine, low, cancellationToken);
        PriorityComposition.Evidence(
            "priority-aging-multi-block.txt",
            string.Create(CultureInfo.InvariantCulture, $"blocks_on_low={onLow.Count} blocks_on_normal={onNormal.Count} promotions={promotions.Sum(point => point.Value)}{Environment.NewLine}"));
    }

    [Fact]
    public async Task AStarvedSingleBlockLowJobIsPromotedAndCountedAndItsBlockCompletesOnLow()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var composition = await _priority.Stage.StartAsync(cancellationToken);
        await _priority.Stage.TakeTemplateAsync(composition, cancellationToken);
        var engine = await PriorityComposition.EngineAsync(composition.App);
        await RestartHarness.StopAsync(composition.App, PriorityComposition.LowRenderWorker, cancellationToken);

        var low = await _priority.SubmitAsync(composition, "low", records: 1, cancellationToken);
        await PriorityComposition.WaitForEffectiveAsync(composition, low, "normal", PromotionDeadline, cancellationToken);
        await RestartHarness.StartAsync(composition.App, PriorityComposition.LowRenderWorker, cancellationToken);

        var ended = await _priority.Stage.WaitForEndAsync(composition, low, cancellationToken, JobDeadline);
        Assert.Equal("SUCCEEDED", ended.Status);
        Assert.NotNull(ended.ArtifactId);

        var output = await PriorityComposition.ScheduledAsync(engine, ReportPdfOutputWorkflow.WorkflowIdFor(low), cancellationToken);
        var render = Assert.Single(output, activity => activity.Type == PdfOutputStepContracts.RenderPdfBlock);
        Assert.Equal("report-pdf-rendering-low", render.Queue);
        Assert.DoesNotContain(output, activity => activity.Type == AssemblePdfStepContracts.AssemblePdf);

        var promotions = await PriorityComposition.CaptureAsync(composition, RestartHarness.OrchestrationWorkerName, OrchestrationMetrics.PriorityPromotionsInstrumentName, cancellationToken);
        Assert.Equal(1, promotions.Sum(point => point.Value));
        await ReplayAsync(engine, low, cancellationToken);
    }

    [Fact]
    public async Task ALowJobWithAPdfAndAnHtmlOutputIsPromotedOnceAndNormalAndHighJobsAreNever()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var composition = await _priority.Stage.StartAsync(cancellationToken);
        await _priority.Stage.TakeTemplateAsync(composition, cancellationToken);
        await RestartHarness.StopAsync(composition.App, PriorityComposition.LowRenderWorker, cancellationToken);

        var low = await _priority.SubmitAsync(composition, "low", records: null, cancellationToken, outputs: "\"pdf\",\"html\"");
        var normal = await _priority.SubmitAsync(composition, "normal", records: null, cancellationToken);
        var high = await _priority.SubmitAsync(composition, "high", records: null, cancellationToken);
        await PriorityComposition.WaitForEffectiveAsync(composition, low, "normal", PromotionDeadline, cancellationToken);
        await RestartHarness.StartAsync(composition.App, PriorityComposition.LowRenderWorker, cancellationToken);

        foreach (var job in new[] { low, normal, high })
        {
            Assert.Equal("SUCCEEDED", (await _priority.Stage.WaitForEndAsync(composition, job, cancellationToken, JobDeadline)).Status);
        }

        Assert.Equal(("normal", "normal"), await PriorityComposition.PriorityOfAsync(composition, normal, cancellationToken));
        Assert.Equal(("high", "high"), await PriorityComposition.PriorityOfAsync(composition, high, cancellationToken));
        var promotions = await PriorityComposition.CaptureAsync(composition, RestartHarness.OrchestrationWorkerName, OrchestrationMetrics.PriorityPromotionsInstrumentName, cancellationToken);
        Assert.Equal(1, promotions.Sum(point => point.Value));
    }

    [Fact]
    public async Task ARestartDuringTheAccumulationStillPromotesTheJobAndItsHistoryReplays()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var composition = await _priority.Stage.StartAsync(cancellationToken);
        await _priority.Stage.TakeTemplateAsync(composition, cancellationToken);
        var engine = await PriorityComposition.EngineAsync(composition.App);
        await RestartHarness.StopAsync(composition.App, PriorityComposition.LowRenderWorker, cancellationToken);

        var low = await _priority.SubmitAsync(composition, "low", records: null, cancellationToken);
        await Wait.UntilAsync(
            "the low job's work waiting on the low queue",
            token => PriorityComposition.HasWaitingWorkAsync(engine, low, token),
            TimeSpan.FromMinutes(5),
            TimeSpan.FromMilliseconds(250),
            cancellationToken);

        // Part way through the wait, the orchestration worker goes away and comes back.
        await Task.Delay(PriorityComposition.AgingWait / 2, cancellationToken);
        await RestartHarness.StopAsync(composition.App, RestartHarness.OrchestrationWorkerName, cancellationToken);
        await RestartHarness.StartAsync(composition.App, RestartHarness.OrchestrationWorkerName, cancellationToken);
        await RestartHarness.WaitUntilHealthyAsync(composition.App, RestartHarness.OrchestrationWorkerName, cancellationToken);

        await PriorityComposition.WaitForEffectiveAsync(composition, low, "normal", PromotionDeadline, cancellationToken);
        await RestartHarness.StartAsync(composition.App, PriorityComposition.LowRenderWorker, cancellationToken);
        Assert.Equal("SUCCEEDED", (await _priority.Stage.WaitForEndAsync(composition, low, cancellationToken, JobDeadline)).Status);
        Assert.Equal(("low", "normal"), await PriorityComposition.PriorityOfAsync(composition, low, cancellationToken));

        var output = await PriorityComposition.ScheduledAsync(engine, ReportPdfOutputWorkflow.WorkflowIdFor(low), cancellationToken);
        Assert.Equal(1, output.Count(activity => activity.Type == "PromoteToNormal"));
        await ReplayAsync(engine, low, cancellationToken);
    }

    // The output's recorded history - its latest run - replays through the delivered workflow code.
    private static async Task ReplayAsync(Temporalio.Client.ITemporalClient engine, string reportJobId, CancellationToken cancellationToken)
    {
        var history = await engine.GetWorkflowHandle(ReportPdfOutputWorkflow.WorkflowIdFor(reportJobId))
            .FetchHistoryAsync(new Temporalio.Client.WorkflowHistoryEventFetchOptions { Rpc = new Temporalio.Client.RpcOptions { CancellationToken = cancellationToken } });
        var replayer = new WorkflowReplayer(new WorkflowReplayerOptions().AddWorkflow<ReportPdfOutputWorkflow>());
        var result = await replayer.ReplayWorkflowAsync(history, throwOnReplayFailure: false);
        Assert.Null(result.ReplayFailure);
    }
}
