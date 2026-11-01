using System.Security.Cryptography;
using System.Text;
using ReportService.Storage;
using ReportService.Uploads;
using Temporalio.Activities;

namespace ReportService.Workers.Orchestration.Uploads;

/// <summary>What a verification attempt is asked to do: verify one upload.</summary>
/// <param name="UploadId">The identifier of the upload to verify.</param>
public sealed record VerifyUploadInput(string UploadId);

/// <summary>
/// How an attempt ended, as the workflow receives it: a few bytes carrying the
/// outcome and, for a rejection, its code and reason. A rejection is a result, so
/// nothing is retried for it; only a failure raised by the service is a failure of
/// the attempt.
/// </summary>
/// <param name="Outcome">How the verification ended.</param>
/// <param name="ErrorCode">Why the upload ended rejected, when it did.</param>
/// <param name="Reason">The fixed reason recorded beside the code.</param>
public sealed record VerifyUploadResult(
    VerificationResult Outcome,
    UploadRejectionCode? ErrorCode,
    UploadRejectionReason? Reason);

/// <summary>
/// The activity the workflow runs: it verifies one upload with the process's own
/// verifier and keeps the attempt alive while it does. The attempt's identity comes
/// from the engine — its workflow, its run, its activity and its attempt number —
/// and is turned into the hash that names the object this attempt writes, so two
/// attempts of one verification never write to the same key and a retried attempt
/// is always a new one. Rejections leave as results; cancellation is passed on.
/// </summary>
public sealed class UploadVerificationActivities(UploadVerifier verifier, TimeProvider clock)
{
    /// <summary>The name the activity is registered and scheduled under.</summary>
    public const string Name = "VerifyUpload";

    /// <summary>How often the activity tells the engine it is still working.</summary>
    public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(5);

    /// <summary>Verifies one upload.</summary>
    /// <param name="input">The upload to verify.</param>
    [Activity(Name)]
    public async Task<VerifyUploadResult> VerifyUploadAsync(VerifyUploadInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var context = ActivityExecutionContext.Current;
        var attemptKey = AttemptKey(input.UploadId, context.Info);

        using var ticker = clock.CreateTimer(
            static _ => Heartbeat(),
            state: null,
            dueTime: HeartbeatInterval,
            period: HeartbeatInterval);

        var outcome = await verifier
            .VerifyAsync(input.UploadId, attemptKey, static _ => Heartbeat(), context.CancellationToken)
            .ConfigureAwait(false);

        return new VerifyUploadResult(outcome.Result, outcome.Code, outcome.Reason);
    }

    /// <summary>
    /// The identifier of one attempt: the SHA-256 of the workflow, the run, the
    /// activity and the attempt number, in lower-case hexadecimal. It is stable
    /// within the attempt and different for every other one.
    /// </summary>
    /// <param name="workflowId">The identifier of the workflow the attempt belongs to.</param>
    /// <param name="runId">The engine's identifier of that execution.</param>
    /// <param name="activityId">The identifier of the scheduled activity.</param>
    /// <param name="attempt">The attempt number, from one.</param>
    public static string AttemptId(string workflowId, string runId, string activityId, int attempt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workflowId);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(activityId);
        ArgumentOutOfRangeException.ThrowIfLessThan(attempt, 1);
        var material = $"{workflowId}\n{runId}\n{activityId}\n{attempt}";
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
    }

    /// <summary>The identifier of the running attempt.</summary>
    /// <param name="info">The engine's description of the running attempt.</param>
    public static string AttemptId(ActivityInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        return AttemptId(info.WorkflowId ?? string.Empty, info.WorkflowRunId ?? string.Empty, info.ActivityId ?? string.Empty, info.Attempt);
    }

    /// <summary>The key one attempt writes its copy of the upload's bytes under.</summary>
    /// <param name="uploadId">The upload being verified.</param>
    /// <param name="info">The engine's description of the running attempt.</param>
    public static ObjectKey AttemptKey(string uploadId, ActivityInfo info) =>
        AttemptKey(uploadId, AttemptId(info));

    /// <summary>The key one attempt writes its copy of the upload's bytes under.</summary>
    /// <param name="uploadId">The upload being verified.</param>
    /// <param name="attemptId">The attempt's identifier.</param>
    public static ObjectKey AttemptKey(string uploadId, string attemptId) =>
        ObjectKey.FromSegments("snapshots", uploadId, attemptId);

    // Tells the engine the attempt is alive; outside an attempt there is nothing to tell.
    private static void Heartbeat()
    {
        try
        {
            ActivityExecutionContext.Current.Heartbeat();
        }
        catch (InvalidOperationException)
        {
            // The progress callback runs on a timer thread that is not inside an attempt.
        }
    }
}
