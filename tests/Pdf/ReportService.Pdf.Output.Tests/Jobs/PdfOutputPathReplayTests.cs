using ReportService.Workers.Orchestration.Jobs;
using Temporalio.Api.Enums.V1;
using Temporalio.Api.History.V1;
using Temporalio.Common;
using Temporalio.Worker;
using Temporalio.Workflows;

namespace ReportService.Workers.Orchestration.Tests.Jobs;

/// <summary>
/// The PDF output's own execution, replayed from runs recorded against a real engine for each path
/// it can take: one block with and without a per-page code, several blocks with a code, a failure
/// that ends the output, a run that continues as a new one at the window and the run after it, an
/// output whose job was promoted while it waited, and a cancelled output. Each replays under the
/// window it was recorded with, and each still shows the fact that names its path, so a change that
/// would break a running output fails here instead of in production.
/// </summary>
[Collection(JobWorkflowSettings.Name)]
public sealed class PdfOutputPathReplayTests
{
    /// <summary>The window every recording was captured with: the composition's default.</summary>
    public const int RecordedWindow = 2;

    /// <summary>The recordings, by the name the suite reads them by.</summary>
    public static TheoryData<string> Recordings { get; } = new(
        "pdf-output-single-block.json",
        "pdf-output-single-block-page-code.json",
        "pdf-output-multi-block-page-code.json",
        "pdf-output-over-capacity.json",
        "pdf-output-known-bad.json",
        "pdf-output-continued-run-1.json",
        "pdf-output-continued-run-2.json",
        "pdf-output-aging-promoted.json",
        "pdf-output-cancelled.json");

    [Theory]
    [MemberData(nameof(Recordings))]
    public async Task EveryRecordedPathOfThePdfOutputReplays(string file)
    {
        var history = await ReadAsync(file);
        var previous = ReportPdfOutputWorkflow.CurrentWindow();
        ReportPdfOutputWorkflow.Configure(RecordedWindow);
        try
        {
            var options = new WorkflowReplayerOptions();
            options.Workflows.Add(WorkflowDefinition.Create(typeof(ReportPdfOutputWorkflow)));
            await new WorkflowReplayer(options).ReplayWorkflowAsync(history, cancellationToken: TestContext.Current.CancellationToken);
        }
        finally
        {
            ReportPdfOutputWorkflow.Configure(previous);
        }
    }

    [Fact]
    public async Task OneBlockWithoutACodeIsPublishedWithoutAnAssembly()
    {
        var history = await ReadAsync("pdf-output-single-block.json");

        Assert.Equal(1, Scheduled(history, PdfOutputStepContracts.RenderPdfBlock));
        Assert.Equal(1, Scheduled(history, TemplateResolutionActivities.ReadPageCodeDeclarationName));
        Assert.Equal(0, Scheduled(history, AssemblePdfStepContracts.AssemblePdf));
        Assert.Equal(1, Scheduled(history, PdfOutputActivities.PublishOutputName));
    }

    [Fact]
    public async Task OneBlockWithACodeGoesThroughTheAssembly()
    {
        var history = await ReadAsync("pdf-output-single-block-page-code.json");

        Assert.Equal(1, Scheduled(history, PdfOutputStepContracts.RenderPdfBlock));
        Assert.Equal(1, Scheduled(history, TemplateResolutionActivities.ReadPageCodeDeclarationName));
        Assert.Equal(1, Scheduled(history, AssemblePdfStepContracts.AssemblePdf));
        Assert.Equal(1, Scheduled(history, PdfOutputActivities.PublishOutputName));
    }

    [Fact]
    public async Task SeveralBlocksAreAssembledWithoutReadingTheDeclaration()
    {
        var history = await ReadAsync("pdf-output-multi-block-page-code.json");

        Assert.True(Scheduled(history, PdfOutputStepContracts.RenderPdfBlock) >= 2);
        Assert.Equal(0, Scheduled(history, TemplateResolutionActivities.ReadPageCodeDeclarationName));
        Assert.Equal(1, Scheduled(history, AssemblePdfStepContracts.AssemblePdf));
    }

    [Theory]
    [InlineData("pdf-output-over-capacity.json", "OUTPUT_LIMIT_EXCEEDED")]
    [InlineData("pdf-output-known-bad.json", "PAGINATION_FAILED")]
    public async Task AFailedOutputEndsWithItsCodeAndPublishesNothing(string file, string code)
    {
        var history = await ReadAsync(file);

        var failure = Assert.Single(ScheduledEvents(history, JobTransitionActivities.FailOutputName));
        Assert.Contains(code, failure.ActivityTaskScheduledEventAttributes.Input.Payloads_[0].Data.ToStringUtf8(), StringComparison.Ordinal);
        Assert.Equal(0, Scheduled(history, PdfOutputActivities.PublishOutputName));
        Assert.Equal(EventType.WorkflowExecutionCompleted, history.Events.Last().EventType);
    }

    [Fact]
    public async Task ARunContinuesAsANewOneAtItsShareAndTheNextRunFinishesTheOutput()
    {
        var first = await ReadAsync("pdf-output-continued-run-1.json");
        var second = await ReadAsync("pdf-output-continued-run-2.json");

        Assert.Equal(PdfOutputWindow.ContinueAsNewAfter, Scheduled(first, PdfOutputStepContracts.RenderPdfBlock));
        Assert.Equal(EventType.WorkflowExecutionContinuedAsNew, first.Events.Last().EventType);
        Assert.Equal(
            first.Events.Last().WorkflowExecutionContinuedAsNewEventAttributes.NewExecutionRunId,
            second.Events.First().WorkflowExecutionStartedEventAttributes.OriginalExecutionRunId);
        Assert.Equal(1, Scheduled(second, AssemblePdfStepContracts.AssemblePdf));
        Assert.Equal(1, Scheduled(second, PdfOutputActivities.PublishOutputName));
    }

    [Fact]
    public async Task AnOutputWhoseJobWaitedIsPromotedOnce()
    {
        var history = await ReadAsync("pdf-output-aging-promoted.json");

        Assert.True(Scheduled(history, "ReadPendingDispatch") >= 1);
        Assert.Equal(1, Scheduled(history, "PromoteToNormal"));
        Assert.Equal(1, Scheduled(history, PdfOutputActivities.PublishOutputName));
    }

    [Fact]
    public async Task ACancelledOutputEndsCancelledAndPublishesNothing()
    {
        var history = await ReadAsync("pdf-output-cancelled.json");

        Assert.Contains(history.Events, entry => entry.EventType == EventType.WorkflowExecutionCancelRequested);
        Assert.Equal(EventType.WorkflowExecutionCanceled, history.Events.Last().EventType);
        Assert.Equal(0, Scheduled(history, PdfOutputActivities.PublishOutputName));
    }

    private static async Task<WorkflowHistory> ReadAsync(string file)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Histories", "Pdf", file);
        Assert.True(File.Exists(path), $"the recorded output history {file} is missing from {Path.GetDirectoryName(path)}.");
        return WorkflowHistory.FromJson(
            Path.GetFileNameWithoutExtension(file),
            await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
    }

    private static IEnumerable<HistoryEvent> ScheduledEvents(WorkflowHistory history, string activity) =>
        history.Events.Where(entry =>
            entry.EventType == EventType.ActivityTaskScheduled
            && string.Equals(entry.ActivityTaskScheduledEventAttributes.ActivityType.Name, activity, StringComparison.Ordinal));

    private static int Scheduled(WorkflowHistory history, string activity) => ScheduledEvents(history, activity).Count();
}
