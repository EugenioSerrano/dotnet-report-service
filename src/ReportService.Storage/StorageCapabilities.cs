namespace ReportService.Storage;

/// <summary>
/// What a provider can do beyond the port's common contract. A provider
/// declares its capabilities once and never changes them while it runs; the
/// application reads them before choosing a path: a direct upload only with
/// <see cref="SignedUpload"/>, a redirected download only with
/// <see cref="SignedDownload"/>, a read of a range only with
/// <see cref="RangeReads"/>.
/// </summary>
/// <param name="SignedUpload">Can grant the write of one staging key through a signed URL.</param>
/// <param name="SignedDownload">Can grant the read of one key through a signed URL.</param>
/// <param name="NativeCreateOnly">Refuses, by itself and atomically, a write to a key that already exists.</param>
/// <param name="RangeReads">Can read a range of an object (unverified by construction).</param>
public readonly record struct StorageCapabilities(bool SignedUpload, bool SignedDownload, bool NativeCreateOnly, bool RangeReads)
{
    /// <summary>No capability.</summary>
    public static StorageCapabilities None => default;

    /// <summary>Every capability.</summary>
    public static StorageCapabilities All => new(true, true, true, true);
}
