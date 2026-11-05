using Microsoft.Extensions.Logging;
using ReportService.Data;
using ReportService.Jobs;
using ReportService.Pdf.Verification;
using ReportService.Rendering;
using ReportService.ServiceDefaults.Telemetry;
using ReportService.Storage;
using ReportService.TemplatePackages.Packages;
using ReportService.TemplatePackages.Records;
using ReportService.Templates;
using Temporalio.Activities;
using Temporalio.Exceptions;

namespace ReportService.Workers.Pdf.Rendering;

/// <summary>What the PDF worker is asked to print: one block of one output of one job.</summary>
/// <param name="ReportJobId">The identifier of the job.</param>
/// <param name="OutputId">The identifier of the PDF output of the job.</param>
/// <param name="BlockIndex">The block's position in the job's partition.</param>
public sealed record RenderPdfBlockInput(string ReportJobId, string OutputId, int BlockIndex);

/// <summary>What the PDF worker committed: the object one block was printed into.</summary>
/// <param name="PartKey">The key the printed block was written under.</param>
/// <param name="Sha256">The SHA-256 of the stored object, lower-case hexadecimal.</param>
/// <param name="SizeBytes">The size of the stored object.</param>
/// <param name="PageCount">How many pages the printed block holds.</param>
public sealed record RenderPdfBlockResult(string PartKey, string Sha256, long SizeBytes, int PageCount);

/// <summary>
/// Prints one block of an output from the template version its job pinned. The step
/// first looks for the commit it may already have written, so an attempt that arrives
/// again answers with what exists; it reads the job's pin, the taken copy and the
/// partition's index page and block through verifying reads; it composes, checks and
/// prints the block with the product's own pipeline; and only when every check passed
/// does it write the file under the output's attempt-scoped key and commit the part.
/// A block that fails its checks writes nothing and fails the step with
/// <c>PAGINATION_FAILED</c>, which is never retried: the same data printed again
/// reaches the same verdict.
/// </summary>
public sealed class RenderPdfBlockActivity(
    IJobStore jobs,
    ITemplateVersionStore versions,
    IObjectStore objects,
    TakenTemplatePackageReader packages,
    PreparedTemplateCache prepared,
    ITemplateEngineAdapter adapter,
    IBlockRenderer renderer,
    PdfRenderingMetrics metrics,
    TimeProvider clock,
    ILoggerFactory loggers,
    ILogger<RenderPdfBlockActivity> logger,
    string? scratchRoot = null)
{
    /// <summary>The name the step is registered and scheduled under.</summary>
    public const string Name = "RenderPdfBlock";

    /// <summary>The code a block that failed its checks ends with.</summary>
    private const string PaginationFailed = "PAGINATION_FAILED";

    /// <summary>The code a package this increment cannot print ends with.</summary>
    private const string UnsupportedCapability = "UNSUPPORTED_CAPABILITY";

    /// <summary>The code a state the step cannot use ends with.</summary>
    private const string ExecutionFailed = "EXECUTION_FAILED";

    /// <summary>The directory a taken copy is extracted under, when the caller names none.</summary>
    public static readonly string DefaultScratchRoot =
        Path.Combine(Path.GetTempPath(), "report-service-render");

    /// <summary>The directory a taken copy of the version being printed is extracted under.</summary>
    public string ScratchRoot { get; } = scratchRoot ?? DefaultScratchRoot;

    /// <summary>Prints one block.</summary>
    /// <param name="input">The job, the output and the block.</param>
    [Activity(Name)]
    public async Task<RenderPdfBlockResult> RunAsync(RenderPdfBlockInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var cancellationToken = ActivityExecutionContext.Current.CancellationToken;

        // The unit of work every log line and span of this step belongs to, and the two
        // facts a span needs beside the job's own identifier: the output it produces and
        // the block it prints. Neither is a metric dimension.
        using var context = JobContext.Begin(input.ReportJobId, loggers);
        System.Diagnostics.Activity.Current?.SetTag("output.id", input.OutputId);
        System.Diagnostics.Activity.Current?.SetTag("block.index", input.BlockIndex);

        var existing = await jobs.FindPartAsync(input.OutputId, input.BlockIndex, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            logger.LogInformation(
                "Block {BlockIndex} of output {OutputId} was printed already",
                input.BlockIndex,
                input.OutputId);
            return ResultOf(existing);
        }

        using var beatEnds = new CancellationTokenSource();
        var beat = BeatAsync(beatEnds.Token);
        try
        {
            return await PrintAsync(input, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await beatEnds.CancelAsync().ConfigureAwait(false);
            await beat.ConfigureAwait(false);
        }
    }

    private async Task<RenderPdfBlockResult> PrintAsync(RenderPdfBlockInput input, CancellationToken cancellationToken)
    {
        var reference = await jobs.FindTemplateReferenceAsync(input.ReportJobId, cancellationToken).ConfigureAwait(false)
            ?? throw Refuse(ExecutionFailed, "the job is not recorded");
        var pin = reference.Pin ?? throw Refuse(ExecutionFailed, "the job's template version was never resolved");

        var record = await versions.FindAsync(pin.TemplateId, pin.ResolvedVersion, cancellationToken).ConfigureAwait(false)
            ?? throw Refuse(ExecutionFailed, "the job's template version was never taken");
        if (!string.Equals(record.PackageSha256, pin.PackageSha256, StringComparison.Ordinal))
        {
            // The pin names the package the job was fixed to; a record that no longer
            // matches it is not the version this job prints.
            throw Refuse(ExecutionFailed, "the taken version does not match the job's pin");
        }

        var partition = await jobs.FindPartitionAsync(input.ReportJobId, cancellationToken).ConfigureAwait(false)
            ?? throw Refuse(ExecutionFailed, "the job has no partition");
        if (input.BlockIndex < 0 || input.BlockIndex >= partition.BlockCount)
        {
            throw Refuse(ExecutionFailed, "the block is not part of the job's partition");
        }

        var state = await jobs.FindForWorkflowAsync(input.ReportJobId, cancellationToken).ConfigureAwait(false)
            ?? throw Refuse(ExecutionFailed, "the job is not recorded");

        var index = await ReadIndexAsync(partition, cancellationToken).ConfigureAwait(false);
        var entry = index.Blocks.FirstOrDefault(block => block.BlockIndex == input.BlockIndex)
            ?? throw Refuse(ExecutionFailed, "the index page holds no such block");
        var (header, records) = await ReadBlockAsync(entry, cancellationToken).ConfigureAwait(false);

        Directory.CreateDirectory(ScratchRoot);
        TakenTemplatePackage package;
        try
        {
            package = await packages.OpenAsync(record, ScratchRoot, cancellationToken).ConfigureAwait(false);
        }
        catch (TemplatePackageUnavailableException unavailable) when (unavailable.Outcome == StorageOutcome.Transient)
        {
            throw;
        }
        catch (TemplatePackageUnavailableException)
        {
            throw Refuse(ExecutionFailed, "the taken copy of the template version could not be read");
        }
        catch (TemplatePackageCorruptException)
        {
            throw Refuse(ExecutionFailed, "the taken copy of the template version does not read back intact");
        }

        await using (package.ConfigureAwait(false))
        {
            var files = new FolderTemplatePackageFiles(package.Root);
            RenderManifest manifest;
            PreparedTemplate template;
            try
            {
                manifest = RenderManifestMapping.From(record);
                template = prepared.GetOrAdd(pin.PackageSha256, () => adapter.Prepare(manifest, files));
            }
            catch (TemplatePackageException)
            {
                throw Refuse(ExecutionFailed, "the taken copy cannot be prepared");
            }

            var composition = new CompositionRequest(
                template,
                new CompositionBlock(
                    input.BlockIndex,
                    records.Count,
                    entry.FirstRecordOrdinal,
                    IsFirst: input.BlockIndex == 0,
                    IsLast: input.BlockIndex == partition.BlockCount - 1),
                header,
                records,
                new CompositionRender(
                    manifest.LocaleDefaults.Culture,
                    manifest.LocaleDefaults.TimeZone,
                    state.Job.ReportClock),
                manifest.GroupKey);

            using var work = WorkerTemporaryDirectory.Create();
            var request = new BlockRenderRequest(
                manifest,
                composition,
                files,
                new PartitionFacts(partition.BlockCount, partition.RecordCount),
                new IndexFacts(entry.BlockIndex, entry.RecordCount, entry.FirstRecordOrdinal),
                new BlockFacts(
                    entry.BlockIndex,
                    entry.FirstRecordOrdinal,
                    records.Select(item => new BlockRecord(item.Ordinal, item.Values)).ToList(),
                    header),
                input.BlockIndex,
                IsFirstBlock: input.BlockIndex == 0,
                work.Path);

            var outcome = await renderer.RenderAsync(request, cancellationToken).ConfigureAwait(false);
            RecordTheRender(outcome);
            if (!outcome.Passed)
            {
                throw VerificationFailed(input, outcome);
            }

            var attempt = ActivityExecutionContext.Current.Info;
            var attemptId = RenderPdfBlockOptions.AttemptId(
                attempt.WorkflowId ?? string.Empty,
                attempt.WorkflowRunId ?? string.Empty,
                attempt.ActivityId,
                attempt.Attempt);
            var key = ObjectKey.FromSegments("outputs", input.ReportJobId, input.OutputId, attemptId);

            ObjectWrite written;
            var storeStarted = System.Diagnostics.Stopwatch.GetTimestamp();
            await using (var content = new FileStream(outcome.PdfPath!, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16))
            {
                var write = await objects.WriteAsync(key, content, cancellationToken).ConfigureAwait(false);
                written = write.Value
                    ?? throw new InvalidOperationException("the storage provider reported success without an object");
            }

            metrics.BlockPhase(
                PdfRenderingMetrics.StorePhase,
                System.Diagnostics.Stopwatch.GetElapsedTime(storeStarted));

            if (written.Size != outcome.SizeBytes || !string.Equals(written.Sha256, outcome.Sha256, StringComparison.Ordinal))
            {
                // The bytes the store holds are not the bytes that passed the checks.
                throw Refuse(ExecutionFailed, "the stored block does not match the printed file");
            }

            var commit = await jobs.CommitPartAsync(
                new PartCommit(
                    input.OutputId,
                    input.BlockIndex,
                    key.ToString(),
                    written.Sha256,
                    written.Size,
                    outcome.Verification.Pages,
                    pin.PackageSha256,
                    clock.GetUtcNow()),
                cancellationToken).ConfigureAwait(false);

            switch (commit.Outcome)
            {
                case PartCommitOutcome.Committed:
                case PartCommitOutcome.AlreadyCommitted when commit.Existing is not null:
                    logger.LogInformation(
                        "Block {BlockIndex} of output {OutputId} printed: {PageCount} pages, verified in {VerificationMs} ms",
                        input.BlockIndex,
                        input.OutputId,
                        outcome.Verification.Pages,
                        outcome.Verification.ReadAndAlignMs + outcome.Verification.SourceChecksMs);
                    return ResultOf(commit.Existing!);
                default:
                    logger.LogWarning(
                        "Block {BlockIndex} of output {OutputId} could not be committed: the output is no longer running",
                        input.BlockIndex,
                        input.OutputId);
                    throw Refuse(ExecutionFailed, "the output is no longer running");
            }
        }
    }

    // The four phases and the verdict of one render, as §11 names them: the durations of the
    // composition, the print, the verification and the store, the outcome with its class, the
    // pages the block holds, and the requests the document made that were aborted.
    private void RecordTheRender(BlockRenderOutcome outcome)
    {
        var verification = outcome.Verification;
        metrics.BlockPhase(PdfRenderingMetrics.ComposePhase, TimeSpan.FromMilliseconds(outcome.ComposeMs));
        if (outcome.Print is { } print)
        {
            metrics.BlockPhase(PdfRenderingMetrics.PrintPhase, print.Duration);
            metrics.AbortedRequests(print.AbortedRequests);
        }

        metrics.BlockPhase(
            PdfRenderingMetrics.VerifyPhase,
            TimeSpan.FromMilliseconds(verification.ReadAndAlignMs + verification.SourceChecksMs));
        metrics.VerificationOutcome(verification.Passed, verification.Reason);
        if (verification.Pages > 0)
        {
            metrics.Pages(verification.Pages);
        }
    }

    private ApplicationFailureException VerificationFailed(RenderPdfBlockInput input, BlockRenderOutcome outcome)
    {
        var verification = outcome.Verification;
        if (string.Equals(verification.Reason, VerificationReasons.PageAccounting, StringComparison.Ordinal))
        {
            logger.LogWarning(
                "Block {BlockIndex} of output {OutputId} refused: the package declares page totals",
                input.BlockIndex,
                input.OutputId);
            return new ApplicationFailureException(
                $"the package declares page totals, which this service cannot print (reason={verification.Reason})",
                errorType: UnsupportedCapability,
                nonRetryable: true);
        }

        logger.LogInformation(
            "Block {BlockIndex} of output {OutputId} failed verification: {FailureClass} on page {Page}, record {Ordinal}",
            input.BlockIndex,
            input.OutputId,
            verification.Reason,
            verification.Page,
            verification.RecordOrdinal);

        var message = new System.Text.StringBuilder(PaginationFailed);
        Append(message, "reason", verification.Reason);
        Append(message, "page", verification.Page);
        Append(message, "record", verification.RecordOrdinal);
        Append(message, "unit", verification.UnitIndex);
        Append(message, "offset", verification.Offset);
        return new ApplicationFailureException(message.ToString(), errorType: PaginationFailed, nonRetryable: true);
    }

    private static void Append(System.Text.StringBuilder message, string name, object? value)
    {
        if (value is null)
        {
            return;
        }

        message.Append(' ').Append(name).Append('=').Append(
            Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture));
    }

    private async Task<BlockIndexPage> ReadIndexAsync(PartitionRecord partition, CancellationToken cancellationToken)
    {
        var read = await objects
            .OpenReadAsync(ObjectKey.Create(partition.IndexObjectKey), partition.IndexSizeBytes, partition.IndexSha256, cancellationToken)
            .ConfigureAwait(false);
        if (!read.Succeeded)
        {
            throw RefuseUnverifiable(read.Outcome, read.Error, "the index page");
        }

        try
        {
            await using var page = read.Value!;
            using var buffer = new MemoryStream();
            await page.Content.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
            return BlockIndexPage.Parse(buffer.ToArray());
        }
        catch (StorageIntegrityException)
        {
            throw Refuse(ExecutionFailed, "the index page does not match its record");
        }
        catch (System.Text.Json.JsonException)
        {
            throw Refuse(ExecutionFailed, "the index page is not an index page");
        }
    }

    private async Task<(IReadOnlyDictionary<string, object?> Header, IReadOnlyList<ComposedRecord> Records)> ReadBlockAsync(
        BlockIndexEntry entry,
        CancellationToken cancellationToken)
    {
        var read = await objects
            .OpenReadAsync(ObjectKey.Create(entry.Key), entry.SizeBytes, entry.Sha256, cancellationToken)
            .ConfigureAwait(false);
        if (!read.Succeeded)
        {
            throw RefuseUnverifiable(read.Outcome, read.Error, "the block");
        }

        try
        {
            await using var block = read.Value!;
            IReadOnlyDictionary<string, object?>? header = null;
            var records = new List<ComposedRecord>((int)Math.Min(entry.RecordCount, 2048));
            await foreach (var value in HeaderItemsReader.ReadAsync(block.Content, cancellationToken).ConfigureAwait(false))
            {
                if (value.Ordinal == 0)
                {
                    header = RenderContextFactory.Header(value.Json.Span);
                    continue;
                }

                records.Add(new ComposedRecord(value.Ordinal, RenderContextFactory.Item(value)));
            }

            return (
                header ?? throw Refuse(ExecutionFailed, "the block holds no header"),
                records);
        }
        catch (StorageIntegrityException)
        {
            throw Refuse(ExecutionFailed, "the block does not match its record");
        }
        catch (DataContractViolation)
        {
            throw Refuse(ExecutionFailed, "the block cannot be read");
        }
    }

    private Task BeatAsync(CancellationToken cancellationToken) => Task.Run(
        async () =>
        {
            using var timer = new PeriodicTimer(RenderPdfBlockOptions.HeartbeatEvery);
            try
            {
                while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
                {
                    ActivityExecutionContext.Current.Heartbeat();
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
        });

    private static RenderPdfBlockResult ResultOf(PartCommit commit) =>
        new(commit.ObjectKey, commit.Sha256, commit.SizeBytes, commit.PageCount);

    private static ApplicationFailureException Refuse(string code, string reason) =>
        new($"{code}: {reason}", errorType: code, nonRetryable: true);

    private static Exception RefuseUnverifiable(StorageOutcome outcome, StorageError? error, string what) =>
        outcome == StorageOutcome.IntegrityFailure
            ? Refuse(ExecutionFailed, what + " does not match its record")
            : new StorageException(outcome, error ?? new StorageError("storage.unavailable", "the store is unavailable"));
}
