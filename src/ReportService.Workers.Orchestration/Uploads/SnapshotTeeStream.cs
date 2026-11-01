using System.IO.Pipelines;
using System.Security.Cryptography;

namespace ReportService.Workers.Orchestration.Uploads;

/// <summary>
/// The single forward read of a stored object, teed. Every block the storage
/// port pulls is (a) hashed and counted as stored bytes, (b) kept in a fixed
/// eight-byte buffer that ends holding the object's last eight bytes, and, for a
/// compressed object, (c) handed to the reader that decompresses it — so the
/// stored bytes are seen exactly once, nothing is re-read, no seek is used and
/// no copy of the content is held. The pipe's thresholds bound how far ahead of
/// the decompressor the write may run. A failure is recorded before it is
/// raised, so the caller can tell which side of the read failed.
/// </summary>
internal sealed class SnapshotTeeStream : Stream
{
    /// <summary>The bytes kept from the end of the object.</summary>
    public const int TailBytes = 8;

    private readonly Stream _inner;
    private readonly PipeWriter? _compressed;
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private readonly byte[] _tail = new byte[TailBytes];
    private readonly Action<long>? _progress;
    private readonly TimeProvider _clock;
    private long _count;
    private long _lastReportedAt;
    private string? _storedSha256;
    private bool _ended;
    private long _tailFilled;
    private Exception? _failure;

    /// <summary>Tees <paramref name="inner"/>, which this stream disposes.</summary>
    /// <param name="inner">The verified, forward-only content of the stored object.</param>
    /// <param name="compressed">Where the same blocks are handed for decompression; null when the object is not compressed.</param>
    /// <param name="progress">Reports the stored bytes read, at most once a second.</param>
    /// <param name="clock">The clock the once-a-second rule is measured with.</param>
    public SnapshotTeeStream(Stream inner, PipeWriter? compressed, Action<long>? progress, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(clock);
        if (!inner.CanRead)
        {
            throw new ArgumentException("the inner stream must be readable.", nameof(inner));
        }

        _inner = inner;
        _compressed = compressed;
        _progress = progress;
        _clock = clock;
        _lastReportedAt = clock.GetTimestamp();
    }

    /// <summary>The stored bytes read so far.</summary>
    public long StoredLength => _count;

    /// <summary>The object's last eight bytes; meaningful once the read ended.</summary>
    public ReadOnlySpan<byte> Tail => _tail;

    /// <summary>The SHA-256 of the stored bytes, available once the read ended.</summary>
    public string StoredSha256 => _storedSha256
        ?? throw new InvalidOperationException("the stored object has not been read to its end yet");

    /// <summary>The first failure this side of the read recorded, when it recorded one.</summary>
    public Exception? RecordedFailure => _failure;

    /// <inheritdoc />
    public override bool CanRead => true;

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
        if (_ended)
        {
            return 0;
        }

        int read;
        try
        {
            read = _inner.Read(buffer);
        }
        catch (Exception exception)
        {
            Fail(exception);
            throw;
        }

        if (read == 0)
        {
            End();
            return 0;
        }

        Account(buffer[..read]);
        if (_compressed is not null)
        {
            var span = _compressed.GetSpan(read);
            buffer[..read].CopyTo(span);
            _compressed.Advance(read);
            _compressed.FlushAsync().AsTask().GetAwaiter().GetResult();
        }

        return read;
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
        if (_ended)
        {
            return 0;
        }

        int read;
        try
        {
            read = await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Fail(exception);
            throw;
        }

        if (read == 0)
        {
            await EndAsync().ConfigureAwait(false);
            return 0;
        }

        Account(buffer.Span[..read]);
        if (_compressed is not null)
        {
            await _compressed.WriteAsync(buffer[..read], cancellationToken).ConfigureAwait(false);
            await _compressed.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        return read;
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
        if (disposing)
        {
            End();
            _hash.Dispose();
            _inner.Dispose();
        }

        base.Dispose(disposing);
    }

    /// <inheritdoc />
    public override async ValueTask DisposeAsync()
    {
        await EndAsync().ConfigureAwait(false);
        _hash.Dispose();
        await _inner.DisposeAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    private void Account(ReadOnlySpan<byte> block)
    {
        _hash.AppendData(block);
        _count += block.Length;
        Report();

        if (block.Length >= TailBytes)
        {
            block[^TailBytes..].CopyTo(_tail);
            _tailFilled = TailBytes;
            return;
        }

        var keep = (int)Math.Min(_tailFilled, TailBytes - block.Length);
        if (keep > 0)
        {
            _tail.AsSpan((int)_tailFilled - keep, keep).CopyTo(_tail);
            _tailFilled = keep;
        }
        else
        {
            _tailFilled = 0;
        }

        block.CopyTo(_tail.AsSpan((int)_tailFilled));
        _tailFilled += block.Length;
    }

    private void Report()
    {
        if (_progress is null)
        {
            return;
        }

        var now = _clock.GetTimestamp();
        if (_clock.GetElapsedTime(_lastReportedAt, now) < TimeSpan.FromSeconds(1))
        {
            return;
        }

        _lastReportedAt = now;
        _progress(_count);
    }

    private void End()
    {
        if (_ended)
        {
            return;
        }

        _ended = true;
        _storedSha256 = Convert.ToHexStringLower(_hash.GetHashAndReset());
        _compressed?.Complete();
    }

    private async ValueTask EndAsync()
    {
        if (_ended)
        {
            return;
        }

        _ended = true;
        _storedSha256 = Convert.ToHexStringLower(_hash.GetHashAndReset());
        if (_compressed is not null)
        {
            await _compressed.CompleteAsync().ConfigureAwait(false);
        }
    }

    private void Fail(Exception exception)
    {
        _failure ??= exception;
        if (!_ended)
        {
            _ended = true;
            _compressed?.Complete(exception);
        }
    }
}
