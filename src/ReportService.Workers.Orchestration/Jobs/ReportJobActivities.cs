using Microsoft.Extensions.Logging;
using ReportService.Jobs;
using ReportService.ServiceDefaults.Telemetry;
using ReportService.Uploads;
using ReportService.Workers.Orchestration.Telemetry;
using Temporalio.Activities;

namespace ReportService.Workers.Orchestration.Jobs;

/// <summary>What one check of a job's data reference is asked to do: look at one job.</summary>
/// <param name="ReportJobId">The identifier of the job.</param>
public sealed record ResolveInputInput(string ReportJobId);

/// <summary>What ending a job is asked to do: fail it with the condition that ended it.</summary>
/// <param name="ReportJobId">The identifier of the job.</param>
/// <param name="ErrorCode">The code of the condition that ended the job.</param>
/// <param name="Reason">The fixed reason recorded beside the code.</param>
/// <param name="Message">The message the job carries, when the failing step has one.</param>
public sealed record FailJobInput(string ReportJobId, string ErrorCode, string Reason, string? Message = null);

/// <summary>
/// The two steps a job runs today, each one small metadata activity on the
/// orchestration queue. They take and return identifiers, codes and outcomes only,
/// work under the job's own context so every log line and span of theirs carries the
/// job, and look at the job's records by its identifier alone — the identifier comes
/// from the durable request that started them, never from a caller. Nothing here
/// waits, and nothing here decides anything twice: a repeated attempt reads the same
/// records and reaches the same answer.
/// </summary>
public sealed class ReportJobActivities(IJobStore jobs, TimeProvider clock, OrchestrationMetrics metrics, ILoggerFactory loggers, ILogger<ReportJobActivities> logger)
{
    /// <summary>The name the input check is registered and scheduled under.</summary>
    public const string ResolveInputName = "ResolveInput";

    /// <summary>The name the failure is registered and scheduled under.</summary>
    public const string FailJobName = "FailJob";

    /// <summary>
    /// The code the service answers with when a job's data reference cannot be used
    /// any more: an upload whose session ended, or whose verification deadline passed,
    /// is not a reference the job can report on.
    /// </summary>
    public const string InputNotFound = "INPUT_NOT_FOUND";

    /// <summary>
    /// Reads the job and the upload it reports on, once, and answers what the workflow
    /// is to do. A job that already ended answers <see cref="InputOutcome.Terminal"/>;
    /// an upload that is not usable yet answers <see cref="InputOutcome.Verifying"/>
    /// and the job stays where it is, because waiting is a later step's work. An upload
    /// that ran out of time — its session ended, or its verification deadline passed —
    /// answers <see cref="InputOutcome.Expired"/> even when nothing has marked it
    /// expired yet.
    /// </summary>
    /// <param name="input">The job to look at.</param>
    [Activity(ResolveInputName)]
    public async Task<ResolveInputResult> ResolveInputAsync(ResolveInputInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        using var context = JobContext.Begin(input.ReportJobId, loggers);

        var state = await jobs
            .FindForWorkflowAsync(input.ReportJobId, ActivityExecutionContext.Current.CancellationToken)
            .ConfigureAwait(false);
        if (state is null)
        {
            // The identifier came from the durable request that started this execution, so a
            // job it cannot find is a record that no longer exists; retrying cannot create it.
            logger.LogWarning("Job {ReportJobId} is not recorded; there is nothing to do", input.ReportJobId);
            return new ResolveInputResult(InputOutcome.Terminal, null);
        }

        if (state.Job.IsTerminal)
        {
            return new ResolveInputResult(InputOutcome.Terminal, null);
        }

        var result = state.UploadStatus switch
        {
            UploadStatus.Ready => new ResolveInputResult(InputOutcome.Ready, null),
            UploadStatus.Rejected => new ResolveInputResult(InputOutcome.Rejected, state.RejectionCode),
            UploadStatus.Expired => new ResolveInputResult(InputOutcome.Expired, null),

            // A confirming upload is judged by its own deadline, the same rule the acceptance
            // answered with; one that has outlived it is expired even if nothing marked it so.
            _ => UploadExpiry.IsOverdue(
                state.UploadStatus,
                state.UploadExpiresAt,
                state.VerificationDeadline,
                clock.GetUtcNow())
                ? new ResolveInputResult(InputOutcome.Expired, null)
                : new ResolveInputResult(InputOutcome.Verifying, null),
        };

        // A read that ends a wait is the one that sees its outcome: the stage is still the
        // waiting one, because the wait clears it after this answer. A read of a job that is
        // not waiting — one that never waited, or the first check of a job accepted on a
        // confirming upload — is not a wait's outcome and is not counted as one.
        if (result.Outcome is InputOutcome.Ready or InputOutcome.Rejected or InputOutcome.Expired
            && string.Equals(state.Job.Stage, InputWaitStage.WaitingForInput, StringComparison.Ordinal))
        {
            metrics.InputWaitOutcome(result.Outcome switch
            {
                InputOutcome.Ready => OrchestrationMetrics.ReadyOutcome,
                InputOutcome.Rejected => OrchestrationMetrics.RejectedOutcome,
                _ => OrchestrationMetrics.ExpiredOutcome,
            });
        }

        return result;
    }

    /// <summary>
    /// Ends the job and every output of it that had not ended, with the code and the
    /// reason the workflow decided on. A job that already ended is left exactly as it
    /// is, which is the answer a repeated attempt receives and treats as done.
    /// </summary>
    /// <param name="input">The job to end and the condition that ended it.</param>
    [Activity(FailJobName)]
    public async Task FailJobAsync(FailJobInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        using var context = JobContext.Begin(input.ReportJobId, loggers);

        var outcome = await jobs
            .FailJobAsync(
                input.ReportJobId,
                input.ErrorCode,
                input.Reason,
                input.Message,
                clock.GetUtcNow(),
                ActivityExecutionContext.Current.CancellationToken)
            .ConfigureAwait(false);

        if (outcome == JobFailureOutcome.Applied)
        {
            metrics.JobOutcome(OrchestrationMetrics.FailedJobStatus, input.ErrorCode);
            logger.LogInformation(
                "Job {ReportJobId} ended {Status} with {ErrorCode} ({Reason})",
                input.ReportJobId,
                OrchestrationMetrics.FailedJobStatus,
                input.ErrorCode,
                input.Reason);
            return;
        }

        logger.LogInformation("Job {ReportJobId} had already ended; the failure changed nothing", input.ReportJobId);
    }
}
