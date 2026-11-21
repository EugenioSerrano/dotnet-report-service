using ReportService.Uploads;

namespace ReportService.Jobs;

/// <summary>
/// A job and its outputs, together with what the upload it reports on now says.
/// This is the one read a step gets when it has nothing but the job's identifier:
/// the identifier comes from the durable request that started the work, so the
/// read takes no caller text and is not scoped to a consumer.
/// </summary>
/// <param name="Job">The job.</param>
/// <param name="Outputs">The outputs the job asked for, in output identifier order.</param>
/// <param name="UploadStatus">The state of the upload the job reports on.</param>
/// <param name="UploadExpiresAt">When the upload's session ends, in UTC.</param>
/// <param name="VerificationDeadline">Until when a verifying upload may still fix its snapshot, in UTC; null when it never was verifying.</param>
/// <param name="RejectionCode">Why the upload ended rejected; null unless it is rejected.</param>
/// <param name="RejectionReason">The fixed reason recorded beside <see cref="RejectionCode"/>.</param>
public sealed record JobStateRecord(
    ReportJobRecord Job,
    IReadOnlyList<ReportOutputRecord> Outputs,
    UploadStatus UploadStatus,
    DateTimeOffset UploadExpiresAt,
    DateTimeOffset? VerificationDeadline,
    UploadRejectionCode? RejectionCode,
    UploadRejectionReason? RejectionReason);
