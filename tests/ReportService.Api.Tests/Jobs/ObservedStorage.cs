using ReportService.Storage;

namespace ReportService.Api.Tests.Jobs;

/// <summary>
/// The storage the host was given, with its reads and its writes counted: a case can say
/// that a refusal opened nothing and that a foreign read wrote nothing, while the fixture
/// still stores and reads through the same port. Every member forwards to the provider the
/// host built, so the bytes, the size and the hash are the provider's own.
/// </summary>
internal sealed class ObservedStorage(IObjectStore inner) : IObjectStore
{
    private int _opens;
    private int _writes;

    /// <summary>How many objects were opened for a read since the case started counting.</summary>
    public int Opens => Volatile.Read(ref _opens);

    /// <summary>How many objects were written since the case started counting.</summary>
    public int Writes => Volatile.Read(ref _writes);

    /// <inheritdoc />
    public StorageCapabilities Capabilities => inner.Capabilities;

    /// <inheritdoc />
    public Task<StorageResult<ObjectWrite>> WriteAsync(ObjectKey key, Stream content, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _writes);
        return inner.WriteAsync(key, content, cancellationToken);
    }

    /// <inheritdoc />
    public Task<StorageResult<VerifiedRead>> OpenReadAsync(
        ObjectKey key,
        long expectedSize,
        string expectedSha256,
        CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _opens);
        return inner.OpenReadAsync(key, expectedSize, expectedSha256, cancellationToken);
    }

    /// <inheritdoc />
    public Task<StorageResult<ObjectInfo>> GetInfoAsync(ObjectKey key, CancellationToken cancellationToken) =>
        inner.GetInfoAsync(key, cancellationToken);

    /// <inheritdoc />
    public Task<StorageResult<bool>> DeleteAsync(ObjectKey key, CancellationToken cancellationToken) =>
        inner.DeleteAsync(key, cancellationToken);

    /// <inheritdoc />
    public IAsyncEnumerable<ObjectInfo> ListAsync(string classPrefix, CancellationToken cancellationToken) =>
        inner.ListAsync(classPrefix, cancellationToken);
}
