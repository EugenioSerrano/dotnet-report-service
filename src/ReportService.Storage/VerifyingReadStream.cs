using System.Security.Cryptography;

namespace ReportService.Storage;

/// <summary>
/// A read-only, forward-only stream that counts and hashes (SHA-256) the bytes
/// as the caller consumes them. When the inner stream ends, it compares what
/// was read with the expected size and SHA-256 and throws a
/// <see cref="StorageIntegrityException"/> when they differ — a shorter,
/// longer or changed content — and again on every later read. Every provider
/// reads through it, so every provider gets the same check.
/// </summary>
public sealed class VerifyingReadStream : Stream
{
    private readonly Stream _inner;
    private readonly long _expectedSize;
    private readonly string _expectedSha256;
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private long _read;
    private StorageIntegrityException? _failure;
    private bool _verified;
    private bool _disposed;

    /// <summary>Wraps <paramref name="inner"/>, which this stream disposes.</summary>
    public VerifyingReadStream(Stream inner, long expectedSize, string expectedSha256)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentOutOfRangeException.ThrowIfNegative(expectedSize);
        if (!Sha256Text.IsValid(expectedSha256))
        {
            throw new ArgumentException("The expected SHA-256 must be 64 lower-case hexadecimal characters.", nameof(expectedSha256));
        }

        if (!inner.CanRead)
        {
            throw new ArgumentException("The inner stream must be readable.", nameof(inner));
        }

        _inner = inner;
        _expectedSize = expectedSize;
        _expectedSha256 = expectedSha256;
    }

    /// <summary>True once the whole content was read and matched the expected size and SHA-256.</summary>
    public bool IsVerified => _verified;

    /// <inheritdoc />
    public override bool CanRead => !_disposed;

    /// <inheritdoc />
    public override bool CanSeek => false;

    /// <inheritdoc />
    public override bool CanWrite => false;

    /// <inheritdoc />
    public override long Length => throw new NotSupportedException();

    /// <inheritdoc />
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count)
    {
        ValidateBufferArguments(buffer, offset, count);
        return Read(buffer.AsSpan(offset, count));
    }

    /// <inheritdoc />
    public override int Read(Span<byte> buffer)
    {
        EnsureReadable();
        if (_verified)
        {
            return 0;
        }

        var read = _inner.Read(buffer);
        return Account(buffer[..read], endOfStream: read == 0 && buffer.Length > 0);
    }

    /// <inheritdoc />
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        ValidateBufferArguments(buffer, offset, count);
        return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    /// <inheritdoc />
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        EnsureReadable();
        if (_verified)
        {
            return 0;
        }

        var read = await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        return Account(buffer.Span[..read], endOfStream: read == 0 && buffer.Length > 0);
    }

    /// <inheritdoc />
    public override void Flush()
    {
    }

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            _hash.Dispose();
            _inner.Dispose();
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
            await _inner.DisposeAsync().ConfigureAwait(false);
        }

        GC.SuppressFinalize(this);
    }

    private int Account(ReadOnlySpan<byte> read, bool endOfStream)
    {
        if (read.Length > 0)
        {
            _hash.AppendData(read);
            _read += read.Length;
            return read.Length;
        }

        if (endOfStream)
        {
            var actual = Convert.ToHexStringLower(_hash.GetHashAndReset());
            if (_read != _expectedSize || !string.Equals(actual, _expectedSha256, StringComparison.Ordinal))
            {
                _failure = new StorageIntegrityException(_expectedSize, _read, _expectedSha256, actual);
                throw _failure;
            }

            _verified = true;
        }

        return 0;
    }

    private void EnsureReadable()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_failure is not null)
        {
            throw _failure;
        }
    }
}
