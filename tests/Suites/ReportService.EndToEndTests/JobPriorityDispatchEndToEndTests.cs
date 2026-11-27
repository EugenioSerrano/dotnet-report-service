using System.Globalization;
using ReportService.Testing;
using ReportService.Jobs;
using ReportService.Workers.Orchestration.Jobs;

namespace ReportService.EndToEndTests;

/// <summary>
/// Dispatch by priority class over the running composition, with one render and one merge worker
/// per class: a 1-page high job's block runs on the high class's queue while a multi-block low
/// job's blocks wait on theirs, it publishes as printed with no assembly, and the low job still
/// succeeds; a multi-block high job's assembly runs on the high merge queue; every other step of
/// a job runs on the queue it always ran on; and nothing running is cancelled or retried by the
/// high work. Everything asserted is read from the engine's recorded histories.
/// </summary>
public sealed class JobPriorityDispatchEndToEndTests : IDisposable
{
    private static readonly TimeSpan JobDeadline = TimeSpan.FromMinutes(20);

    private static readonly string[] ClassedTypes =
    [
        PdfOutputStepContracts.RenderPdfBlock,
        AssemblePdfStepContracts.AssemblePdf,
        "TakeTemplateVersion",
        "ResolveTemplateVersion",
    ];

    private readonly PriorityComposition _priority = new();

    /// <inheritdoc />
    public void Dispose() => _priority.Dispose();

    [Fact]
    public async Task AOnePageHighJobRunsOnTheHighQueueWhileALowJobsBlocksWait()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var composition = await _priority.Stage.StartAsync(cancellationToken);
        await _priority.Stage.TakeTemplateAsync(composition, cancellationToken);
        var engine = await PriorityComposition.EngineAsync(composition.App);

        var low = await _priority.SubmitAsync(composition, "low", records: null, cancellationToken);

        // The precondition: the low job has blocks scheduled on its queue and not started.
        await Wait.UntilAsync(
            "the low job holding a block waiting on the low render queue",
            token => PriorityComposition.HasWaitingWorkAsync(engine, low, token),
            TimeSpan.FromMinutes(5),
            TimeSpan.FromMilliseconds(250),
            cancellationToken);

        var high = await _priority.SubmitAsync(composition, "high", records: 1, cancellationToken);
        var highEnded = await _priority.Stage.WaitForEndAsync(composition, high, cancellationToken, JobDeadline);
        var lowAtHighEnd = await _priority.Stage.WaitForEndAsync(composition, low, cancellationToken, TimeSpan.Zero).ContinueWith(
            static task => task.IsCompletedSuccessfully ? task.Result.Status : "RUNNING",
            TaskScheduler.Default);

        Assert.Equal("SUCCEEDED", highEnded.Status);
        Assert.NotNull(highEnded.ArtifactId);
        Assert.Equal("RUNNING", lowAtHighEnd);

        var highOutput = await PriorityComposition.ScheduledAsync(engine, ReportPdfOutputWorkflow.WorkflowIdFor(high), cancellationToken);
        var highRender = Assert.Single(highOutput, activity => activity.Type == PdfOutputStepContracts.RenderPdfBlock);
        Assert.Equal(PriorityTaskQueues.For("report-pdf-rendering", JobPriority.High), highRender.Queue);
        Assert.Equal(1, highRender.Attempt);
        Assert.DoesNotContain(highOutput, activity => activity.Type == AssemblePdfStepContracts.AssemblePdf);

        var lowEnded = await _priority.Stage.WaitForEndAsync(composition, low, cancellationToken, JobDeadline);
        Assert.Equal("SUCCEEDED", lowEnded.Status);
        var lowOutput = await PriorityComposition.ScheduledAsync(engine, ReportPdfOutputWorkflow.WorkflowIdFor(low), cancellationToken);
        var lowRenders = lowOutput.Where(activity => activity.Type == PdfOutputStepContracts.RenderPdfBlock).ToList();
        Assert.NotEmpty(lowRenders);
        Assert.All(lowRenders, render => Assert.Equal("report-pdf-rendering-low", render.Queue));
        Assert.All(lowRenders, render => Assert.Equal(1, render.Attempt));

        // Blocks of the low job that were scheduled before the high job ran and started only after it.
        var overtaken = lowRenders.Count(render => render.Scheduled < highRender.Started && render.Started > highRender.Started);
        PriorityComposition.Evidence(
            "priority-dispatch-one-page.txt",
            string.Create(CultureInfo.InvariantCulture, $"high_render_queue={highRender.Queue} high_render_started={highRender.Started:O} low_blocks={lowRenders.Count} low_blocks_waiting_when_high_ran={overtaken}{Environment.NewLine}"));
        Assert.True(overtaken > 0, "at least one low block scheduled before the high one had to start after it");
    }

    [Fact]
    public async Task AMultiBlockHighJobsAssemblyRunsOnTheHighMergeQueueAndEveryOtherStepKeepsItsQueue()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var composition = await _priority.Stage.StartAsync(cancellationToken);
        await _priority.Stage.TakeTemplateAsync(composition, cancellationToken);
        var engine = await PriorityComposition.EngineAsync(composition.App);

        var high = await _priority.SubmitAsync(composition, "high", records: null, cancellationToken);
        var ended = await _priority.Stage.WaitForEndAsync(composition, high, cancellationToken, JobDeadline);
        Assert.Equal("SUCCEEDED", ended.Status);

        var output = await PriorityComposition.ScheduledAsync(engine, ReportPdfOutputWorkflow.WorkflowIdFor(high), cancellationToken);
        var assembly = Assert.Single(output, activity => activity.Type == AssemblePdfStepContracts.AssemblePdf);
        Assert.Equal("report-pdf-merge-high", assembly.Queue);
        Assert.All(
            output.Where(activity => activity.Type == PdfOutputStepContracts.RenderPdfBlock),
            render => Assert.Equal("report-pdf-rendering-high", render.Queue));

        // Every other step of the job and of its output runs on the queue it always ran on.
        var job = await PriorityComposition.ScheduledAsync(engine, ReportJobWorkflow.WorkflowIdFor(high), cancellationToken);
        var unclassed = job.Concat(output).Where(activity => !ClassedTypes.Contains(activity.Type, StringComparer.Ordinal)).ToList();
        Assert.NotEmpty(unclassed);
        Assert.All(unclassed, activity => Assert.Equal(JobPriority.Normal, PriorityTaskQueues.ClassOf(activity.Queue)));
        Assert.All(
            job.Where(activity => activity.Type is "TakeTemplateVersion" or "ResolveTemplateVersion"),
            step => Assert.Equal("report-pdf-rendering-high", step.Queue));
    }
}
