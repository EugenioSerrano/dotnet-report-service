using Microsoft.Extensions.Logging;
using ReportService.Data;
using ReportService.Jobs;
using ReportService.Storage;
using ReportService.Uploads;
using Temporalio.Activities;
using Temporalio.Exceptions;

namespace ReportService.Workers.Orchestration.Jobs;

/// <summary>What the data step of a job is asked to do: read one job's snapshot.</summary>
/// <param name="ReportJobId">The identifier of the job.</param>
public sealed record ValidateAndPartitionInput(string ReportJobId);

/// <summary>
/// The data step of one job: it reads the job's snapshot once through the verifying
/// reader, admits its records into one block, writes the block and its index page
/// under keys of this attempt, and records the partition. It first looks for the
/// partition it may already have committed, so an attempt that arrives again reads
/// what exists instead of writing a second one. A snapshot the profile refuses ends
/// the step with a failure that is never retried; the objects of a losing attempt
/// are left where they are.
/// </summary>
public sealed class ValidateAndPartitionActivity(
    IJobStore jobs,
    IUploadStore uploads,
    IObjectStore objects,
    TimeProvider clock,
    ILogger<ValidateAndPartitionActivity> logger)
{
    /// <summary>The name the step is registered and scheduled under.</summary>
    public const string Name = "ValidateAndPartition";

    /// <summary>The code a failure on the service's side ends a job with.</summary>
    private const string ExecutionFailed = "EXECUTION_FAILED";

    /// <summary>Reads one job's snapshot and records its partition.</summary>
    /// <param name="input">The job to partition.</param>
    [Activity(Name)]
    public async Task<PartitionResult> RunAsync(ValidateAndPartitionInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var cancellationToken = ActivityExecutionContext.Current.CancellationToken;

        var existing = await jobs.FindPartitionAsync(input.ReportJobId, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            logger.LogInformation("Job {ReportJobId} already has its partition", input.ReportJobId);
            return await ResultOfAsync(existing, cancellationToken).ConfigureAwait(false);
        }

        var state = await jobs.FindForWorkflowAsync(input.ReportJobId, cancellationToken).ConfigureAwait(false)
            ?? throw Refuse("INPUT_NOT_FOUND", "the job is not recorded");
        var upload = await uploads.LoadForVerificationAsync(state.Job.UploadId, cancellationToken).ConfigureAwait(false)
            ?? throw Refuse("INPUT_NOT_FOUND", "the upload of the job is not recorded");
        var snapshot = upload.Snapshot
            ?? throw Refuse("INPUT_NOT_FOUND", "the upload of the job has no snapshot");

        var read = await objects
            .OpenReadAsync(snapshot.ObjectKey, snapshot.SizeStored, snapshot.Sha256Stored, cancellationToken)
            .ConfigureAwait(false);
        if (!read.Succeeded)
        {
            throw RefuseUnverifiable(read.Outcome, read.Error!, "the snapshot");
        }

        var attempt = ActivityExecutionContext.Current.Info;
        var attemptId = PdfOutputActivityOptions.AttemptId(
            attempt.WorkflowId ?? string.Empty,
            attempt.WorkflowRunId ?? string.Empty,
            attempt.ActivityId,
            attempt.Attempt);
        var jobId = input.ReportJobId;

        await using var verified = read.Value!;
        await using var content = snapshot.Compression == UploadContentEncoding.Gzip
            ? new System.IO.Compression.GZipStream(verified.Content, System.IO.Compression.CompressionMode.Decompress)
            : verified.Content;

        // The block is one bounded document — at most the block's own maximum — so it is held
        // while it is written, and never the snapshot or the record set. The beat runs beside
        // the pass, so a slow read inside it cannot silence the step.
        using var block = new MemoryStream();
        long recordsRead = 0;
        using var passEnds = new CancellationTokenSource();
        var beat = ActivityHeartbeat.WhileRunningAsync(
            PdfOutputActivityOptions.HeartbeatEvery,
            () => Interlocked.Read(ref recordsRead),
            count => ActivityExecutionContext.Current.Heartbeat(new PartitionHeartbeat(count)),
            passEnds.Token);

        PartitionSummary summary;
        try
        {
            summary = await SingleBlockPartitioner
                .PartitionAsync(
                    HeaderItemsReader.ReadAsync(content, cancellationToken),
                    block,
                    cancellationToken,
                    count => Interlocked.Exchange(ref recordsRead, count))
                .ConfigureAwait(false);
        }
        catch (DataContractViolation violation)
        {
            // The profile's own diagnostic: the rule, the position, and no value of the document.
            throw new ApplicationFailureException(violation.Message, errorType: violation.Code, nonRetryable: true);
        }
        catch (StorageIntegrityException)
        {
            // The bytes read no longer match the size and SHA-256 recorded for them.
            throw Refuse(ExecutionFailed, "the snapshot does not match its record");
        }
        finally
        {
            await passEnds.CancelAsync().ConfigureAwait(false);
            await beat.ConfigureAwait(false);
        }

        var blockKey = ObjectKey.FromSegments("block", jobId, "0", attemptId);
        block.Position = 0;
        var blockWrite = await objects.WriteAsync(blockKey, block, cancellationToken).ConfigureAwait(false);
        var written = Written(blockWrite.Value, "the block");

        var index = BlockIndexPage.Serialize(new BlockIndexPage(
            SingleBlockPartitioner.PartitionerVersion,
            [new BlockIndexEntry(
                0,
                blockKey.ToString(),
                written.Sha256,
                written.Size,
                summary.FirstRecordOrdinal,
                summary.RecordCount,
                summary.RecordBytes)]));
        var indexKey = ObjectKey.FromSegments("index", jobId, "0", attemptId);
        using var indexContent = new MemoryStream(index, writable: false);
        var indexWrite = await objects.WriteAsync(indexKey, indexContent, cancellationToken).ConfigureAwait(false);
        var writtenIndex = Written(indexWrite.Value, "the index page");

        var partition = new PartitionRecord(
            jobId,
            SingleBlockPartitioner.PartitionerVersion,
            summary.BlockCount,
            summary.RecordCount,
            summary.RecordBytes,
            indexKey.ToString(),
            writtenIndex.Sha256,
            writtenIndex.Size,
            clock.GetUtcNow());

        var committed = await jobs.CommitPartitionAsync(partition, cancellationToken).ConfigureAwait(false);
        if (committed.Outcome == PartitionCommitOutcome.AlreadyCommitted)
        {
            // Another attempt committed first; this attempt's objects are its own orphans and
            // the answer is the partition that exists.
            return await ResultOfAsync(committed.Existing, cancellationToken).ConfigureAwait(false);
        }

        logger.LogInformation(
            "Job {ReportJobId} partitioned into {BlockCount} block(s) holding {RecordCount} record(s)",
            jobId,
            partition.BlockCount,
            partition.RecordCount);

        return new PartitionResult(
            partition.PartitionerVersion,
            partition.BlockCount,
            partition.RecordCount,
            partition.RecordBytes,
            1,
            blockKey.ToString(),
            written.Size,
            written.Sha256,
            partition.IndexObjectKey,
            partition.IndexSizeBytes,
            partition.IndexSha256);
    }

    private static ObjectWrite Written(ObjectWrite? write, string what) =>
        write ?? throw new InvalidOperationException($"the storage provider reported success without {what}");

    private async Task<PartitionResult> ResultOfAsync(PartitionRecord partition, CancellationToken cancellationToken)
    {
        var indexRead = await objects
            .OpenReadAsync(
                ObjectKey.Create(partition.IndexObjectKey),
                partition.IndexSizeBytes,
                partition.IndexSha256,
                cancellationToken)
            .ConfigureAwait(false);
        if (!indexRead.Succeeded)
        {
            throw RefuseUnverifiable(indexRead.Outcome, indexRead.Error!, "the index page");
        }

        byte[] json;
        try
        {
            await using var page = indexRead.Value!;
            using var buffer = new MemoryStream();
            await page.Content.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
            json = buffer.ToArray();
        }
        catch (StorageIntegrityException)
        {
            // The page read back no longer matches the size and SHA-256 recorded for it.
            throw Refuse(ExecutionFailed, "the index page does not match its record");
        }

        var first = BlockIndexPage.Parse(json).Blocks[0];
        return new PartitionResult(
            partition.PartitionerVersion,
            partition.BlockCount,
            partition.RecordCount,
            partition.RecordBytes,
            first.FirstRecordOrdinal,
            first.Key,
            first.SizeBytes,
            first.Sha256,
            partition.IndexObjectKey,
            partition.IndexSizeBytes,
            partition.IndexSha256);
    }

    // A refusal of the data is the code the profile names, and it is never retried: the
    // same snapshot read again reaches the same answer.
    private static ApplicationFailureException Refuse(string code, string reason) =>
        new($"{code}: {reason}", errorType: code, nonRetryable: true);

    // A read whose stored content does not match the size and SHA-256 recorded for it is a
    // refusal too, and never retried; it is a failure on the service's side rather than a
    // refusal of the data, so it ends the job with that code. Every other outcome keeps the
    // port's own exception, so a temporary fault is still retried.
    private static Exception RefuseUnverifiable(StorageOutcome outcome, StorageError error, string what) =>
        outcome == StorageOutcome.IntegrityFailure
            ? Refuse(ExecutionFailed, what + " does not match its record")
            : new StorageException(outcome, error);
}

/// <summary>The beat a running data step reports: how many records it has read so far.</summary>
/// <param name="RecordsRead">How many records the pass has admitted.</param>
public sealed record PartitionHeartbeat(long RecordsRead);
