using ReportService.Workers.Orchestration.Jobs;
using Temporalio.Api.Enums.V1;
using Temporalio.Common;
using Temporalio.Worker;
using Temporalio.Workflows;

namespace ReportService.Workers.Orchestration.Tests.Jobs;

/// <summary>
/// A multi-block output execution recorded before the continuation drain existed still
/// replays under the current build: the recording carries no drain marker, so the workflow
/// keeps the window decisions it made then, command for command.
/// </summary>
[Collection(JobWorkflowSettings.Name)]
public sealed class PdfOutputChildReplayTests
{
    /// <summary>The recording's file, under the suite's committed histories.</summary>
    public const string RecordingFile = "pdf-output-multi-block-window-2.json";

    /// <summary>The window the recording was captured with: the composition's default.</summary>
    public const int RecordedWindow = 2;

    private static string PathOf() =>
        Path.Combine(AppContext.BaseDirectory, "Histories", "Pdf", RecordingFile);

    [Fact]
    public async Task AMultiBlockOutputRecordedBeforeTheDrainStillReplays()
    {
        var json = await File.ReadAllTextAsync(PathOf(), TestContext.Current.CancellationToken);
        var history = WorkflowHistory.FromJson(Path.GetFileNameWithoutExtension(RecordingFile), json);

        // The recording is the one it claims to be: a multi-block output that never drained.
        var patches = history.Events
            .Where(historyEvent => historyEvent.EventType == EventType.MarkerRecorded)
            .Select(historyEvent => historyEvent.MarkerRecordedEventAttributes.Details["patch-data"].Payloads_[0].Data.ToStringUtf8())
            .ToList();
        Assert.Contains(patches, patch => patch.Contains(ReportPdfOutputWorkflow.MultiBlockRenderPatch, StringComparison.Ordinal));
        Assert.DoesNotContain(patches, patch => patch.Contains(ReportPdfOutputWorkflow.ContinueAsNewDrainPatch, StringComparison.Ordinal));
        Assert.Equal(
            2,
            history.Events.Count(historyEvent =>
                historyEvent.EventType == EventType.ActivityTaskScheduled
                && historyEvent.ActivityTaskScheduledEventAttributes.ActivityType.Name == "RenderPdfBlock"));
        Assert.DoesNotContain(
            history.Events,
            historyEvent => historyEvent.EventType == EventType.WorkflowExecutionContinuedAsNew);

        var previous = ReportPdfOutputWorkflow.CurrentWindow();
        ReportPdfOutputWorkflow.Configure(RecordedWindow);
        try
        {
            var options = new WorkflowReplayerOptions();
            options.Workflows.Add(WorkflowDefinition.Create(typeof(ReportPdfOutputWorkflow)));
            var replayer = new WorkflowReplayer(options);

            await replayer.ReplayWorkflowAsync(history, cancellationToken: TestContext.Current.CancellationToken);
        }
        finally
        {
            ReportPdfOutputWorkflow.Configure(previous);
        }
    }
}
