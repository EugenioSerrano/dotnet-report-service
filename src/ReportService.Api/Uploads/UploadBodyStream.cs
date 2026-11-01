using System.Net.Sockets;
using Microsoft.AspNetCore.Http;

namespace ReportService.Api.Uploads;

/// <summary>How a body's arrival ended, as the reader recorded it.</summary>
public enum UploadStreamFailure
{
    /// <summary>No failure was recorded: the body is still arriving or ended normally.</summary>
    None = 0,

    /// <summary>The transfer crossed the size limit of the operation.</summary>
    QuotaExceeded = 1,

    /// <summary>The transfer ended before the body did.</summary>
    Interrupted = 2,
}

/// <summary>
/// The consumer's body as the storage port reads it: a forward-only, read-only
/// view that counts the bytes it hands on, refuses the one block that would carry
/// the transfer past the size limit, and translates the transport's own failures
/// into one condition of the service. Every failure is <b>recorded before it is
/// raised</b>, so the caller can decide from the record even when the port it
/// writes through reports something else — the outcome of a transfer never
/// depends on how the port reports a failing source.
/// </summary>
public sealed class UploadBodyStream : Stream
{
    private readonly Stream _inner;
    private readonly long _maxBytes;
    private readonly long? _declaredLength;

    /// <summary>Creates the reader over <paramref name="inner"/>.</summary>
    /// <param name="inner">The request's body.</param>
    /// <param name="maxBytes">The most bytes the operation accepts.</param>
    /// <param name="declaredLength">The length the caller declared, when it declared one.</param>
    public UploadBodyStream(Stream inner, long maxBytes, long? declaredLength)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);
        _inner = inner;
        _maxBytes = maxBytes;
        _declaredLength = declaredLength;
    }

    /// <summary>The bytes handed on so far.</summary>
    public long BytesRead { get; private set; }

    /// <summary>The failure the reader recorded, if any; recorded before the exception is raised.</summary>
    public UploadStreamFailure RecordedFailure { get; private set; }

    /// <inheritdoc />
    public override bool CanRead => true;

    /// <inheritdoc />
    public override bool CanSeek => false;

    /// <inheritdoc />
    public override bool CanWrite => false;

    /// <inheritdoc />
    public override long Length => throw new NotSupportedException("the request body has no known length.");

    /// <inheritdoc />
    public override long Position
    {
        get => BytesRead;
        set => throw new NotSupportedException("the request body is forward only.");
    }

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

    /// <inheritdoc />
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    /// <inheritdoc />
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        int read;
        try
        {
            read = await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsTransportFailure(exception))
        {
            RecordedFailure = UploadStreamFailure.Interrupted;
            throw new UploadTransferInterruptedException(exception);
        }

        if (read == 0)
        {
            // A body shorter than the length it declared ended before its end.
            if (_declaredLength is { } declared && BytesRead < declared)
            {
                RecordedFailure = UploadStreamFailure.Interrupted;
                throw new UploadTransferInterruptedException();
            }

            return 0;
        }

        BytesRead += read;
        if (BytesRead > _maxBytes)
        {
            RecordedFailure = UploadStreamFailure.QuotaExceeded;
            throw new UploadQuotaExceededException();
        }

        return read;
    }

    /// <inheritdoc />
    public override void Flush()
    {
    }

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException("the request body is forward only.");

    /// <inheritdoc />
    public override void SetLength(long value) => throw new NotSupportedException("the request body is read only.");

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException("the request body is read only.");

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        // The request's own body belongs to the server; this view closes nothing.
        base.Dispose(disposing);
    }

    // What a body read raises when the connection beneath it failed or ended: the
    // transport's own types, never a failure the service raised itself.
    private static bool IsTransportFailure(Exception exception) =>
        exception is IOException
            or SocketException
            or BadHttpRequestException
            or OperationCanceledException
            or ObjectDisposedException;
}
