namespace ReportService.Storage;

/// <summary>
/// The object storage port: the only way product code reaches stored objects.
/// It knows keys, never consumers, jobs or HTTP. Every operation returns an
/// outcome instead of a provider exception; keys, paths, URLs and credentials
/// never appear in an error. No content travels as a byte array, a string or
/// a memory stream: content is always a stream.
/// </summary>
public interface IObjectStore
{
    /// <summary>What this provider can do beyond the common contract; never changes while it runs.</summary>
    StorageCapabilities Capabilities { get; }

    /// <summary>
    /// Writes <paramref name="content"/> once at <paramref name="key"/>,
    /// streaming it with a bounded buffer, and returns the size and SHA-256 of
    /// the bytes actually written. An existing key is never overwritten: the
    /// outcome is <see cref="StorageOutcome.AlreadyExists"/> and the existing
    /// object is unchanged. An exception thrown by <paramref name="content"/>
    /// itself is the caller's and is passed on, after the partial write is
    /// removed.
    /// </summary>
    Task<StorageResult<ObjectWrite>> WriteAsync(ObjectKey key, Stream content, CancellationToken cancellationToken);

    /// <summary>
    /// Opens the object at <paramref name="key"/> for a read verified against
    /// <paramref name="expectedSize"/> and <paramref name="expectedSha256"/>
    /// (lower-case hexadecimal): consuming the content to its end raises a
    /// <see cref="StorageIntegrityException"/> when either differs.
    /// </summary>
    Task<StorageResult<VerifiedRead>> OpenReadAsync(ObjectKey key, long expectedSize, string expectedSha256, CancellationToken cancellationToken);

    /// <summary>The size and last-modified time of the object at <paramref name="key"/>.</summary>
    Task<StorageResult<ObjectInfo>> GetInfoAsync(ObjectKey key, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes the object at <paramref name="key"/>. Idempotent: a missing
    /// object is a success. The value says whether an object was removed.
    /// </summary>
    Task<StorageResult<bool>> DeleteAsync(ObjectKey key, CancellationToken cancellationToken);

    /// <summary>
    /// Lists the objects under <paramref name="classPrefix"/> (one or more key
    /// segments, no wildcard) in ordinal key order, a page at a time; a whole
    /// listing is never held in memory. A failure while listing raises a
    /// <see cref="StorageException"/>.
    /// </summary>
    IAsyncEnumerable<ObjectInfo> ListAsync(string classPrefix, CancellationToken cancellationToken);
}
