using System.Security.Cryptography;

namespace ReportService.Storage;

/// <summary>
/// A write-only stream over a provider's sink that counts and hashes (SHA-256)
/// every byte as it is written. <see cref="Size"/> and <see cref="Sha256"/>
/// are available only once <see cref="CompleteAsync"/> has flushed the sink:
/// a write that failed, or a stream disposed without completing, has no
/// figures to report. Every provider writes through it, so every provider
/// returns the hash of what it actually wrote.
/// </summary>
public sealed class HashingWriteStream : Stream
{
    private readonly Stream _sink;
    private readonly bool _leaveOpen;
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private long _written;
    private string? _sha256;
    private bool _faulted;
    private bool _disposed;

    /// <summary>Wraps <paramref name="sink"/>; disposes it with this stream unless <paramref name="leaveOpen"/>.</summary>
    public HashingWriteStream(Stream sink, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(sink);
        if (!sink.CanWrite)
        {
            throw new ArgumentException("The sink must be writable.", nameof(sink));
        }

        _sink = sink;
        _leaveOpen = leaveOpen;
    }

    /// <summary>True once <see cref="CompleteAsync"/> has succeeded.</summary>
    public bool IsCompleted => _sha256 is not null;

    /// <summary>The number of bytes written; available once completed.</summary>
    public long Size => IsCompleted ? _written : throw NotCompleted();

    /// <summary>The SHA-256 of the bytes written, lower-case hexadecimal; available once completed.</summary>
    public string Sha256 => _sha256 ?? throw NotCompleted();

    /// <inheritdoc />
    public override bool CanRead => false;

    /// <inheritdoc />
    public override bool CanSeek => false;

    /// <inheritdoc />
    public override bool CanWrite => !_disposed && !IsCompleted;

    /// <inheritdoc />
    public override long Length => throw new NotSupportedException();

    /// <inheritdoc />
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    /// <summary>Flushes the sink and fixes the size and SHA-256 of what was written.</summary>
    public async Task CompleteAsync(CancellationToken cancellationToken)
    {
        EnsureWritable();
        try
        {
            await _sink.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            _faulted = true;
            throw;
        }

        _sha256 = Convert.ToHexStringLower(_hash.GetHashAndReset());
    }

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count)
    {
        ValidateBufferArguments(buffer, offset, count);
        Write(buffer.AsSpan(offset, count));
    }

    /// <inheritdoc />
    public override void Write(ReadOnlySpan<byte> buffer)
    {
        EnsureWritable();
        try
        {
            _sink.Write(buffer);
        }
        catch
        {
            _faulted = true;
            throw;
        }

        Count(buffer);
    }

    /// <inheritdoc />
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        ValidateBufferArguments(buffer, offset, count);
        return WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    /// <inheritdoc />
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        EnsureWritable();
        try
        {
            await _sink.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            _faulted = true;
            throw;
        }

        Count(buffer.Span);
    }

    /// <inheritdoc />
    public override void Flush() => _sink.Flush();

    /// <inheritdoc />
    public override Task FlushAsync(CancellationToken cancellationToken) => _sink.FlushAsync(cancellationToken);

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            _hash.Dispose();
            if (!_leaveOpen)
            {
                _sink.Dispose();
            }
        }

        base.Dispose(disposing);
    }

    /// <inheritdoc />
    public override async ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            _hash.Dispose();
            if (!_leaveOpen)
            {
                await _sink.DisposeAsync().ConfigureAwait(false);
            }
        }

        GC.SuppressFinalize(this);
    }

    private void Count(ReadOnlySpan<byte> written)
    {
        _hash.AppendData(written);
        _written += written.Length;
    }

    private void EnsureWritable()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_faulted)
        {
            throw new InvalidOperationException("A write to the sink failed; the stream cannot be completed.");
        }

        if (IsCompleted)
        {
            throw new InvalidOperationException("The stream is already completed.");
        }
    }

    private static InvalidOperationException NotCompleted() =>
        new("The size and SHA-256 are known only once the stream is completed.");
}
