namespace StreamingDataSpike.Parsing;

/// <summary>
/// A read-only wrapper that counts the uncompressed bytes read and stops
/// when the input limit is exceeded.
/// </summary>
internal sealed class CountingStream(Stream inner, long limit) : Stream
{
    /// <summary>Uncompressed bytes read so far.</summary>
    public long BytesRead { get; private set; }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => BytesRead;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => Count(inner.Read(buffer, offset, count));

    public override int Read(Span<byte> buffer) => Count(inner.Read(buffer));

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        Count(await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false));

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            inner.Dispose();
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await inner.DisposeAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }

    private int Count(int read)
    {
        BytesRead += read;
        if (limit > 0 && BytesRead > limit)
        {
            throw new InputLimitExceededException(BytesRead);
        }

        return read;
    }
}

/// <summary>Raised by <see cref="CountingStream"/>; the reader turns it into an envelope error.</summary>
internal sealed class InputLimitExceededException(long bytesRead) : Exception("The input limit was exceeded.")
{
    /// <summary>Uncompressed bytes read when the limit was exceeded.</summary>
    public long BytesRead { get; } = bytesRead;
}
