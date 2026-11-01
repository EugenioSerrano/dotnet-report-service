using ReportService.Uploads;

namespace ReportService.Api.Uploads;

/// <summary>
/// What a status read answers: the session's identity, its state, the two things
/// it declared and the two ends of its lifetime, each as the wire text. The
/// staging key and the owner stay inside the service.
/// </summary>
/// <param name="UploadId">The identifier of the session.</param>
/// <param name="Status">The state of the session, in its wire text.</param>
/// <param name="Mode">The declared mode, in its wire text.</param>
/// <param name="Profile">The declared profile, in its wire text.</param>
/// <param name="CreatedAt">When the session was opened.</param>
/// <param name="ExpiresAt">When the session stops accepting data.</param>
public sealed record UploadStatusResponse(
    string UploadId,
    string Status,
    string Mode,
    string Profile,
    string CreatedAt,
    string ExpiresAt)
{
    /// <summary>Projects a stored session onto the response.</summary>
    /// <param name="record">The session to project.</param>
    public static UploadStatusResponse From(UploadRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return new UploadStatusResponse(
            record.UploadId,
            UploadWireValues.ToWire(record.Status),
            UploadWireValues.ToWire(record.Mode),
            UploadWireValues.ToWire(record.Profile),
            UploadWireValues.FormatTimestamp(record.CreatedAt),
            UploadWireValues.FormatTimestamp(record.ExpiresAt));
    }
}
