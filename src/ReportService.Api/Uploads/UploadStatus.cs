namespace ReportService.Api.Uploads;

/// <summary>
/// The state of an upload session. This operation creates an upload in
/// <see cref="Created"/> and reads it back; the later states belong to
/// streaming, verification and expiry.
/// </summary>
public enum UploadStatus
{
    /// <summary>The session is open and waiting for data.</summary>
    Created,

    /// <summary>Content is being received.</summary>
    Uploading,

    /// <summary>The content was confirmed and is being verified.</summary>
    Verifying,

    /// <summary>Verification finished and the snapshot is fixed.</summary>
    Ready,

    /// <summary>The content was refused and no snapshot exists.</summary>
    Rejected,

    /// <summary>The session passed its expiry time.</summary>
    Expired,
}
