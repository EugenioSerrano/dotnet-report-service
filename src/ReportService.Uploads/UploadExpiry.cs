namespace ReportService.Uploads;

/// <summary>
/// Until when an upload may still be used. An upload that was never confirmed
/// lives until the expiry of its session; one that was confirmed lives until its
/// own verification deadline, which is counted from the confirmation and is
/// independent of the session's expiry. The rule is written once here, so the
/// API, the background sweep and anything that later accepts work on an upload
/// can never disagree on what "expired" means. It holds no clock: the instant is
/// always passed in, and it is the same instant the caller writes with.
/// </summary>
public static class UploadExpiry
{
    /// <summary>The cause of an expiry of an upload that was never confirmed.</summary>
    public const string SessionCause = "session";

    /// <summary>The cause of an expiry of an upload whose verification outlasted its deadline.</summary>
    public const string VerificationDeadlineCause = "verification-deadline";

    /// <summary>Whether <paramref name="upload"/> may no longer be used at <paramref name="now"/>.</summary>
    /// <param name="upload">The upload as it is recorded.</param>
    /// <param name="now">The instant to judge it at, in UTC.</param>
    /// <returns>
    /// True for an unconfirmed upload whose session expiry has been reached, and for
    /// a confirmed one whose verification deadline has been reached; false for an
    /// upload that has already ended, however it ended.
    /// </returns>
    public static bool IsOverdue(UploadRecord upload, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(upload);
        return upload.Status switch
        {
            UploadStatus.Created or UploadStatus.Uploading => now >= upload.ExpiresAt,
            UploadStatus.Verifying => now >= (upload.VerificationDeadline ?? DateTimeOffset.MaxValue),
            _ => false,
        };
    }

    /// <summary>The cause of the expiry of an upload last seen in <paramref name="status"/>.</summary>
    /// <param name="status">The state the upload was in before it expired.</param>
    public static string CauseOf(UploadStatus status) => status switch
    {
        UploadStatus.Created or UploadStatus.Uploading => SessionCause,
        UploadStatus.Verifying => VerificationDeadlineCause,
        _ => SessionCause,
    };
}
