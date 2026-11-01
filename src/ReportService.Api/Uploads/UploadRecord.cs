using ReportService.Storage;

namespace ReportService.Api.Uploads;

/// <summary>
/// One upload session as the service records it: the identifier the consumer
/// receives, the consumer that owns it, what it declared, its state, the key its
/// bytes will be written under, and the two timestamps of its lifetime. The
/// value is immutable; a later state change replaces it.
/// </summary>
/// <param name="UploadId">The identifier of the session, unique across the service.</param>
/// <param name="ConsumerId">The declared consumer that owns the session.</param>
/// <param name="Mode">How the consumer's data is to arrive.</param>
/// <param name="Profile">The shape of data the consumer declared.</param>
/// <param name="Status">The state of the session.</param>
/// <param name="StagingKey">The key the session's bytes will be written under; no byte is written by this operation.</param>
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
    DateTimeOffset ExpiresAt);
