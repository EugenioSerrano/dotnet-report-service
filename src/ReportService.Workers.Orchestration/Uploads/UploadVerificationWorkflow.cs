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

    /// <summary>
    /// Publishes the configured time bound of one attempt to the workflow type. The
    /// delivered worker host instantiates workflows without a service provider, so the
    /// bound is set here, once, when the process plans its workers — before any
    /// execution starts and never per run.
    /// </summary>
    /// <param name="startToClose">How long one attempt may run.</param>
    public static void Configure(TimeSpan startToClose)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(startToClose, TimeSpan.Zero);
        _startToClose = startToClose;
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
        return await Workflow.ExecuteActivityAsync(
            (UploadVerificationActivities activities) => activities.VerifyUploadAsync(new VerifyUploadInput(input.UploadId)),
            UploadVerificationActivityOptions.Create(_startToClose)).ConfigureAwait(true);
    }
}
