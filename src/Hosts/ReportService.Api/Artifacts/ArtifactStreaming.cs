using System.Buffers;
using ReportService.Api.Telemetry;
using ReportService.Storage;

namespace ReportService.Api.Artifacts;

/// <summary>
/// The verified copy behind a download: it passes the opened object through the
/// verifying reader to the response body with one chunk held back. Chunk <c>n</c>
/// is written only after chunk <c>n+1</c> has been read, and the last chunk only
/// after the read that ends the stream has returned — which is where the reader
/// compares the bytes with the size and the SHA-256 the row carries. A
/// verification failure, an object longer than its announced length or a source
/// that stops early therefore ends the transfer before the held chunk is
/// written, and the client receives fewer bytes than the response declared: an
/// incomplete transfer, never a complete corrupt file. Nothing here is buffered
/// beyond two rented chunks, and the content never travels as a byte array, a
/// string or a memory stream.
/// </summary>
public sealed class ArtifactStreaming(ArtifactDownloadMetrics metrics, ILogger<ArtifactStreaming> logger)
{
    /// <summary>The size of each rented buffer.</summary>
    public const int BufferLength = 64 * 1024;

    /// <summary>The stored object is shorter than the row says.</summary>
    public const string SizeMismatch = "size";

    /// <summary>The stored object has the row's length but another SHA-256.</summary>
    public const string HashMismatch = "hash";

    /// <summary>The stored object is longer than the row says.</summary>
    public const string LongerMismatch = "longer";

    /// <summary>Copies <paramref name="source"/> to the response, or aborts the transfer.</summary>
    /// <param name="http">The request whose response carries the content.</param>
    /// <param name="source">The verifying reader of the stored object.</param>
    /// <param name="expectedSize">The length the response declared.</param>
    /// <param name="artifactId">The artifact being downloaded, for the log records.</param>
    /// <param name="cancellationToken">The request's cancellation token.</param>
    /// <returns>Whether the whole content reached the client.</returns>
    public async Task<bool> CopyVerifiedAsync(
        HttpContext http,
        Stream source,
        long expectedSize,
        string artifactId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactId);

        var body = http.Response.Body;
        var held = ArrayPool<byte>.Shared.Rent(BufferLength);
        var next = ArrayPool<byte>.Shared.Rent(BufferLength);
        var heldLength = 0;
        var written = 0L;
        try
        {
            while (true)
            {
                int read;
                try
                {
                    read = await source
                        .ReadAsync(next.AsMemory(0, BufferLength), cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    logger.LogDebug("Download of artifact {ArtifactId} ended by the client", artifactId);
                    return false;
                }
                catch (StorageIntegrityException failure)
                {
                    Abort(http, artifactId, MismatchOf(failure));
                    return false;
                }
                catch (IOException)
                {
                    // The stored object could not be read to its announced length: the
                    // transfer is cut rather than completed short.
                    Abort(http, artifactId, SizeMismatch);
                    return false;
                }

                if (read == 0)
                {
                    // The reader compared the bytes with what the row declares and said nothing.
                    break;
                }

                if (written + heldLength + read > expectedSize)
                {
                    Abort(http, artifactId, LongerMismatch);
                    return false;
                }

                if (heldLength > 0 && !await WriteAsync(body, held, heldLength, artifactId, cancellationToken).ConfigureAwait(false))
                {
                    return false;
                }

                written += heldLength;
                (held, next) = (next, held);
                heldLength = read;
            }

            // Defensive: a source that ended without the reader complaining still has to have
            // delivered exactly the length the response announced.
            if (written + heldLength != expectedSize)
            {
                Abort(http, artifactId, SizeMismatch);
                return false;
            }

            if (heldLength > 0 && !await WriteAsync(body, held, heldLength, artifactId, cancellationToken).ConfigureAwait(false))
            {
                return false;
            }

            return await FlushAsync(body, artifactId, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(held);
            ArrayPool<byte>.Shared.Return(next);
        }
    }

    // The transfer is cut and counted: the client keeps its declared length, its received
    // bytes are fewer, and no byte of the held chunk is written.
    private void Abort(HttpContext http, string artifactId, string mismatch)
    {
        metrics.IntegrityFailure();
        logger.LogError(
            "Download of artifact {ArtifactId} aborted: the stored object failed verification ({Mismatch})",
            artifactId,
            mismatch);
        http.Abort();
    }

    // A write that fails means the client is gone: nothing is counted as an integrity
    // failure and nothing is logged above Debug, because the object was never at fault.
    private async Task<bool> WriteAsync(Stream body, byte[] buffer, int count, string artifactId, CancellationToken cancellationToken)
    {
        try
        {
            await body.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException)
        {
            logger.LogDebug("Download of artifact {ArtifactId} ended by the client", artifactId);
            return false;
        }
    }

    private async Task<bool> FlushAsync(Stream body, string artifactId, CancellationToken cancellationToken)
    {
        try
        {
            await body.FlushAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException)
        {
            logger.LogDebug("Download of artifact {ArtifactId} ended by the client", artifactId);
            return false;
        }
    }

    private static string MismatchOf(StorageIntegrityException failure) =>
        string.Equals(failure.Error.Code, StorageErrorCodes.HashMismatch, StringComparison.Ordinal)
            ? HashMismatch
            : SizeMismatch;
}
