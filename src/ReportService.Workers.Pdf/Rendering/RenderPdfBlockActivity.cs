using Microsoft.Extensions.Logging;
using ReportService.Data;
using ReportService.Jobs;
using ReportService.Pdf.Verification;
using ReportService.Rendering;
using ReportService.ServiceDefaults.Telemetry;
using ReportService.Storage;
using ReportService.TemplatePackages.Manifest;
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
    TemplateEngineAdapterRegistry engines,
    IBlockRenderer renderer,
    PdfRenderingMetrics metrics,
    TimeProvider clock,
    ILoggerFactory loggers,
    ILogger<RenderPdfBlockActivity> logger,
    string? scratchRoot = null,
    GlobalisationProvenance? globalisation = null)
{
    /// <summary>
    /// The worker's watchdog, installed by the composition on the resolved activity and by a
    /// test on the activity it constructs. <see langword="null"/> in a process that runs no
    /// watchdog, where an attempt registers nowhere.
    /// </summary>
    internal RenderWatchdog? Watchdog { get; set; }

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

    /// <summary>
    /// The seam that forces the second attempt of one block: it acts after the PDF is verified
    /// and streamed and before the part is committed, so a test can read the two printed
    /// documents a retry really produces. A process registers no implementation, and the seam
    /// is reachable only from the tests this assembly names - the production composition
    /// asserts it holds none.
    /// </summary>
    internal PartCommitFault? PartCommitFault { get; set; }

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
        var attempt = ActivityExecutionContext.Current.Info;
        var registration = Watchdog?.Register(
            input.OutputId,
            input.BlockIndex,
            attempt.Attempt,
            clock.GetUtcNow(),
            RenderPdfBlockOptions.StartToClose,
            beatEnds);
        var beat = BeatAsync(registration, beatEnds.Token);
        RenderPdfBlockResult result;
        try
        {
            // A fault the step did not map is named here, where the block and the output are
            // known, and by its type - never by its message, which is not the service's to
            // publish. A refusal is not logged here: every one of them is logged where it is
            // decided (the presentation and engine catches, the page and verification checks,
            // the commit, and the sites that call RefuseAndLog), so no line is written twice.
            try
            {
                result = await PrintAsync(input, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception unhandled) when (unhandled is not ApplicationFailureException)
            {
                logger.LogWarning(
                    "Block {BlockIndex} of output {OutputId} could not be finished ({ExceptionType})",
                    input.BlockIndex,
                    input.OutputId,
                    unhandled.GetType().Name);
                throw;
            }
        }
        finally
        {
            if (registration is not null)
            {
                Watchdog!.Unregister(registration);
            }

            await beatEnds.CancelAsync().ConfigureAwait(false);
            await beat.ConfigureAwait(false);
        }

        // The watchdog stopped this attempt: the activity raises the typed failure the
        // watchdog built, when the process has not ended yet. The recorded cause is what the
        // child maps, so this is the engine's own retry, never the mapping's source.
        if (registration is { Tripped: true, Failure: { } failure })
        {
            throw failure;
        }

        return result;
    }

    private async Task<RenderPdfBlockResult> PrintAsync(RenderPdfBlockInput input, CancellationToken cancellationToken)
    {
        var reference = await jobs.FindTemplateReferenceAsync(input.ReportJobId, cancellationToken).ConfigureAwait(false)
            ?? throw RefuseAndLog(input, "the job is not recorded");
        var pin = reference.Pin ?? throw RefuseAndLog(input, "the job's template version was never resolved");

        var record = await versions.FindAsync(pin.TemplateId, pin.ResolvedVersion, cancellationToken).ConfigureAwait(false)
            ?? throw RefuseAndLog(input, "the job's template version was never taken");
        if (!string.Equals(record.PackageSha256, pin.PackageSha256, StringComparison.Ordinal))
        {
            // The pin names the package the job was fixed to; a record that no longer
            // matches it is not the version this job prints.
            throw RefuseAndLog(input, "the taken version does not match the job's pin");
        }

        var partition = await jobs.FindPartitionAsync(input.ReportJobId, cancellationToken).ConfigureAwait(false)
            ?? throw RefuseAndLog(input, "the job has no partition");
        if (input.BlockIndex < 0 || input.BlockIndex >= partition.BlockCount)
        {
            throw RefuseAndLog(input, "the block is not part of the job's partition");
        }

        var state = await jobs.FindForWorkflowAsync(input.ReportJobId, cancellationToken).ConfigureAwait(false)
            ?? throw RefuseAndLog(input, "the job is not recorded");

        var index = await ReadIndexAsync(input, partition, cancellationToken).ConfigureAwait(false);
        var entry = index.Blocks.FirstOrDefault(block => block.BlockIndex == input.BlockIndex)
            ?? throw RefuseAndLog(input, "the index page holds no such block");
        var (header, records) = await ReadBlockAsync(input, entry, cancellationToken).ConfigureAwait(false);

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
            throw RefuseAndLog(input, "the taken copy of the template version could not be read");
        }
        catch (TemplatePackageCorruptException)
        {
            throw RefuseAndLog(input, "the taken copy of the template version does not read back intact");
        }

        await using (package.ConfigureAwait(false))
        {
            var files = new FolderTemplatePackageFiles(package.Root);
            RenderManifest manifest;
            ITemplateEngineAdapter adapter;
            PreparedTemplate template;
            try
            {
                manifest = RenderManifestMapping.From(record);
            }
            catch (TemplatePackageException)
            {
                throw RefuseAndLog(input, "the taken copy cannot be prepared");
            }

            // The engine is decided before the package is prepared: the version's own
            // declaration, the pin's cross-check and the engines this deployment offers.
            // A refusal is mapped here, where the decision is taken - the same mapping the
            // renderer's own refusal takes below - so D2's code and reason reach the failure
            // whether the step or the renderer refused, and nothing has been composed yet.
            var decision = EngineResolution.Resolve(manifest, TemplateEngineNames.ToManifestName(pin.Engine), engines);
            if (decision.Adapter is not { } resolved)
            {
                throw EngineRefusalOf(new EngineRefusedException(decision), input);
            }

            adapter = resolved;
            try
            {
                template = prepared.GetOrAdd(pin.PackageSha256, () => adapter.Prepare(manifest, files));
            }
            catch (TemplatePackageException)
            {
                throw RefuseAndLog(input, "the taken copy cannot be prepared");
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
                    state.Job.Culture,
                    state.Job.TimeZone,
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
                work.Path,
                PinEngine: adapter.Engine);

            BlockRenderOutcome outcome;
            try
            {
                outcome = await renderer.RenderAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (TemplateGuardException guard) when (string.Equals(guard.Code, TemplateErrorCodes.RenderTimeout, StringComparison.Ordinal))
            {
                // The composition passed its 60 s deadline. The guard's timeout is retryable -
                // ADR-022 counts it against the block's three attempts, so a deterministic
                // runaway template cannot restart workers indefinitely - and the cause is
                // recorded here, at the activity boundary, where the store is reachable: the
                // template library keeps no store dependency.
                logger.LogWarning(
                    "Block {BlockIndex} of output {OutputId} not printed: the composition passed its deadline",
                    input.BlockIndex,
                    input.OutputId);
                await RecordAttemptCauseAsync(input, AttemptCauseVocabulary.GuardDeadline, cancellationToken).ConfigureAwait(false);
                throw new ApplicationFailureException(
                    guard.Message,
                    errorType: TemplateErrorCodes.RenderTimeout,
                    nonRetryable: false);
            }
            catch (TemplateGuardException guard) when (string.Equals(guard.Code, TemplateErrorCodes.QuotaExceeded, StringComparison.Ordinal))
            {
                // The size cap is deterministic: it is never retried, and it writes no cause
                // row, because no retry could make the same block smaller.
                logger.LogWarning(
                    "Block {BlockIndex} of output {OutputId} not printed: the composition passed the {Limit} limit",
                    input.BlockIndex,
                    input.OutputId,
                    guard.Limit);
                throw new ApplicationFailureException(
                    guard.Message,
                    errorType: TemplateErrorCodes.QuotaExceeded,
                    nonRetryable: true);
            }
            catch (EngineRefusedException refused)
            {
                // The version's engine cannot compose the block: nothing was composed or
                // printed, the diagnostic names the field and the engines, and the same
                // version would fail the same way on every attempt.
                throw EngineRefusalOf(refused, input);
            }
            catch (PresentationRefusedException refused)
            {
                // A value or a format cannot be presented as the version declares: nothing was
                // printed, and the refusal carries the code, the reason, the function and the
                // record - never a value and never the format's expansion.
                logger.LogWarning(
                    "Block {BlockIndex} of output {OutputId} not printed: {Reason}",
                    input.BlockIndex,
                    input.OutputId,
                    refused.Message);
                throw new ApplicationFailureException(refused.Message, errorType: refused.Failure.Code, nonRetryable: true);
            }
            catch (PresentationUnavailable unavailable)
            {
                // The job's culture, time zone or clock is absent or unusable: nothing was
                // composed, and the same job would fail the same way on every attempt.
                logger.LogWarning(
                    "Block {BlockIndex} of output {OutputId} not printed: presentation unavailable at {Field}",
                    input.BlockIndex,
                    input.OutputId,
                    unavailable.Field);
                throw Refuse(ExecutionFailed, unavailable.Message);
            }

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
                throw RefuseAndLog(input, "the stored block does not match the printed file");
            }

            // The one seam a test installs: the second attempt is forced after the document
            // was verified and streamed and before the commit, so both printed documents exist
            // and the first one is the orphan of an attempt that committed nothing.
            if (PartCommitFault is { } fault)
            {
                await fault.FailOnceAsync(input.OutputId, input.BlockIndex, cancellationToken).ConfigureAwait(false);
            }

            // What produced this block's presentation, written in the same transaction as the
            // commit: the job's fixed settings, the version's engine and the version it runs,
            // and the globalisation data of this process. Names and identifiers, never a value.
            var settings = globalisation ?? GlobalisationProvenance.Current;
            var presentation = new PresentationProvenance(
                state.Job.Culture ?? throw RefuseAndLog(input, "presentation unavailable at culture"),
                state.Job.TimeZone ?? throw RefuseAndLog(input, "presentation unavailable at time-zone"),
                state.Job.ReportClock,
                adapter.Engine,
                adapter.Version,
                settings.IcuVersion,
                settings.TzDataVersion);

            var commit = await jobs.CommitPartAsync(
                new PartCommit(
                    input.OutputId,
                    input.BlockIndex,
                    key.ToString(),
                    written.Sha256,
                    written.Size,
                    outcome.Verification.Pages,
                    pin.PackageSha256,
                    clock.GetUtcNow(),
                    presentation),
                cancellationToken).ConfigureAwait(false);

            switch (commit.Outcome)
            {
                case PartCommitOutcome.Committed:
                case PartCommitOutcome.AlreadyCommitted when commit.Existing is not null:
                    logger.LogInformation(
                        "Block {BlockIndex} of output {OutputId} printed: {PageCount} pages on {Paper} {Orientation}, verified in {VerificationMs} ms, engine {Engine} {EngineVersion}, culture {Culture}, time zone {TimeZone}",
                        input.BlockIndex,
                        input.OutputId,
                        outcome.Verification.Pages,
                        outcome.Verification.Paper,
                        outcome.Verification.Orientation,
                        outcome.Verification.ReadAndAlignMs + outcome.Verification.SourceChecksMs,
                        presentation.Engine,
                        presentation.EngineVersion,
                        presentation.Culture,
                        presentation.TimeZone);
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

        if (string.Equals(verification.Reason, VerificationReasons.PageSettingsInvalid, StringComparison.Ordinal))
        {
            // The member's path is configuration, never a value of the consumer's data.
            logger.LogWarning(
                "Block {BlockIndex} of output {OutputId} not printed: page settings invalid at {Field}",
                input.BlockIndex,
                input.OutputId,
                verification.Field);
            return new ApplicationFailureException(
                PaginationFailed + " reason=" + verification.Reason + " field=" + verification.Field,
                errorType: PaginationFailed,
                nonRetryable: true);
        }

        if (string.Equals(verification.Reason, VerificationReasons.PageSize, StringComparison.Ordinal))
        {
            // Layout facts only: the page's number and the size it was read at.
            logger.LogWarning(
                "Block {BlockIndex} of output {OutputId} failed verification: {FailureClass} on page {Page}, measured {MeasuredWidth} x {MeasuredHeight} pt, expected {ExpectedWidth} x {ExpectedHeight} pt",
                input.BlockIndex,
                input.OutputId,
                verification.Reason,
                verification.Page,
                verification.MeasuredWidthPt,
                verification.MeasuredHeightPt,
                verification.ExpectedWidthPt,
                verification.ExpectedHeightPt);
        }
        else
        {
            logger.LogInformation(
                "Block {BlockIndex} of output {OutputId} failed verification: {FailureClass} on page {Page}, record {Ordinal}",
                input.BlockIndex,
                input.OutputId,
                verification.Reason,
                verification.Page,
                verification.RecordOrdinal);
        }

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

    private async Task<BlockIndexPage> ReadIndexAsync(RenderPdfBlockInput input, PartitionRecord partition, CancellationToken cancellationToken)
    {
        var read = await objects
            .OpenReadAsync(ObjectKey.Create(partition.IndexObjectKey), partition.IndexSizeBytes, partition.IndexSha256, cancellationToken)
            .ConfigureAwait(false);
        if (!read.Succeeded)
        {
            throw Unverifiable(input, read.Outcome, read.Error, "the index page");
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
            throw RefuseAndLog(input, "the index page does not match its record");
        }
        catch (System.Text.Json.JsonException)
        {
            throw RefuseAndLog(input, "the index page is not an index page");
        }
    }

    private async Task<(IReadOnlyDictionary<string, object?> Header, IReadOnlyList<ComposedRecord> Records)> ReadBlockAsync(
        RenderPdfBlockInput input,
        BlockIndexEntry entry,
        CancellationToken cancellationToken)
    {
        var read = await objects
            .OpenReadAsync(ObjectKey.Create(entry.Key), entry.SizeBytes, entry.Sha256, cancellationToken)
            .ConfigureAwait(false);
        if (!read.Succeeded)
        {
            throw Unverifiable(input, read.Outcome, read.Error, "the block");
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
                header ?? throw RefuseAndLog(input, "the block holds no header"),
                records);
        }
        catch (StorageIntegrityException)
        {
            throw RefuseAndLog(input, "the block does not match its record");
        }
        catch (DataContractViolation)
        {
            throw RefuseAndLog(input, "the block cannot be read");
        }
    }

    private Task BeatAsync(AttemptRegistration? registration, CancellationToken cancellationToken) => Task.Run(
        async () =>
        {
            using var timer = new PeriodicTimer(RenderPdfBlockOptions.HeartbeatEvery);
            try
            {
                while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
                {
                    // A watchdog trip means the worker is stopping and the attempt is being
                    // retried elsewhere: the beats stop here, so the engine sees the timeout.
                    if (registration is { Tripped: true })
                    {
                        return;
                    }

                    ActivityExecutionContext.Current.Heartbeat();
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
        });

    private static RenderPdfBlockResult ResultOf(PartCommit commit) =>
        new(commit.ObjectKey, commit.Sha256, commit.SizeBytes, commit.PageCount);

    /// <summary>
    /// The one mapping of an engine refusal: the code and the reason D2 gives it, non-retryably,
    /// with nothing composed. The step's own decision and the renderer's refusal both end here,
    /// so the same refusal cannot be mapped two ways, and the line names the field and the
    /// engine names - never a value of the job's data.
    /// </summary>
    private ApplicationFailureException EngineRefusalOf(EngineRefusedException refused, RenderPdfBlockInput input)
    {
        logger.LogWarning(
            "Block {BlockIndex} of output {OutputId} not printed: {Reason}",
            input.BlockIndex,
            input.OutputId,
            refused.Message);
        return new ApplicationFailureException(refused.Message, errorType: refused.Code, nonRetryable: true);
    }

    private static ApplicationFailureException Refuse(string code, string reason) =>
        new($"{code}: {reason}", errorType: code, nonRetryable: true);

    // The cause of a guard deadline, written where the store is reachable. A store that
    // cannot answer does not replace the guard's code: the carried RENDER_TIMEOUT still
    // maps, and only its reason falls back to "none", which the child reads as such.
    private async Task RecordAttemptCauseAsync(RenderPdfBlockInput input, string cause, CancellationToken cancellationToken)
    {
        try
        {
            await jobs.RecordAttemptCauseAsync(
                new AttemptCause(
                    input.OutputId,
                    input.BlockIndex,
                    ActivityExecutionContext.Current.Info.Attempt,
                    cause,
                    clock.GetUtcNow()),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(
                "Block {BlockIndex} of output {OutputId} could not record its attempt cause ({ExceptionType})",
                input.BlockIndex,
                input.OutputId,
                exception.GetType().Name);
        }
    }

    // A refusal the step has not reported yet: the line names the block, the output and the
    // service's own constant text, and the failure it returns is the one the caller throws.
    private ApplicationFailureException RefuseAndLog(RenderPdfBlockInput input, string reason)
    {
        logger.LogWarning(
            "Block {BlockIndex} of output {OutputId} refused: {Reason}",
            input.BlockIndex,
            input.OutputId,
            reason);
        return Refuse(ExecutionFailed, reason);
    }

    // The read of an object the step verified: an integrity failure is a refusal and is logged
    // here; a transient one stays the storage failure the engine retries.
    private Exception Unverifiable(RenderPdfBlockInput input, StorageOutcome outcome, StorageError? error, string what)
    {
        if (outcome != StorageOutcome.IntegrityFailure)
        {
            return RefuseUnverifiable(outcome, error, what);
        }

        return RefuseAndLog(input, what + " does not match its record");
    }

    private static Exception RefuseUnverifiable(StorageOutcome outcome, StorageError? error, string what) =>
        outcome == StorageOutcome.IntegrityFailure
            ? Refuse(ExecutionFailed, what + " does not match its record")
            : new StorageException(outcome, error ?? new StorageError("storage.unavailable", "the store is unavailable"));
}
