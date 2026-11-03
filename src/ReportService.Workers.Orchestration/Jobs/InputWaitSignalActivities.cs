using Microsoft.Extensions.Logging;
using ReportService.Jobs;
using ReportService.Workers.Orchestration.Telemetry;
using Temporalio.Activities;

namespace ReportService.Workers.Orchestration.Jobs;

/// <summary>What notifying the waiting jobs is asked to do: tell the jobs of one upload that it changed.</summary>
/// <param name="UploadId">The identifier of the upload whose state changed.</param>
public sealed record SignalWaitingJobsInput(string UploadId);

/// <summary>
/// The step a finished verification runs last: it reads the jobs of its upload that may be
/// waiting — the queued ones carrying the waiting stage and the queued ones that carry no
/// stage yet — and nudges each of them once, in the order they were accepted. The step
/// carries no payload and no state: the nudge only makes a job re-read its upload, so a
/// job that is not waiting consumes it and nothing happens.
/// <para>
/// A job whose execution does not exist yet, or that has already closed, is logged and
/// skipped, and the other jobs are still nudged; any other engine failure fails the attempt,
/// which the engine retries three times and then abandons — a verification is never turned
/// into an expired upload because a nudge could not be delivered, and the wait's own timer
/// is the guarantee behind every notification.
/// </para>
/// </summary>
public sealed class InputWaitSignalActivities(
    IJobStore jobs,
    IWorkflowSignaller signaller,
    OrchestrationMetrics metrics,
    ILogger<InputWaitSignalActivities> logger)
{
    /// <summary>The name the step is registered and scheduled under.</summary>
    public const string Name = "SignalWaitingJobs";

    /// <summary>The span tag the upload identifier is carried on.</summary>
    public const string UploadTagName = "upload.id";

    /// <summary>Notifies every job of <paramref name="input"/>'s upload that may be waiting.</summary>
    /// <param name="input">The upload whose state changed.</param>
    /// <returns>How many executions were actually nudged.</returns>
    [Activity(Name)]
    public async Task<int> SignalAsync(SignalWaitingJobsInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var cancellationToken = ActivityExecutionContext.Current.CancellationToken;
        System.Diagnostics.Activity.Current?.SetTag(UploadTagName, input.UploadId);

        var waiting = await jobs.FindInputWaitingJobIdsAsync(input.UploadId, cancellationToken).ConfigureAwait(false);
        var signalled = 0;
        foreach (var reportJobId in waiting)
        {
            bool delivered;
            try
            {
                delivered = await signaller
                    .SignalInputChangedAsync(ReportJobWorkflow.WorkflowIdFor(reportJobId), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                metrics.InputWaitNotification(OrchestrationMetrics.FailedResult);
                throw;
            }

            if (delivered)
            {
                signalled++;
                metrics.InputWaitNotification(OrchestrationMetrics.SignalledResult);
                continue;
            }

            metrics.InputWaitNotification(OrchestrationMetrics.SkippedResult);
            logger.LogDebug(
                "Upload {UploadId}: job {ReportJobId} has no open execution to nudge",
                input.UploadId,
                reportJobId);
        }

        logger.LogInformation("Upload {UploadId}: {Count} waiting job(s) notified", input.UploadId, signalled);
        return signalled;
    }
}
