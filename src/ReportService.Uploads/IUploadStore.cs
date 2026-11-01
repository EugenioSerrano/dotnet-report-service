namespace ReportService.Uploads;

/// <summary>
/// The upload records of the service. Every method a consumer drives takes the
/// declared consumer as well as the identifier, so no caller can touch a session
/// without saying whose it is; an upload of another consumer is answered exactly
/// as a session that does not exist. Every write is one conditional statement or
/// one transaction, so a step that arrives twice, or too late, changes nothing.
/// </summary>
public interface IUploadStore
{
    /// <summary>Records a new session.</summary>
    /// <param name="record">The session to record.</param>
    /// <param name="cancellationToken">The request's cancellation token.</param>
    Task CreateAsync(UploadRecord record, CancellationToken cancellationToken);

    /// <summary>Finds a session by its identifier, among those of <paramref name="consumerId"/>.</summary>
    /// <param name="uploadId">The identifier of the session.</param>
    /// <param name="consumerId">The declared consumer asking.</param>
    /// <param name="cancellationToken">The request's cancellation token.</param>
    /// <returns>The session, or <see langword="null"/> when it does not exist for that consumer.</returns>
    Task<UploadRecord?> FindAsync(string uploadId, string consumerId, CancellationToken cancellationToken);

    /// <summary>Moves a session from created to receiving, recording the declared encoding.</summary>
    /// <param name="uploadId">The identifier of the session.</param>
    /// <param name="consumerId">The declared consumer sending the body.</param>
    /// <param name="encoding">How the body is declared encoded.</param>
    /// <param name="cancellationToken">The request's cancellation token.</param>
    /// <returns>True when this call moved the session; false when it was not created any more.</returns>
    Task<bool> TryStartTransferAsync(
        string uploadId,
        string consumerId,
        UploadContentEncoding encoding,
        CancellationToken cancellationToken);

    /// <summary>Records how a transfer ended, once, while the session is receiving.</summary>
    /// <param name="uploadId">The identifier of the session.</param>
    /// <param name="outcome">Whether the whole body arrived.</param>
    /// <param name="receivedBytes">The bytes received.</param>
    /// <param name="stagingSize">The size of the object written, when the bytes landed.</param>
    /// <param name="stagingSha256">The SHA-256 of the object written, when the bytes landed.</param>
    /// <param name="cancellationToken">The request's cancellation token.</param>
    /// <returns>True when this call recorded the outcome; false when one was already recorded.</returns>
    Task<bool> RecordTransferAsync(
        string uploadId,
        UploadContentOutcome outcome,
        long receivedBytes,
        long? stagingSize,
        string? stagingSha256,
        CancellationToken cancellationToken);

    /// <summary>Ends a session rejected before any confirmation, once, while it is receiving.</summary>
    /// <param name="uploadId">The identifier of the session.</param>
    /// <param name="code">Why it was rejected.</param>
    /// <param name="reason">The fixed reason recorded beside the code.</param>
    /// <param name="receivedBytes">The bytes received when the transfer stopped.</param>
    /// <param name="cancellationToken">The request's cancellation token.</param>
    /// <returns>True when this call ended the session; false when it had already ended.</returns>
    Task<bool> RejectTransferAsync(
        string uploadId,
        UploadRejectionCode code,
        UploadRejectionReason reason,
        long receivedBytes,
        CancellationToken cancellationToken);

    /// <summary>
    /// Moves a session to verifying and records the request that starts its
    /// verification, in one transaction. The request is recorded once per
    /// session, so a repeated confirmation records no second one.
    /// </summary>
    /// <param name="uploadId">The identifier of the session.</param>
    /// <param name="consumerId">The declared consumer confirming.</param>
    /// <param name="declaredSha256">The SHA-256 the consumer declared, lower-case hexadecimal, when it declared one.</param>
    /// <param name="completedAt">When the confirmation happened, in UTC.</param>
    /// <param name="cancellationToken">The request's cancellation token.</param>
    /// <returns>True when this call confirmed the session; false when it was not in a confirmable state.</returns>
    Task<bool> ConfirmAsync(
        string uploadId,
        string consumerId,
        string? declaredSha256,
        DateTimeOffset completedAt,
        CancellationToken cancellationToken);

    /// <summary>
    /// Loads a session by its identifier alone, with its snapshot when it has
    /// one. This read exists for the verification step and takes no caller text:
    /// the identifier comes from the durable request that started the work.
    /// </summary>
    /// <param name="uploadId">The identifier of the session.</param>
    /// <param name="cancellationToken">The step's cancellation token.</param>
    /// <returns>The session, or <see langword="null"/> when it does not exist.</returns>
    Task<UploadRecord?> LoadForVerificationAsync(string uploadId, CancellationToken cancellationToken);

    /// <summary>
    /// Records a snapshot and moves its session to ready, in one transaction.
    /// The snapshot is unique per session: an earlier attempt that committed
    /// first wins, and this call reports that instead of writing.
    /// </summary>
    /// <param name="snapshot">The snapshot to record.</param>
    /// <param name="cancellationToken">The step's cancellation token.</param>
    Task<SnapshotCommitResult> CommitReadyAsync(SnapshotRecord snapshot, CancellationToken cancellationToken);

    /// <summary>Ends a verifying session rejected.</summary>
    /// <param name="uploadId">The identifier of the session.</param>
    /// <param name="code">Why it was rejected.</param>
    /// <param name="reason">The fixed reason recorded beside the code.</param>
    /// <param name="cancellationToken">The step's cancellation token.</param>
    /// <returns>True when this call ended the session; false when it was no longer verifying.</returns>
    Task<bool> CommitRejectedAsync(
        string uploadId,
        UploadRejectionCode code,
        UploadRejectionReason reason,
        CancellationToken cancellationToken);
}
