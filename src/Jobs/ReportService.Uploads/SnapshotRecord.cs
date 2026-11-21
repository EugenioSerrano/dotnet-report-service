using ReportService.Storage;

namespace ReportService.Uploads;

/// <summary>
/// The immutable snapshot of a verified upload: the object holding the bytes
/// that were read, the hash and size of the content they decompress to, and the
/// hash and size of the stored bytes themselves. The pair is kept apart on
/// purpose — the content's identity is never confused with the stored object's.
/// One snapshot exists per upload, and the record is what makes it exist: the
/// object alone is not a snapshot until this record commits.
/// </summary>
/// <param name="UploadId">The identifier of the upload the snapshot belongs to.</param>
/// <param name="ConsumerId">The consumer that owns the upload.</param>
/// <param name="ObjectKey">The key the stored bytes were written under; never a URL.</param>
/// <param name="Compression">The encoding of the stored bytes: none or gzip.</param>
/// <param name="Profile">The data profile the upload declared.</param>
/// <param name="Sha256Uncompressed">The SHA-256 of the content, lower-case hexadecimal.</param>
/// <param name="SizeUncompressed">The size of the content, in bytes.</param>
/// <param name="Sha256Stored">The SHA-256 of the stored bytes, lower-case hexadecimal.</param>
/// <param name="SizeStored">The size of the stored bytes, in bytes.</param>
/// <param name="CreatedAt">When the snapshot was fixed, in UTC.</param>
public sealed record SnapshotRecord(
    string UploadId,
    string ConsumerId,
    ObjectKey ObjectKey,
    UploadContentEncoding Compression,
    DataProfile Profile,
    string Sha256Uncompressed,
    long SizeUncompressed,
    string Sha256Stored,
    long SizeStored,
    DateTimeOffset CreatedAt);
