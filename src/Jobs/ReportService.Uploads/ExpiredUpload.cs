using ReportService.Storage;

namespace ReportService.Uploads;

/// <summary>
/// One upload a sweep expired: which upload, the state it was in when it was
/// expired, and the key of the staging object that has to be deleted with it.
/// The state before is what tells the two expiries apart: an unconfirmed upload
/// ran out of session, a confirmed one outlasted its verification deadline.
/// </summary>
/// <param name="UploadId">The identifier of the upload.</param>
/// <param name="PreviousStatus">The state the upload was in before it was expired.</param>
/// <param name="StagingKey">The key the upload's bytes were written under.</param>
public sealed record ExpiredUpload(string UploadId, UploadStatus PreviousStatus, ObjectKey StagingKey)
{
    /// <summary>The cause of this expiry: the session or the verification deadline.</summary>
    public string Cause => UploadExpiry.CauseOf(PreviousStatus);
}

/// <summary>
/// What expiring one upload left behind: the upload as it now stands, and whether
/// this call is the one that expired it. False means the upload was not due, or
/// another writer moved it first; the record is then whatever that writer left, so
/// the caller answers by the state it is actually in.
/// </summary>
/// <param name="Upload">The upload as it stands afterwards; null when it does not exist.</param>
/// <param name="Expired">Whether this call expired it.</param>
public sealed record UploadExpiryOutcome(UploadRecord? Upload, bool Expired);
