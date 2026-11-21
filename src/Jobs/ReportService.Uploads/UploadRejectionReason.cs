namespace ReportService.Uploads;

/// <summary>
/// The reason recorded beside a rejection code: a fixed vocabulary, never a
/// value taken from the consumer's data. <see cref="Truncated"/> means the body
/// did not hold one complete unit of content; <see cref="Malformed"/> means the
/// content that is there is corrupt; <see cref="Modified"/> means what is stored
/// is no longer what arrived.
/// </summary>
public enum UploadRejectionReason
{
    /// <summary>The content does not hash to the SHA-256 the consumer declared.</summary>
    Sha256Mismatch = 0,

    /// <summary>The stored content is not whole.</summary>
    Truncated = 1,

    /// <summary>The stored content is corrupt.</summary>
    Malformed = 2,

    /// <summary>The stored content is no longer what was received.</summary>
    Modified = 3,

    /// <summary>The declared encoding cannot be decoded.</summary>
    Encoding = 4,

    /// <summary>The stored size limit was crossed.</summary>
    StoredBytes = 5,

    /// <summary>The decompressed size limit was crossed.</summary>
    InputBytes = 6,
}
