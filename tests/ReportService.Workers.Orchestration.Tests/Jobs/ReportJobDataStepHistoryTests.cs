using ReportService.Testing;
using ReportService.Workers.Orchestration.Jobs;
using Temporalio.Api.Enums.V1;
using Temporalio.Api.History.V1;
using Temporalio.Common;
using Temporalio.Workflows;

namespace ReportService.Workers.Orchestration.Tests.Jobs;

/// <summary>
/// What the two recordings of the data step's own refusals show about the run that wrote
/// them: the step is scheduled once, on the queue its settings name, and the job is ended
/// by one step carrying the refusal's code and diagnostic — with no child execution and no
/// rendering or publishing step anywhere in the history. The documents and the template
/// those runs read are written behind their records by the capture, so each history is the
/// outcome of a real execution and not a hand-built one.
/// </summary>
[Collection(JobWorkflowSettings.Name)]
public sealed class ReportJobDataStepHistoryTests
{
    private const string DataStep = "ValidateAndPartition";

    private const string DataInvalid = "DATA_INVALID";

    private const string TemplateInvalid = "TEMPLATE_INVALID";

    [Theory]
    [InlineData(JobHistoryFixtures.DataRefusedFile, DataInvalid, "reason=truncated")]
    [InlineData(JobHistoryFixtures.TemplateInvalidFile, TemplateInvalid, "file=schema.json")]
    public async Task TheDataStepIsScheduledOnceAndEndsTheJobWithoutRenderingAnything(
        string file,
        string errorCode,
        string diagnostic)
    {
        var history = await JobHistoryFiles.ReadAsync(file);

        var scheduled = Assert.Single(Scheduled(history, DataStep));
        var attributes = scheduled.ActivityTaskScheduledEventAttributes;

        // The step is served from the queue its own settings name, under the policy of the
        // data class — and that policy refuses both codes without a retry.
        Assert.Equal(PdfOutputActivityOptions.DataQueue, attributes.TaskQueue.Name);
        Assert.Equal(TimeSpan.FromHours(1), attributes.StartToCloseTimeout.ToTimeSpan());
        Assert.Contains(DataInvalid, attributes.RetryPolicy.NonRetryableErrorTypes);
        Assert.Contains(TemplateInvalid, attributes.RetryPolicy.NonRetryableErrorTypes);

        // One step ended the job, with the refusal's own code and diagnostic, after the
        // data step ran.
        var failure = Assert.Single(Scheduled(history, ReportJobActivities.FailJobName));
        var input = failure.ActivityTaskScheduledEventAttributes.Input.Payloads_[0].Data.ToStringUtf8();
        Assert.Contains(errorCode, input, StringComparison.Ordinal);
        Assert.Contains(diagnostic, input, StringComparison.Ordinal);
        Assert.True(
            failure.EventId > scheduled.EventId,
            $"{file}: the job is ended at event {failure.EventId} and the data step was scheduled at {scheduled.EventId}.");

        // Nothing was rendered, nothing was published, and no child execution was started.
        Assert.DoesNotContain(history.Events, entry => entry.EventType == EventType.StartChildWorkflowExecutionInitiated);
        Assert.Empty(Scheduled(history, "RenderPdfBlock"));
        Assert.Empty(Scheduled(history, "PublishOutput"));
        Assert.Equal(EventType.WorkflowExecutionCompleted, history.Events.Last().EventType);
    }

    [Fact]
    public async Task EachRecordingCheckedItsInputOnceAndWaitedForNothing()
    {
        foreach (var file in new[] { JobHistoryFixtures.DataRefusedFile, JobHistoryFixtures.TemplateInvalidFile })
        {
            var history = await JobHistoryFiles.ReadAsync(file);

            Assert.Single(Scheduled(history, ReportJobActivities.ResolveInputName));
            Assert.Single(Scheduled(history, DataStep));
            Assert.Single(Scheduled(history, ReportJobActivities.FailJobName));
            Assert.Empty(Scheduled(history, InputWaitActivities.BeginName));
            Assert.Empty(Scheduled(history, InputWaitActivities.EndName));
            Assert.DoesNotContain(history.Events, entry => entry.EventType == EventType.TimerStarted);
        }
    }

    private static IReadOnlyList<HistoryEvent> Scheduled(WorkflowHistory history, string activityName) =>
    [
        .. history.Events.Where(entry =>
            entry.EventType == EventType.ActivityTaskScheduled
            && string.Equals(
                entry.ActivityTaskScheduledEventAttributes.ActivityType.Name,
                activityName,
                StringComparison.Ordinal)),
    ];
}
