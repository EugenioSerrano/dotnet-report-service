using ReportService.Uploads;

namespace ReportService.Workers.Orchestration.Uploads;

/// <summary>How a verification ended.</summary>
public enum VerificationResult
{
    /// <summary>A snapshot was fixed, or an earlier attempt had already fixed it.</summary>
    Ready = 0,

    /// <summary>The upload ended rejected with a code and a reason.</summary>
    Rejected = 1,

    /// <summary>The upload does not exist; nothing was done.</summary>
    NotFound = 2,

    /// <summary>The upload is not waiting to be verified; nothing was written.</summary>
    NotVerifying = 3,
}

/// <summary>
/// The result of one verification: how it ended, the code and reason of a
/// rejection, and the snapshot of an upload that is ready. It carries no content
/// byte, no key and no path.
/// </summary>
/// <param name="Result">How the verification ended.</param>
/// <param name="Code">Why the upload ended rejected, when it did.</param>
/// <param name="Reason">The fixed reason recorded beside the code.</param>
/// <param name="Snapshot">The snapshot in force when the upload is ready.</param>
public sealed record VerificationOutcome(
    VerificationResult Result,
    UploadRejectionCode? Code = null,
    UploadRejectionReason? Reason = null,
    SnapshotRecord? Snapshot = null)
{
    /// <summary>The upload does not exist.</summary>
    public static VerificationOutcome NotFound { get; } = new(VerificationResult.NotFound);

    /// <summary>The upload is not waiting to be verified.</summary>
    public static VerificationOutcome NotVerifying { get; } = new(VerificationResult.NotVerifying);

    /// <summary>The upload is ready with <paramref name="snapshot"/>.</summary>
    /// <param name="snapshot">The snapshot in force.</param>
    public static VerificationOutcome Ready(SnapshotRecord snapshot) =>
        new(VerificationResult.Ready, Snapshot: snapshot);

    /// <summary>The upload ended rejected with <paramref name="code"/> and <paramref name="reason"/>.</summary>
    /// <param name="code">Why it was rejected.</param>
    /// <param name="reason">The fixed reason recorded beside the code.</param>
    public static VerificationOutcome Rejected(UploadRejectionCode code, UploadRejectionReason reason) =>
        new(VerificationResult.Rejected, code, reason);
}
