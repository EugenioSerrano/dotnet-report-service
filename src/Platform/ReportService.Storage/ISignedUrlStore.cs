namespace ReportService.Storage;

/// <summary>
/// Signed URLs, implemented only by a provider that declares
/// <see cref="StorageCapabilities.SignedUpload"/> or
/// <see cref="StorageCapabilities.SignedDownload"/>. A URL grants one verb on
/// one key until it expires; it is never logged, stored or used as an
/// object's identity.
/// </summary>
public interface ISignedUrlStore
{
    /// <summary>A URL that grants the write of <paramref name="stagingKey"/> for <paramref name="timeToLive"/>.</summary>
    Task<StorageResult<Uri>> CreateUploadUrlAsync(ObjectKey stagingKey, TimeSpan timeToLive, CancellationToken cancellationToken);

    /// <summary>
    /// A URL that grants the read of <paramref name="key"/> for
    /// <paramref name="timeToLive"/>, offering <paramref name="downloadFileName"/>
    /// as the file name.
    /// </summary>
    Task<StorageResult<Uri>> CreateDownloadUrlAsync(ObjectKey key, TimeSpan timeToLive, string downloadFileName, CancellationToken cancellationToken);
}
