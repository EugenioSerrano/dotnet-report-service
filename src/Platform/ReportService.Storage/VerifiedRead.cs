namespace ReportService.Storage;

/// <summary>
/// An opened object whose content is verified as it is consumed:
/// <see cref="Content"/> is forward-only, and at its end it raises a
/// <see cref="StorageIntegrityException"/> when the bytes read do not have
/// <see cref="Size"/> and <see cref="Sha256"/>. Dispose it to release the
/// object.
/// </summary>
public sealed class VerifiedRead : IDisposable, IAsyncDisposable
{
    /// <summary>Wraps <paramref name="content"/> in a verifying reader.</summary>
    public VerifiedRead(Stream content, long expectedSize, string expectedSha256)
    {
        Content = new VerifyingReadStream(content, expectedSize, expectedSha256);
        Size = expectedSize;
        Sha256 = expectedSha256;
    }

    /// <summary>The verified, forward-only content.</summary>
    public Stream Content { get; }

    /// <summary>The expected size, as given by the caller.</summary>
    public long Size { get; }

    /// <summary>The expected SHA-256, as given by the caller.</summary>
    public string Sha256 { get; }

    /// <inheritdoc />
    public void Dispose() => Content.Dispose();

    /// <inheritdoc />
    public ValueTask DisposeAsync() => Content.DisposeAsync();
}
