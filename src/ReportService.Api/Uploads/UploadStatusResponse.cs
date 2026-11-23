using ReportService.Uploads;

namespace ReportService.Api.Uploads;

/// <summary>
/// What a status read answers: the session's identity, its state, the two things
/// it declared, the two ends of its lifetime, the bytes that arrived, when it was
/// confirmed, and — once its verification has finished — either the content's
/// hash and size or why it was refused. Every member is always present and is
/// null while it does not apply. The hash and size shown are those of the
/// uncompressed content; the stored object's own pair stays in the records and is
/// never shown, so the two can never be confused. The staging key and the owner
/// stay inside the service.
/// </summary>
/// <param name="UploadId">The identifier of the session.</param>
/// <param name="Status">The state of the session, in its wire text.</param>
/// <param name="Mode">The declared mode, in its wire text.</param>
/// <param name="Profile">The declared profile, in its wire text.</param>
/// <param name="CreatedAt">When the session was opened.</param>
/// <param name="ExpiresAt">When the session stops accepting data.</param>
/// <param name="ReceivedBytes">The bytes the content operation received; null before a transfer ends.</param>
/// <param name="CompletedAt">When the session was confirmed; null before.</param>
/// <param name="Sha256">The hash of the content, lower-case hexadecimal; only when ready.</param>
/// <param name="SizeBytes">The size of the content; only when ready.</param>
/// <param name="RejectionCode">Why the upload ended rejected; only when rejected.</param>
/// <param name="RejectionReason">The fixed reason beside the code; only when rejected.</param>
public sealed record UploadStatusResponse(
    string UploadId,
    string Status,
    string Mode,
    string Profile,
    string CreatedAt,
    string ExpiresAt,
    long? ReceivedBytes,
    string? CompletedAt,
    string? Sha256,
    long? SizeBytes,
    string? RejectionCode,
    string? RejectionReason)
{
    /// <summary>Projects a stored session onto the response.</summary>
    /// <param name="record">The session to project.</param>
    public static UploadStatusResponse From(UploadRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var ready = record.Status == UploadStatus.Ready ? record.Snapshot : null;
        return new UploadStatusResponse(
            record.UploadId,
            UploadWireValues.ToWire(record.Status),
            UploadWireValues.ToWire(record.Mode),
            UploadWireValues.ToWire(record.Profile),
            UploadWireValues.FormatTimestamp(record.CreatedAt),
            UploadWireValues.FormatTimestamp(record.ExpiresAt),
            record.ReceivedBytes,
            record.CompletedAt is { } completed ? UploadWireValues.FormatTimestamp(completed) : null,
            ready?.Sha256Uncompressed,
            ready?.SizeUncompressed,
            record.RejectionCode is { } code ? UploadWireValues.ToStoredText(code) : null,
            record.RejectionReason is { } reason ? UploadWireValues.ToStoredText(reason) : null);
    }
}
