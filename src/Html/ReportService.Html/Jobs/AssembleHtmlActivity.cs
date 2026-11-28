using Microsoft.Extensions.Logging;
using ReportService.Data;
using ReportService.Jobs;
using ReportService.ServiceDefaults;
using ReportService.Storage;
using ReportService.TemplatePackages.Manifest;
using ReportService.TemplatePackages.Packages;
using ReportService.TemplatePackages.Records;
using ReportService.Templates;
using Temporalio.Activities;
using Temporalio.Exceptions;

namespace ReportService.Workers.Orchestration.Jobs;

/// <summary>
/// Assembles one job's HTML output: it reads the job's pin, the taken template version, the
/// partition's single block and the composed document-mode HTML, runs the self-contained
/// <see cref="HtmlOutputAssembler"/> (US-008.BOLT-001) and writes the one <c>.html</c> under the
/// output's attempt-scoped key — <b>never starting Chromium</b> (AC-4): it is served on the
/// orchestration worker's <c>report-html</c> queue and depends only on the composition, the
/// assembler and the data/storage projects, never on <c>ReportService.Rendering</c>. A package that
/// declares no HTML-capable template is refused with <c>UNSUPPORTED_CAPABILITY</c> before anything
/// is composed (AC-5); a reference reaching outside the packaged template is refused by the
/// assembler and nothing is written (ADR-047 §3.5). Deterministic refusals are never retried.
/// </summary>
public sealed class AssembleHtmlActivity(
    IJobStore jobs,
    ITemplateVersionStore versions,
    IObjectStore objects,
    TakenTemplatePackageReader packages,
    PreparedTemplateCache prepared,
    TemplateEngineAdapterRegistry engines,
    TimeProvider clock,
    ILoggerFactory loggers,
    ILogger<AssembleHtmlActivity> logger)
{
    /// <summary>The code a package this increment cannot produce HTML from ends with.</summary>
    private const string UnsupportedCapability = "UNSUPPORTED_CAPABILITY";

    /// <summary>The code a state the step cannot use ends with.</summary>
    private const string ExecutionFailed = "EXECUTION_FAILED";

    /// <summary>The directory a taken copy is extracted under, when the caller names none.</summary>
    public static readonly string DefaultScratchRoot =
        Path.Combine(Path.GetTempPath(), "report-service-html");

    /// <summary>The directory a taken copy of the version being assembled is extracted under.</summary>
    public string ScratchRoot { get; init; } = DefaultScratchRoot;

    /// <summary>Assembles one job's HTML output.</summary>
    /// <param name="input">The job and the output.</param>
    [Activity(HtmlOutputStepContracts.AssembleHtml)]
    public async Task<AssembleHtmlStepResult> RunAsync(AssembleHtmlStepInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var cancellationToken = ActivityExecutionContext.Current.CancellationToken;
        using var context = JobContext.Begin(input.ReportJobId, loggers);
        System.Diagnostics.Activity.Current?.SetTag("output.id", input.OutputId);

        var reference = await jobs.FindTemplateReferenceAsync(input.ReportJobId, cancellationToken).ConfigureAwait(false)
            ?? throw Refuse(ExecutionFailed, "the job is not recorded");
        var pin = reference.Pin ?? throw Refuse(ExecutionFailed, "the job's template version was never resolved");

        var record = await versions.FindAsync(pin.TemplateId, pin.ResolvedVersion, cancellationToken).ConfigureAwait(false)
            ?? throw Refuse(ExecutionFailed, "the job's template version was never taken");
        if (!string.Equals(record.PackageSha256, pin.PackageSha256, StringComparison.Ordinal))
        {
            throw Refuse(ExecutionFailed, "the taken version does not match the job's pin");
        }

        // HTML capability check (AC-5): a package that declares no HTML-capable template (no
        // outputs.html parts) is refused before anything is composed or written.
        var html = record.Manifest.Outputs?.Html;
        if (html?.Parts is not { } parts || string.IsNullOrEmpty(parts.DocumentStart))
        {
            logger.LogWarning("Output {OutputId} refused: the package declares no HTML output", input.OutputId);
            throw Refuse(UnsupportedCapability, "the package declares no HTML-capable template");
        }

        var partition = await jobs.FindPartitionAsync(input.ReportJobId, cancellationToken).ConfigureAwait(false)
            ?? throw Refuse(ExecutionFailed, "the job has no partition");
        var state = await jobs.FindForWorkflowAsync(input.ReportJobId, cancellationToken).ConfigureAwait(false)
            ?? throw Refuse(ExecutionFailed, "the job is not recorded");

        var manifest = HtmlManifestOf(record, parts, html.Styles);
        var decision = EngineResolution.Resolve(manifest, TemplateEngineNames.ToManifestName(pin.Engine), engines);
        if (decision.Adapter is not { } adapter)
        {
            logger.LogWarning("Output {OutputId} refused: {Reason}", input.OutputId, decision.Reason);
            throw new ApplicationFailureException(
                decision.Message ?? decision.Code ?? UnsupportedCapability,
                errorType: decision.Code ?? UnsupportedCapability,
                nonRetryable: true);
        }

        // The read, composition and assembly of one block can run for longer than the
        // step's heartbeat timeout on a large partition, so the step beats beside its work:
        // a beat runs on the same interval the data step uses (PdfOutputActivityOptions.
        // HeartbeatEvery) until the work ends, reporting the step is still running. The
        // assembly publishes no record-by-record count, so each beat carries no progress
        // detail — it only keeps the attempt alive. The beat ends with the work.
        using var assemblyEnds = new CancellationTokenSource();
        var beat = ActivityHeartbeat.WhileRunningAsync(
            PdfOutputActivityOptions.HeartbeatEvery,
            () => 0,
            _ => ActivityExecutionContext.Current.Heartbeat(),
            assemblyEnds.Token);
        try
        {
            var (header, records) = await ReadBlockZeroAsync(input, partition, cancellationToken).ConfigureAwait(false);

            PresentationSettings settings;
            try
            {
                settings = PresentationSettingsResolver.Resolve(
                    new PresentationValues(state.Job.Culture, state.Job.TimeZone, state.Job.ReportClock));
            }
            catch (PresentationUnavailable unavailable)
            {
                throw Refuse(ExecutionFailed, unavailable.Message);
            }

            Directory.CreateDirectory(ScratchRoot);
            var package = await packages.OpenAsync(record, ScratchRoot, cancellationToken).ConfigureAwait(false);
            await using (package.ConfigureAwait(false))
            {
                var files = new FolderTemplatePackageFiles(package.Root);
                PreparedTemplate template;
                try
                {
                    template = prepared.GetOrAdd(pin.PackageSha256, () => adapter.Prepare(manifest, files));
                }
                catch (TemplatePackageException)
                {
                    throw Refuse(UnsupportedCapability, "the taken copy cannot be prepared for HTML");
                }

                ComposedBlock composed;
                try
                {
                    composed = await new BlockComposer(clock).ComposeAsync(
                        new CompositionRequest(
                            template,
                            new CompositionBlock(
                                0,
                                records.Count,
                                records.Count == 0 ? 1 : records[0].Ordinal,
                                IsFirst: true,
                                IsLast: true),
                            header,
                            records,
                            new CompositionRender(state.Job.Culture, state.Job.TimeZone, state.Job.ReportClock),
                            manifest.GroupKey,
                            ImageSourcePolicy.Html),
                        adapter,
                        settings,
                        cancellationToken).ConfigureAwait(false);
                }
                catch (TemplateGuardException guard) when (string.Equals(guard.Code, TemplateErrorCodes.ImageSourceInvalid, StringComparison.Ordinal))
                {
                    // An image source the HTML output does not admit: decided by the data and the
                    // parts, never retried, nothing published. The reason names the part, the
                    // record and the attribute, never the value.
                    logger.LogWarning(
                        "Output {OutputId} refused: an image source is not admitted ({Reason})",
                        input.OutputId,
                        guard.Message);
                    throw new ApplicationFailureException(guard.Message, errorType: TemplateErrorCodes.ImageSourceInvalid, nonRetryable: true);
                }

                using var assembled = new MemoryStream();
                var result = await new HtmlOutputAssembler()
                    .AssembleAsync(composed, files, assembled, cancellationToken)
                    .ConfigureAwait(false);
                if (!result.IsProduced)
                {
                    // A definitive HTML failure: a reference outside the packaged template, a
                    // non-inlinable asset. Nothing is published (ADR-047 §3.5).
                    logger.LogWarning(
                        "Output {OutputId} refused: the HTML output is not self-contained ({Cause})",
                        input.OutputId,
                        result.Refusal?.Cause);
                    throw Refuse(UnsupportedCapability, "the HTML output is not self-contained");
                }

                var attempt = ActivityExecutionContext.Current.Info;
                var attemptId = PdfOutputActivityOptions.AttemptId(
                    attempt.WorkflowId ?? string.Empty,
                    attempt.WorkflowRunId ?? string.Empty,
                    attempt.ActivityId,
                    attempt.Attempt);
                var key = ObjectKey.FromSegments("outputs", input.ReportJobId, input.OutputId, attemptId);

                assembled.Position = 0;
                var write = await objects.WriteAsync(key, assembled, cancellationToken).ConfigureAwait(false);
                var written = write.Value
                    ?? throw new InvalidOperationException("the storage provider reported success without an object");

                logger.LogInformation("Output {OutputId} assembled: {SizeBytes} bytes", input.OutputId, written.Size);
                return new AssembleHtmlStepResult(key.ToString(), written.Sha256, written.Size);
            }
        }
        finally
        {
            await assemblyEnds.CancelAsync().ConfigureAwait(false);
            await beat.ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The render manifest of the package's HTML output: its document-mode parts and stylesheets,
    /// the engine the version fixed, and a placeholder PDF settings the HTML composition does not
    /// read (the composition needs only the parts and the engine).
    /// </summary>
    private static RenderManifest HtmlManifestOf(TemplateVersionRecord record, TemplateParts parts, IReadOnlyList<string> styles)
    {
        var declared = new Dictionary<TemplatePart, string>(5);
        Add(declared, TemplatePart.DocumentStart, parts.DocumentStart);
        Add(declared, TemplatePart.GroupStart, parts.GroupStart);
        Add(declared, TemplatePart.Row, parts.Row);
        Add(declared, TemplatePart.GroupEnd, parts.GroupEnd);
        Add(declared, TemplatePart.DocumentEnd, parts.DocumentEnd);

        return new RenderManifest(
            TemplateEngineNames.ToManifestName(record.Engine),
            record.EngineVersion,
            declared,
            styles,
            new PdfSettings("A4", "portrait", new PageMarginsMm(0, 0, 0, 0), PageTotalsEnabled: false, VerificationAnchors.None),
            new LocaleDefaults(record.Manifest.LocaleDefaults?.Culture, record.Manifest.LocaleDefaults?.TimeZone),
            record.Manifest.Partitioning?.GroupKey);
    }

    private static void Add(Dictionary<TemplatePart, string> parts, TemplatePart part, string? path)
    {
        if (!string.IsNullOrEmpty(path))
        {
            parts[part] = path;
        }
    }

    private async Task<(IReadOnlyDictionary<string, object?> Header, IReadOnlyList<ComposedRecord> Records)> ReadBlockZeroAsync(
        AssembleHtmlStepInput input,
        PartitionRecord partition,
        CancellationToken cancellationToken)
    {
        var indexRead = await objects
            .OpenReadAsync(ObjectKey.Create(partition.IndexObjectKey), partition.IndexSizeBytes, partition.IndexSha256, cancellationToken)
            .ConfigureAwait(false);
        if (!indexRead.Succeeded)
        {
            throw Refuse(ExecutionFailed, "the index page could not be read");
        }

        BlockIndexEntry entry;
        await using (indexRead.Value!.ConfigureAwait(false))
        {
            using var buffer = new MemoryStream();
            await indexRead.Value!.Content.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
            var page = BlockIndexPage.Parse(buffer.ToArray());
            entry = page.Blocks.FirstOrDefault(block => block.BlockIndex == 0)
                ?? throw Refuse(ExecutionFailed, "the index page holds no first block");
        }

        var blockRead = await objects
            .OpenReadAsync(ObjectKey.Create(entry.Key), entry.SizeBytes, entry.Sha256, cancellationToken)
            .ConfigureAwait(false);
        if (!blockRead.Succeeded)
        {
            throw Refuse(ExecutionFailed, "the block could not be read");
        }

        await using (blockRead.Value!.ConfigureAwait(false))
        {
            IReadOnlyDictionary<string, object?>? header = null;
            var records = new List<ComposedRecord>((int)Math.Min(entry.RecordCount, 2048));
            await foreach (var value in HeaderItemsReader.ReadAsync(blockRead.Value!.Content, cancellationToken).ConfigureAwait(false))
            {
                if (value.Ordinal == 0)
                {
                    header = RenderContextFactory.Header(value.Json.Span);
                    continue;
                }

                // The block document numbers its records locally; the index entry names the
                // snapshot's first ordinal. Read them as the global ordinals the entry
                // declares, exactly as the PDF render step does for its block.
                var ordinal = entry.FirstRecordOrdinal + (long)(value.Ordinal - 1);
                records.Add(new ComposedRecord(ordinal, RenderContextFactory.Item(value)));
            }

            return (header ?? throw Refuse(ExecutionFailed, "the block holds no header"), records);
        }
    }

    private static ApplicationFailureException Refuse(string code, string reason) =>
        new($"{code}: {reason}", errorType: code, nonRetryable: true);
}
