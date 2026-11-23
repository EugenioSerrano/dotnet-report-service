using ReportService.Uploads;

namespace ReportService.Workers.Orchestration.Jobs;

/// <summary>
/// What checking a job's data reference came to. The workflow decides by this and
/// nothing else: <see cref="Ready"/> and <see cref="Verifying"/> leave the job where
/// it is, <see cref="Rejected"/> and <see cref="Expired"/> end it with the code the
/// outcome carries, and <see cref="Terminal"/> says the job had already ended and
/// must not be touched again.
/// </summary>
public enum InputOutcome
{
    /// <summary>The upload is verified and its snapshot is fixed: the job may continue.</summary>
    Ready,

    /// <summary>The upload is not usable yet and not unusable either: the job waits where it is.</summary>
    Verifying,

    /// <summary>The upload ended rejected; the code beside the outcome says why.</summary>
    Rejected,

    /// <summary>The upload ran out of time: its session ended, or its verification deadline passed.</summary>
    Expired,

    /// <summary>The job had already ended, so there is nothing to do.</summary>
    Terminal,
}

/// <summary>
/// The result of one check of a job's data reference: the outcome, and — for a
/// rejection — the code the upload itself ended with, so the job ends with exactly
/// that condition and not with one invented here.
/// </summary>
/// <param name="Outcome">How the check ended.</param>
/// <param name="RejectionCode">The upload's own rejection code; present exactly when <see cref="Outcome"/> is <see cref="InputOutcome.Rejected"/>.</param>
public sealed record ResolveInputResult(InputOutcome Outcome, UploadRejectionCode? RejectionCode)
{
    /// <summary>The rejection code of a rejected input.</summary>
    /// <remarks>
    /// A method, not a property: the whole result travels as an activity's answer, and a
    /// computed member would be serialized with it and fail for every outcome that has
    /// no code.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The outcome is not <see cref="InputOutcome.Rejected"/>.</exception>
    public UploadRejectionCode RejectedWith() => Outcome == InputOutcome.Rejected && RejectionCode is { } code
        ? code
        : throw new InvalidOperationException("only a rejected input carries a rejection code.");
}
