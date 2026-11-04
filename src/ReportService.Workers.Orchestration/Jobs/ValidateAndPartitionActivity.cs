using Microsoft.Extensions.Logging;
using ReportService.Data;
using ReportService.DataContract;
using ReportService.Jobs;
using ReportService.Storage;
using ReportService.TemplatePackages.Packages;
using ReportService.TemplatePackages.Records;
using ReportService.Uploads;
using Temporalio.Activities;
using Temporalio.Exceptions;

namespace ReportService.Workers.Orchestration.Jobs;

/// <summary>What the data step of a job is asked to do: read one job's snapshot.</summary>
/// <param name="ReportJobId">The identifier of the job.</param>
public sealed record ValidateAndPartitionInput(string ReportJobId);

/// <summary>
/// The data step of one job: it sources the data contract of the template the job is
/// pinned to, reads the job's snapshot once through the verifying reader, checks the
/// header and every record against that contract while it reads, admits the records into
/// one block, writes the block and its index page under keys of this attempt, and records
/// the partition. It first looks for the partition it may already have committed, so an
/// attempt that arrives again reads what exists instead of writing a second one. A
/// snapshot the profile refuses, or a value the template's schema refuses, ends the step
/// with a failure that is never retried; a template whose pin or schema cannot be used
/// ends it with a failure of its own, also never retried; the objects of a losing attempt
/// are left where they are.
/// </summary>
public sealed class ValidateAndPartitionActivity(
    IJobStore jobs,
    IUploadStore uploads,
    IObjectStore objects,
    ITemplateVersionStore versions,
    PreparedDataContractCache contracts,
    TimeProvider clock,
    ILogger<ValidateAndPartitionActivity> logger,
    string? scratchRoot = null)
{
    /// <summary>The name the step is registered and scheduled under.</summary>
    public const string Name = "ValidateAndPartition";

    /// <summary>The code a failure on the service's side ends a job with.</summary>
    private const string ExecutionFailed = "EXECUTION_FAILED";

    /// <summary>The code the template's own contract ends a job with.</summary>
    private const string TemplateInvalid = "TEMPLATE_INVALID";

    /// <summary>The code a job whose records cannot be read ends with.</summary>
    private const string InputNotFound = "INPUT_NOT_FOUND";

    /// <summary>The reason word a value the template's schema refuses carries.</summary>
    private const string SchemaViolation = "schema-violation";

    /// <summary>The file of the taken package the contract is read from.</summary>
    private const string SchemaFile = "schema.json";

    /// <summary>The span tag of the code a job ended with.</summary>
    private const string ErrorCodeTag = "error.code";

    /// <summary>The span tag of the reason word the pass stopped at.</summary>
    private const string ViolationReasonTag = "data.violation.reason";

    /// <summary>The span tag of the ordinal of the value the pass stopped at.</summary>
    private const string ViolationOrdinalTag = "data.violation.ordinal";

    /// <summary>The directory a taken copy is extracted under, when the caller names none.</summary>
    public static readonly string DefaultScratchRoot =
        Path.Combine(Path.GetTempPath(), "report-service-data-contract");

    /// <summary>The directory a taken copy of the version being checked is extracted under.</summary>
    public string ScratchRoot { get; } = scratchRoot ?? DefaultScratchRoot;

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

        // The template's data contract is sourced before the snapshot is read: the pin names
        // the schema the data is checked against, and a pin that cannot be sourced leaves
        // nothing to check it with.
        using var lease = await LeaseTheContractAsync(input.ReportJobId, cancellationToken).ConfigureAwait(false);
        var contract = lease.Compilation.Contract
            ?? throw SchemaRefused(input.ReportJobId, lease.Compilation.Profile.Rule, ordinal: null, keyword: null);

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

        // The progress the consumer polls is written by a task of the pass's own: it awaits
        // each store write, reports the count only when it changed, and a write that fails
        // never fails the step. Keeping it beside the beat is also what keeps a slow write
        // from delaying a beat.
        var progress = WriteProgressAsync(jobId, () => Interlocked.Read(ref recordsRead), passEnds.Token);

        PartitionSummary summary;
        try
        {
            summary = await SingleBlockPartitioner
                .PartitionAsync(
                    CheckedAsync(contract, HeaderItemsReader.ReadAsync(content, cancellationToken), input.ReportJobId, cancellationToken),
                    block,
                    cancellationToken,
                    count => Interlocked.Exchange(ref recordsRead, count))
                .ConfigureAwait(false);
        }
        catch (DataContractViolation violation)
        {
            // The records the pass admitted before it stopped are the last honest count the
            // consumer can be given; the write is best effort, as every progress write is.
            await RecordProgressAsync(input.ReportJobId, Interlocked.Read(ref recordsRead), cancellationToken).ConfigureAwait(false);

            // The profile's own diagnostic: the rule, the position, and no value of the document.
            // The span carries the code, the reason and the ordinal, and the log line the same
            // with the offset — never a value, a key or a path of the storage.
            System.Diagnostics.Activity.Current?.SetTag(ErrorCodeTag, violation.Code);
            System.Diagnostics.Activity.Current?.SetTag(ViolationReasonTag, violation.Reason ?? violation.Limit);
            System.Diagnostics.Activity.Current?.SetTag(ViolationOrdinalTag, violation.Ordinal);
            logger.LogInformation(
                "Job {ReportJobId} snapshot refused: code={Code} reason={Reason} ordinal={Ordinal} offset={Offset} path={Path} keyword={Keyword}",
                input.ReportJobId,
                violation.Code,
                violation.Reason ?? violation.Limit,
                violation.Ordinal,
                violation.Offset,
                violation.Path,
                violation.Keyword);
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
            await progress.ConfigureAwait(false);
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

    // The progress writer of the pass: it ticks on the beat's own interval, awaits each
    // store write, and reports the count only when it changed since the last write. A
    // write that fails is logged with its type and retried on the next tick; progress
    // never fails the step.
    private async Task WriteProgressAsync(string reportJobId, Func<long> progress, CancellationToken cancellationToken)
    {
        long lastWritten = -1;
        using var timer = new PeriodicTimer(PdfOutputActivityOptions.HeartbeatEvery);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                var count = progress();
                if (count == lastWritten)
                {
                    continue;
                }

                if (await RecordProgressAsync(reportJobId, count, cancellationToken).ConfigureAwait(false))
                {
                    lastWritten = count;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The pass has ended: there is nothing left to write.
        }
    }

    // One progress write, best effort: false when it did not reach the store, which is
    // logged with the exception's type only and never raised to the step.
    private async Task<bool> RecordProgressAsync(string reportJobId, long recordsRead, CancellationToken cancellationToken)
    {
        try
        {
            await jobs.RecordRecordsReadAsync(reportJobId, recordsRead, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                "Progress of job {ReportJobId} not recorded ({ExceptionType})",
                reportJobId,
                exception.GetType().Name);
            return false;
        }
    }

    // The job's own reference, the taken version it names and the contract of that version,
    // leased from the worker's cache. The version is read off the reference and the version
    // store is reached only when one is there: a reference by name carries none, and a store
    // refuses a null version by throwing rather than answering nothing.
    private async Task<PreparedDataContractLease> LeaseTheContractAsync(
        string reportJobId,
        CancellationToken cancellationToken)
    {
        var reference = await jobs.FindTemplateReferenceAsync(reportJobId, cancellationToken).ConfigureAwait(false)
            ?? throw Refuse(InputNotFound, "the job is not recorded");

        // The version the job reads is the one it was pinned to: an exact reference is pinned
        // to the version it named, and a reference by name is pinned by the step that resolved
        // it. Only a reference the step has not resolved carries no pin, and an exact one of
        // those still names its version.
        var version = reference.Pin?.ResolvedVersion ?? reference.RequestedVersion;
        if (version is null)
        {
            // Nothing fixed this job's version: the step that resolves a reference by name has
            // not run for it, and this step never invents one. It says so, once, and the job ends.
            throw PinNoVersion(reportJobId);
        }

        var record = await versions.FindAsync(reference.TemplateId, version, cancellationToken).ConfigureAwait(false);
        if (record is null)
        {
            throw PinNotTaken(reportJobId);
        }

        var pin = TemplatePin.From(record, clock.GetUtcNow());
        var schema = await ReadSchemaAsync(record, pin, reportJobId, cancellationToken).ConfigureAwait(false);

        // A contract prepared by this lease is one the worker did not hold: the one debug
        // line of a compilation names the package's hash and how long it took, and nothing
        // of any document.
        var compilationsBefore = contracts.CompilationCount;
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var lease = contracts.Lease(pin.PackageSha256, pin.SchemaRef, schema);
        if (contracts.CompilationCount > compilationsBefore)
        {
            logger.LogDebug(
                "Job {ReportJobId}: the data contract of package {PackageSha256} was compiled in {ElapsedMilliseconds} ms",
                reportJobId,
                pin.PackageSha256,
                System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }

        return lease;
    }

    // The schema of the pinned version, read from the taken copy at the path the version's
    // record names: the copy is opened with the record's own size and SHA-256, so the bytes
    // the contract is compiled from are the bytes the take fixed.
    private async Task<ReadOnlyMemory<byte>> ReadSchemaAsync(
        TemplateVersionRecord record,
        TemplatePin pin,
        string reportJobId,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(ScratchRoot);
        TakenTemplatePackage package;
        try
        {
            package = await new TakenTemplatePackageReader(objects)
                .OpenAsync(record, ScratchRoot, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TemplatePackageUnavailableException unavailable)
        {
            // The store could not deliver the copy. A fault that may not happen again keeps
            // the port's own exception, so the engine's retry policy decides exactly as it
            // does for a transient read of the snapshot; any other answer is final — the
            // object is not there, the store refuses for good, or the bytes do not verify —
            // and the template cannot be used.
            if (unavailable.Outcome == StorageOutcome.Transient)
            {
                throw;
            }

            throw TakenCopyUnreadable(reportJobId);
        }
        catch (TemplatePackageCorruptException)
        {
            throw TakenCopyCorrupt(reportJobId);
        }

        await using (package.ConfigureAwait(false))
        {
            if (!package.Files.Contains(pin.SchemaRef, StringComparer.Ordinal))
            {
                throw SchemaAbsent(reportJobId);
            }

            var path = Path.Combine(package.Root, pin.SchemaRef.Replace('/', Path.DirectorySeparatorChar));
            return await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        }
    }

    // The two roots of the contract are evaluated in the same pass that reads the document,
    // each value as soon as the reader yields it and before the partitioner appends it: the
    // header at the header root, every record at the record root. The first value the
    // contract refuses stops the pass.
    private async IAsyncEnumerable<RawRecord> CheckedAsync(
        PreparedDataContract contract,
        IAsyncEnumerable<RawRecord> values,
        string reportJobId,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var value in values.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            ContractCheck check;
            try
            {
                check = value.Ordinal == 0 ? contract.CheckHeader(value.Json) : contract.CheckRecord(value.Json);
            }
            catch (ContractEvaluationFailed failure)
            {
                throw SchemaCannotBeDecided(reportJobId, failure.Kind, value.Ordinal, failure.Keyword);
            }

            if (!check.IsValid)
            {
                // The schema's own diagnostic: the reason, the value's ordinal and position,
                // the field's path relative to the value and the rule — never the value.
                throw new DataContractViolation(
                    DataContractViolation.DataInvalid,
                    SchemaViolation,
                    limit: null,
                    value.Ordinal,
                    value.Offset,
                    check.Path,
                    check.Keyword);
            }

            yield return value;
        }
    }

    // The pin cannot be sourced: the reference names no version, or it names one no record
    // holds. The message is exactly the code and the rule, because the pin path reads no
    // schema file and has no keyword to name.
    private ApplicationFailureException PinNoVersion(string reportJobId)
    {
        logger.LogWarning("Job {ReportJobId}: its template version was never resolved", reportJobId);
        return RefusedTemplate("pin");
    }

    private ApplicationFailureException PinNotTaken(string reportJobId)
    {
        logger.LogWarning("Job {ReportJobId}: its template version was never taken", reportJobId);
        return RefusedTemplate("pin");
    }

    private ApplicationFailureException TakenCopyUnreadable(string reportJobId)
    {
        logger.LogWarning("Job {ReportJobId}: the taken copy of its template version could not be read", reportJobId);
        return RefusedTemplate("package");
    }

    private ApplicationFailureException TakenCopyCorrupt(string reportJobId)
    {
        logger.LogWarning("Job {ReportJobId}: the taken copy of its template version does not read back intact", reportJobId);
        return RefusedTemplate("package");
    }

    private ApplicationFailureException SchemaAbsent(string reportJobId)
    {
        logger.LogWarning("Job {ReportJobId}: the taken copy of its template version carries no schema", reportJobId);
        return SchemaRefused(reportJobId, "absent", ordinal: null, keyword: null);
    }

    // A value the contract could not decide at all: the rule that stopped it, the ordinal of
    // the value and the keyword it reached, never a value.
    private ApplicationFailureException SchemaCannotBeDecided(
        string reportJobId,
        string kind,
        int ordinal,
        string? keyword) => SchemaRefused(reportJobId, kind, ordinal, keyword);

    // The pin's own refusal: the code and the rule, and nothing else.
    private ApplicationFailureException RefusedTemplate(string rule)
    {
        System.Diagnostics.Activity.Current?.SetTag(ErrorCodeTag, TemplateInvalid);
        logger.LogWarning("Template data cannot be used for a job: {Rule}", rule);
        return new ApplicationFailureException($"{TemplateInvalid} rule={rule}", errorType: TemplateInvalid, nonRetryable: true);
    }

    // The schema cannot be used: it is absent from the taken copy, the copy cannot be read,
    // the profile refuses it, it does not compile, or a value cannot be decided against it.
    private ApplicationFailureException SchemaRefused(
        string reportJobId,
        string? rule,
        int? ordinal,
        string? keyword)
    {
        System.Diagnostics.Activity.Current?.SetTag(ErrorCodeTag, TemplateInvalid);
        logger.LogWarning(
            "Job {ReportJobId}: {File} cannot be used (rule={Rule}, ordinal={Ordinal}, keyword={Keyword})",
            reportJobId,
            SchemaFile,
            rule ?? "schema",
            ordinal,
            keyword);

        var message = $"{TemplateInvalid} file={SchemaFile} rule={rule ?? "schema"}";
        if (ordinal is { } position)
        {
            message += " ordinal=" + position.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        if (keyword is not null)
        {
            message += " keyword=" + keyword;
        }

        return new ApplicationFailureException(message, errorType: TemplateInvalid, nonRetryable: true);
    }

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
