using ReportService.Workers.Orchestration.Jobs;
using Temporalio.Exceptions;
using Temporalio.Workflows;

namespace ReportService.Workers.Orchestration.Uploads;

/// <summary>What the verification workflow is asked to verify: one upload, by identifier.</summary>
/// <param name="UploadId">The identifier of the upload.</param>
public sealed record UploadVerificationInput(string UploadId);

/// <summary>
/// One verification, as a workflow: it runs one attempt of the verification
/// activity and returns its result. It holds no clock, no random value and no I/O
/// of its own, so its history replays exactly — everything that can fail or vary
/// happens inside the activity. A failed attempt is retried by the engine under the
/// bounded policy of <see cref="UploadVerificationActivityOptions"/>; what the
/// activity returns is the outcome, and a rejection is an outcome.
/// </summary>
[Workflow(WorkflowType)]
public sealed class UploadVerificationWorkflow
{
    /// <summary>The name the workflow is registered and scheduled under.</summary>
    public const string WorkflowType = "UploadVerification";

    /// <summary>The queue the workflow itself runs on.</summary>
    public const string OrchestrationQueue = "report-orchestration";

    /// <summary>The prefix of the workflow's identifier: one execution per upload.</summary>
    public const string WorkflowIdPrefix = "upload";

    private static TimeSpan _startToClose = TimeSpan.FromHours(1);

    private static TimeSpan _scheduleToClose = TimeSpan.FromHours(6);

    /// <summary>The execution settings the next attempt of a verification is scheduled with.</summary>
    public static ActivityOptions ActivityOptions() => UploadVerificationActivityOptions.Create(_startToClose, _scheduleToClose);

    /// <summary>
    /// Publishes the configured time bound of one attempt to the workflow type. The
    /// delivered worker host instantiates workflows without a service provider, so the
    /// bound is set here, once, when the process plans its workers — before any
    /// execution starts and never per run.
    /// </summary>
    /// <param name="startToClose">How long one attempt may run.</param>
    /// <param name="scheduleToClose">How long the engine may keep scheduling attempts of one verification.</param>
    public static void Configure(TimeSpan startToClose, TimeSpan scheduleToClose)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(startToClose, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(scheduleToClose, TimeSpan.Zero);
        _startToClose = startToClose;
        _scheduleToClose = scheduleToClose;
    }

    /// <summary>The identifier of the one execution that verifies <paramref name="uploadId"/>.</summary>
    /// <param name="uploadId">The identifier of the upload.</param>
    public static string WorkflowIdFor(string uploadId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uploadId);
        return $"{WorkflowIdPrefix}/{uploadId}";
    }

    /// <summary>Verifies one upload.</summary>
    /// <param name="input">The upload to verify.</param>
    [WorkflowRun]
    public async Task<VerifyUploadResult> RunAsync(UploadVerificationInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var result = await Workflow.ExecuteActivityAsync(
            (UploadVerificationActivities activities) => activities.VerifyUploadAsync(new VerifyUploadInput(input.UploadId)),
            ActivityOptions()).ConfigureAwait(true);

        // Every outcome notifies: a late attempt that lost to the deadline must still wake the
        // jobs waiting on the upload, and a nudge that reaches nobody is harmless. The nudge is
        // not a state write and gives up after its few attempts, so a notification that cannot
        // be delivered is logged and the verification's own answer is returned unchanged — an
        // upload is never turned into a failed one because a nudge failed.
        try
        {
            await Workflow.ExecuteActivityAsync(
                (InputWaitSignalActivities activities) => activities.SignalAsync(new SignalWaitingJobsInput(input.UploadId)),
                InputWaitActivityOptions.CreateSignal()).ConfigureAwait(true);
        }
        catch (ActivityFailureException exception)
        {
            Workflow.Logger.LogWarning(
                "Upload {UploadId}: waiting jobs were not notified ({Reason})",
                input.UploadId,
                exception.Message);
        }

        return result;
    }
}
