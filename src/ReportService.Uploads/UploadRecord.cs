using ReportService.Storage;

namespace ReportService.Uploads;

/// <summary>
/// One upload session as the service records it: the identifier the consumer
/// receives, the consumer that owns it, what it declared, its state, the key its
/// bytes are written under, the two timestamps of its lifetime, what arrived and
/// how its body ended, and the outcome when it has one. The value is immutable;
/// a later state change replaces it. Every member added after the session was
/// first recorded is null until the step that sets it runs.
/// </summary>
/// <param name="UploadId">The identifier of the session, unique across the service.</param>
/// <param name="ConsumerId">The declared consumer that owns the session.</param>
/// <param name="Mode">How the consumer's data is to arrive.</param>
/// <param name="Profile">The shape of data the consumer declared.</param>
/// <param name="Status">The state of the session.</param>
/// <param name="StagingKey">The key the session's bytes are written under.</param>
/// <param name="CreatedAt">When the session was opened, in UTC.</param>
/// <param name="ExpiresAt">When the session stops accepting data, in UTC.</param>
public sealed record UploadRecord(
    string UploadId,
    string ConsumerId,
    UploadMode Mode,
    DataProfile Profile,
    UploadStatus Status,
    ObjectKey StagingKey,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt)
{
    /// <summary>How the body was declared encoded; null until a transfer starts.</summary>
    public UploadContentEncoding? ContentEncoding { get; init; }

    /// <summary>How the body's arrival ended; null while it is still arriving or never started.</summary>
    public UploadContentOutcome? ContentOutcome { get; init; }

    /// <summary>The bytes received by the content operation; null before a transfer ends.</summary>
    public long? ReceivedBytes { get; init; }

    /// <summary>The size of the object the bytes were written to; null when they did not land.</summary>
    public long? StagingSize { get; init; }

    /// <summary>The SHA-256 of the object the bytes were written to, lower-case hexadecimal.</summary>
    public string? StagingSha256 { get; init; }

    /// <summary>The SHA-256 the consumer declared for the content, lower-case hexadecimal.</summary>
    public string? DeclaredSha256 { get; init; }

    /// <summary>When the consumer confirmed the upload, in UTC.</summary>
    public DateTimeOffset? CompletedAt { get; init; }

    /// <summary>
    /// Until when the confirmed upload's verification may still fix a snapshot, in
    /// UTC; counted from <see cref="CompletedAt"/> and null until a confirmation
    /// records it. It is what an upload that is verifying expires by, never
    /// <see cref="ExpiresAt"/>.
    /// </summary>
    public DateTimeOffset? VerificationDeadline { get; init; }

    /// <summary>Why the upload ended rejected; null unless it is rejected.</summary>
    public UploadRejectionCode? RejectionCode { get; init; }

    /// <summary>The fixed reason recorded beside <see cref="RejectionCode"/>.</summary>
    public UploadRejectionReason? RejectionReason { get; init; }

    /// <summary>The snapshot fixed for this upload; null until one is committed.</summary>
    public SnapshotRecord? Snapshot { get; init; }
}
