using System.IO.Compression;
using System.IO.Pipelines;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ReportService.Storage;
using ReportService.Uploads;
using ReportService.Workers.Orchestration.Telemetry;

namespace ReportService.Workers.Orchestration.Uploads;

/// <summary>
/// Verifies one upload and fixes its snapshot, or ends it rejected. The stored
/// object is read exactly once, forward only: the same pull of the storage port
/// counts and hashes the stored bytes, keeps the object's last eight bytes and
/// hands the blocks to the decompressor, which counts, hashes and checksums the
/// content. Only when both sides have finished is the object's own trailer
/// compared with what was computed, and only when that comparison passes is the
/// consumer's declared hash looked at — so an object whose content is intact but
/// whose trailer is missing is refused before anything can be fixed. Rejections
/// are results, never failures; only a service-side cause is thrown, so the
/// caller retries it under a new attempt.
/// </summary>
public sealed class UploadVerifier
{
    /// <summary>How far the stored side may run ahead of the decompressor, in bytes.</summary>
    public const int PipePauseThresholdBytes = 1024 * 1024;

    /// <summary>Where the stored side resumes once the decompressor caught up, in bytes.</summary>
    public const int PipeResumeThresholdBytes = 512 * 1024;

    /// <summary>The largest block copied out of the stored object at once, in bytes.</summary>
    public const int CopyBufferBytes = 64 * 1024;

    private readonly IUploadStore _uploads;
    private readonly IObjectStore _objects;
    private readonly VerificationOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<UploadVerifier> _logger;
    private readonly OrchestrationMetrics _metrics;

    /// <summary>Creates the verifier over the stores, the limits and the process's clock.</summary>
    /// <param name="uploads">The upload records, including the outcome commit.</param>
    /// <param name="objects">The object storage port.</param>
    /// <param name="options">The stored and decompressed size limits, as the process validated them.</param>
    /// <param name="clock">The clock the snapshot's creation time and the progress interval are measured with.</param>
    /// <param name="logger">The log the outcome is written to, with identifiers and codes only.</param>
    /// <param name="metrics">The process's instruments.</param>
    public UploadVerifier(
        IUploadStore uploads,
        IObjectStore objects,
        IOptions<VerificationOptions> options,
        TimeProvider clock,
        ILogger<UploadVerifier> logger,
        OrchestrationMetrics metrics)
    {
        ArgumentNullException.ThrowIfNull(uploads);
        ArgumentNullException.ThrowIfNull(objects);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(metrics);
        _uploads = uploads;
        _objects = objects;
        _options = options.Value;
        _clock = clock;
        _logger = logger;
        _metrics = metrics;
    }

    /// <summary>
    /// Verifies <paramref name="uploadId"/>, writing the attempt's bytes to
    /// <paramref name="attemptKey"/> and committing the outcome.
    /// </summary>
    /// <param name="uploadId">The upload to verify.</param>
    /// <param name="attemptKey">The key this attempt writes its copy of the bytes under.</param>
    /// <param name="progress">Reports the stored bytes read, at most once a second; may be null.</param>
    /// <param name="cancellationToken">The step's cancellation token.</param>
    public async Task<VerificationOutcome> VerifyAsync(
        string uploadId,
        ObjectKey attemptKey,
        Action<long>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(uploadId);
        if (attemptKey.IsEmpty)
        {
            throw new ArgumentException("the attempt key is empty.", nameof(attemptKey));
        }

        var started = _clock.GetTimestamp();
        var record = await _uploads.LoadForVerificationAsync(uploadId, cancellationToken).ConfigureAwait(false);
        if (record is null)
        {
            return VerificationOutcome.NotFound;
        }

        switch (record.Status)
        {
            case UploadStatus.Ready:
                return record.Snapshot is { } committed
                    ? VerificationOutcome.Ready(committed)
                    : VerificationOutcome.NotVerifying;
            case UploadStatus.Rejected:
                return VerificationOutcome.Rejected(
                    record.RejectionCode ?? UploadRejectionCode.DataInvalid,
                    record.RejectionReason ?? UploadRejectionReason.Malformed);
            case not UploadStatus.Verifying:
                return VerificationOutcome.NotVerifying;
            default:
                break;
        }

        // A body that ended before its end is refused without reading anything.
        if (record.ContentOutcome == UploadContentOutcome.Interrupted)
        {
            return await FinishRejectedAsync(
                record, attemptKey, attemptWritten: false,
                UploadRejectionCode.DataInvalid, UploadRejectionReason.Truncated,
                started, cancellationToken).ConfigureAwait(false);
        }

        // An encoding the service does not decode was recorded at transfer and is refused here.
        if (record.ContentEncoding == UploadContentEncoding.Other)
        {
            return await FinishRejectedAsync(
                record, attemptKey, attemptWritten: false,
                UploadRejectionCode.UnsupportedCapability, UploadRejectionReason.Encoding,
                started, cancellationToken).ConfigureAwait(false);
        }

        if (record.StagingSize is not { } storedSize || record.StagingSha256 is not { } storedSha256)
        {
            // A confirmed upload always records what arrived; a row without it is not usable.
            return await FinishRejectedAsync(
                record, attemptKey, attemptWritten: false,
                UploadRejectionCode.DataInvalid, UploadRejectionReason.Modified,
                started, cancellationToken).ConfigureAwait(false);
        }

        if (storedSize > _options.MaxStoredBytes)
        {
            return await FinishRejectedAsync(
                record, attemptKey, attemptWritten: false,
                UploadRejectionCode.QuotaExceeded, UploadRejectionReason.StoredBytes,
                started, cancellationToken).ConfigureAwait(false);
        }

        var opened = await _objects.OpenReadAsync(record.StagingKey, storedSize, storedSha256, cancellationToken)
            .ConfigureAwait(false);
        if (opened.Outcome == StorageOutcome.NotFound)
        {
            return await FinishRejectedAsync(
                record, attemptKey, attemptWritten: false,
                UploadRejectionCode.DataInvalid, UploadRejectionReason.Modified,
                started, cancellationToken).ConfigureAwait(false);
        }

        if (!opened.Succeeded)
        {
            throw new StorageException(opened.Outcome, opened.Error!);
        }

        var read = opened.Value!;
        var compressed = record.ContentEncoding == UploadContentEncoding.Gzip;
        var pipe = compressed
            ? new Pipe(new PipeOptions(
                pauseWriterThreshold: PipePauseThresholdBytes,
                resumeWriterThreshold: PipeResumeThresholdBytes))
            : null;
        var sink = compressed ? new HashingCountingSink(_options.MaxUncompressedBytes) : null;
        var tee = new SnapshotTeeStream(read.Content, pipe?.Writer, progress, _clock);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var decompression = compressed
            ? Task.Run(() => DecompressAsync(pipe!.Reader, sink!, linked.Token), linked.Token)
            : null;

        StorageResult<ObjectWrite>? written = null;
        Exception? writeFault = null;
        try
        {
            written = await _objects.WriteAsync(attemptKey, tee, linked.Token).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            writeFault = exception;
        }

        Exception? decompressionFault = null;
        if (decompression is not null)
        {
            if (writeFault is not null && !decompression.IsCompleted)
            {
                await linked.CancelAsync().ConfigureAwait(false);
            }

            try
            {
                await decompression.ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                decompressionFault = exception;
            }
        }

        // Both sides of the read have finished; the object is closed before anything is deleted.
        await read.DisposeAsync().ConfigureAwait(false);

        // The side that recorded a failure decides first, whatever the write returned.
        var attemptWritten = written is { Succeeded: true };
        if (tee.RecordedFailure is StorageIntegrityException)
        {
            return await FinishRejectedAsync(
                record, attemptKey, attemptWritten,
                UploadRejectionCode.DataInvalid, UploadRejectionReason.Modified,
                started, cancellationToken).ConfigureAwait(false);
        }

        if (sink?.RecordedFailure is UncompressedQuotaExceededException)
        {
            return await FinishRejectedAsync(
                record, attemptKey, attemptWritten,
                UploadRejectionCode.QuotaExceeded, UploadRejectionReason.InputBytes,
                started, cancellationToken).ConfigureAwait(false);
        }

        if (sink?.RecordedFailure is InvalidDataException)
        {
            return await FinishRejectedAsync(
                record, attemptKey, attemptWritten,
                UploadRejectionCode.DataInvalid, UploadRejectionReason.Malformed,
                started, cancellationToken).ConfigureAwait(false);
        }

        if (writeFault is OperationCanceledException)
        {
            throw writeFault;
        }

        if (writeFault is StorageException storageFault)
        {
            throw storageFault;
        }

        if (writeFault is not null)
        {
            throw writeFault;
        }

        if (decompressionFault is not null)
        {
            throw decompressionFault;
        }

        if (written is not { Succeeded: true })
        {
            throw new StorageException(written?.Outcome ?? StorageOutcome.Transient, written?.Error ?? new StorageError(
                StorageErrorCodes.Unexpected, "the stored copy could not be written."));
        }

        // The stored copy must be exactly the bytes that were read.
        var stored = written!.Value!;
        if (stored.Size != tee.StoredLength
            || !string.Equals(stored.Sha256, tee.StoredSha256, StringComparison.Ordinal))
        {
            throw new StorageIntegrityException(tee.StoredLength, stored.Size, tee.StoredSha256, stored.Sha256);
        }

        if (compressed)
        {
            sink!.Complete();
        }

        var contentSha256 = compressed ? sink!.Sha256 : tee.StoredSha256;
        var contentSize = compressed ? sink!.Count : tee.StoredLength;

        // The object's own trailer has to attest everything that was read, before any declaration is looked at.
        if (compressed && !GzipMemberAttestation.IsAttested(tee.Tail, tee.StoredLength, sink!.Crc32Value, contentSize))
        {
            return await FinishRejectedAsync(
                record, attemptKey, attemptWritten,
                UploadRejectionCode.DataInvalid, UploadRejectionReason.Truncated,
                started, cancellationToken).ConfigureAwait(false);
        }

        if (record.DeclaredSha256 is { } declared
            && !string.Equals(declared, contentSha256, StringComparison.OrdinalIgnoreCase))
        {
            return await FinishRejectedAsync(
                record, attemptKey, attemptWritten,
                UploadRejectionCode.DataInvalid, UploadRejectionReason.Sha256Mismatch,
                started, cancellationToken).ConfigureAwait(false);
        }

        var snapshot = new SnapshotRecord(
            record.UploadId,
            record.ConsumerId,
            attemptKey,
            record.ContentEncoding ?? UploadContentEncoding.None,
            record.Profile,
            contentSha256,
            contentSize,
            tee.StoredSha256,
            tee.StoredLength,
            UploadWireValues.TruncateToMillisecond(_clock.GetUtcNow()));

        var commit = await _uploads.CommitReadyAsync(snapshot, cancellationToken).ConfigureAwait(false);
        switch (commit)
        {
            case SnapshotCommitResult.Committed:
                await TryDeleteAsync(record.StagingKey, cancellationToken).ConfigureAwait(false);
                LogReady(snapshot);
                RecordOutcome(OrchestrationMetrics.ReadyOutcome, OrchestrationMetrics.NoCode, started);
                return VerificationOutcome.Ready(snapshot);

            case SnapshotCommitResult.AlreadyCommitted:
                // An earlier attempt fixed the snapshot; this attempt's object is not referenced.
                await TryDeleteAsync(attemptKey, cancellationToken).ConfigureAwait(false);
                await TryDeleteAsync(record.StagingKey, cancellationToken).ConfigureAwait(false);
                var current = await _uploads.LoadForVerificationAsync(uploadId, cancellationToken).ConfigureAwait(false);
                if (current?.Snapshot is { } winner)
                {
                    LogReady(winner);
                    RecordOutcome(OrchestrationMetrics.ReadyOutcome, OrchestrationMetrics.NoCode, started);
                    return VerificationOutcome.Ready(winner);
                }

                return VerificationOutcome.NotVerifying;

            default:
                await TryDeleteAsync(attemptKey, cancellationToken).ConfigureAwait(false);
                return VerificationOutcome.NotVerifying;
        }
    }

    private static async Task DecompressAsync(PipeReader reader, HashingCountingSink sink, CancellationToken cancellationToken)
    {
        var source = reader.AsStream();
        try
        {
            using (var gzip = new GZipStream(source, CompressionMode.Decompress, leaveOpen: true))
            {
                var buffer = new byte[CopyBufferBytes];
                int read;
                while ((read = await gzip.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    sink.Append(buffer.AsSpan(0, read));
                }
            }
        }
        catch (Exception exception)
        {
            sink.Record(exception);
            await reader.CompleteAsync(exception).ConfigureAwait(false);
            throw;
        }
        finally
        {
            // Whatever the decompressor left behind is consumed, so the stored side never waits
            // for a reader that already ended.
            try
            {
                var leftover = new byte[CopyBufferBytes];
                while (await source.ReadAsync(leftover, cancellationToken).ConfigureAwait(false) > 0)
                {
                }
            }
            catch (Exception)
            {
                // The stored side already failed and closed the pipe; there is nothing left to drain.
            }
        }
    }

    private async Task<VerificationOutcome> FinishRejectedAsync(
        UploadRecord record,
        ObjectKey attemptKey,
        bool attemptWritten,
        UploadRejectionCode code,
        UploadRejectionReason reason,
        long started,
        CancellationToken cancellationToken)
    {
        if (attemptWritten)
        {
            await TryDeleteAsync(attemptKey, cancellationToken).ConfigureAwait(false);
        }

        var ended = await _uploads.CommitRejectedAsync(record.UploadId, code, reason, cancellationToken)
            .ConfigureAwait(false);
        if (ended)
        {
            await TryDeleteAsync(record.StagingKey, cancellationToken).ConfigureAwait(false);
        }

        _logger.LogInformation(
            "Upload {UploadId} verified: rejected with {ErrorCode} ({Reason})",
            record.UploadId,
            code.ToString(),
            reason.ToString());
        RecordOutcome(OrchestrationMetrics.RejectedOutcome, UploadWireValues.ToStoredText(code), started);
        return VerificationOutcome.Rejected(code, reason);
    }

    private void LogReady(SnapshotRecord snapshot) =>
        _logger.LogInformation(
            "Upload {UploadId} verified: ready, {SizeBytes} bytes, compression {Compression}",
            snapshot.UploadId,
            snapshot.SizeUncompressed,
            snapshot.Compression.ToString());

    private void RecordOutcome(string outcome, string code, long started)
    {
        _metrics.UploadOutcome(outcome, code, OrchestrationMetrics.VerificationStage);
        _metrics.RecordVerificationDuration(outcome, _clock.GetElapsedTime(started));
    }

    private async Task TryDeleteAsync(ObjectKey key, CancellationToken cancellationToken)
    {
        try
        {
            var deleted = await _objects.DeleteAsync(key, cancellationToken).ConfigureAwait(false);
            if (!deleted.Succeeded)
            {
                _logger.LogWarning("A stored object could not be deleted ({StorageOutcome})", deleted.Outcome);
            }
        }
        catch (StorageException exception)
        {
            _logger.LogWarning("A stored object could not be deleted ({StorageOutcome})", exception.Outcome);
        }
    }
}
