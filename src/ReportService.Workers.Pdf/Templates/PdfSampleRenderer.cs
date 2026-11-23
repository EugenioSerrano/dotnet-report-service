using System.Diagnostics;
using System.Security.Cryptography;
using System.Globalization;
using Microsoft.Extensions.Options;
using Microsoft.Playwright;
using ReportService.Pdf.Verification;
using ReportService.Rendering;
using ReportService.TemplateChecks.Sample;
using ReportService.TemplatePackages.Manifest;
using ReportService.TemplatePackages.Packages;
using ReportService.Templates;

namespace ReportService.Workers.Pdf.Templates;

/// <summary>
/// The product's one sample renderer, on the PDF worker: it composes the version's
/// sample in document mode and as one block, prints the block with the product's
/// printer, verifies it with the product's own post-render verification, and does the
/// same once more with zero records. It is a job's path - the same composer, printer,
/// verifier and text extraction - with the sample's own presentation, and it keeps
/// nothing: the composed HTML, the printed PDF and the work directory are gone when
/// the call returns.
/// <para>
/// The engine comes from the version's own declaration and only through
/// <see cref="TemplateEngineAdapterRegistry"/>; a version this deployment cannot render
/// is refused before the take runs. A failure is classified here, where the exception
/// and the part that was rendering are known; a class this renderer cannot name is
/// rethrown, never turned into a refusal. A composition or a print past its deadline,
/// and a browser that crashed or went away, are <see cref="SampleAttempt.Unavailable"/>:
/// the take retries them and never refuses the author's version.
/// </para>
/// <para>
/// The sample's presentation is the package's: its <c>localeDefaults</c> when it declares
/// both a culture and a time zone, else the invariant culture and UTC with the take's own
/// start instant. A job's presentation resolver refuses an absent culture by design, so
/// the sample's settings are built here and handed to the composer, the way a job hands
/// its fixed settings.
/// </para>
/// </summary>
public sealed class PdfSampleRenderer : ISampleRenderer
{
    /// <summary>The directory under the temporary directory every sample's work area is made in.</summary>
    public const string DefaultScratchRootName = "report-service-samples";

    private const string SampleFile = "sample.json";

    private readonly BlockComposer _composer;
    private readonly IPdfPrinter _printer;
    private readonly BlockVerifier _verifier;
    private readonly TemplateEngineAdapterRegistry _engines;
    private readonly PreparedTemplateCache _prepared;
    private readonly SampleRenderMetrics _metrics;
    private readonly TimeProvider _clock;
    private readonly SampleRenderOptions _options;
    private readonly string _scratchRoot;

    /// <summary>Creates the renderer over the capability the worker already runs.</summary>
    /// <param name="composer">The product's block composer.</param>
    /// <param name="printer">The product's PDF printer.</param>
    /// <param name="verifier">The product's post-render verification.</param>
    /// <param name="engines">The adapters this process registered.</param>
    /// <param name="prepared">The cache of prepared packages.</param>
    /// <param name="metrics">The sample's instruments.</param>
    /// <param name="clock">The clock the sample's start instant is read from.</param>
    /// <param name="options">The deadline of one render call.</param>
    /// <param name="scratchRoot">The directory work areas are made in, or <see langword="null"/> for the default.</param>
    public PdfSampleRenderer(
        BlockComposer composer,
        IPdfPrinter printer,
        BlockVerifier verifier,
        TemplateEngineAdapterRegistry engines,
        PreparedTemplateCache prepared,
        SampleRenderMetrics metrics,
        TimeProvider clock,
        IOptions<SampleRenderOptions> options,
        string? scratchRoot = null)
    {
        ArgumentNullException.ThrowIfNull(composer);
        ArgumentNullException.ThrowIfNull(printer);
        ArgumentNullException.ThrowIfNull(verifier);
        ArgumentNullException.ThrowIfNull(engines);
        ArgumentNullException.ThrowIfNull(prepared);
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(options);

        _composer = composer;
        _printer = printer;
        _verifier = verifier;
        _engines = engines;
        _prepared = prepared;
        _metrics = metrics;
        _clock = clock;
        _options = options.Value;
        _scratchRoot = scratchRoot ?? Path.Combine(Path.GetTempPath(), DefaultScratchRootName);
    }

    /// <inheritdoc />
    public async Task<SampleAttempt> RenderAsync(SampleRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var engine = EngineNameOf(request.Manifest);
        var started = Stopwatch.GetTimestamp();

        var read = await SamplePartition.ReadAsync(request.SampleJson, cancellationToken).ConfigureAwait(false);
        if (read is not SampleRead.Partitioned partitioned)
        {
            var overBound = (SampleRead.OverBound)read;
            var refusal = FailedAttempt(new SampleFailure(
                SampleFailureKind.OverBound,
                SampleFile,
                Records: overBound.Records,
                RecordBytes: overBound.RecordBytes,
                Bound: overBound.Bound));
            Record(engine, refusal, started);
            return refusal;
        }

        var sample = request.EmptySample ? partitioned.Sample.Empty() : partitioned.Sample;
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(_options.RenderBudget);

        SampleAttempt attempt;
        try
        {
            attempt = await RenderOnceAsync(request, sample, budget.Token, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            attempt = new SampleAttempt.Unavailable(SampleUnavailableCause.RenderTimeout);
        }

        Record(engine, attempt, started);
        return attempt;
    }

    private void Record(string engine, SampleAttempt attempt, long started)
    {
        var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        switch (attempt)
        {
            case SampleAttempt.Rendered:
                _metrics.Rendered(engine, SampleRenderMetrics.PassedOutcome, elapsed);
                break;
            case SampleAttempt.Failed failed:
                _metrics.Rendered(engine, SampleRenderMetrics.RefusedOutcome, elapsed);
                _metrics.Refused(engine, SampleRenderCheck.RuleOf(failed.Failure));
                break;
            default:
                _metrics.Rendered(engine, SampleRenderMetrics.UnavailableOutcome, elapsed);
                break;
        }
    }

    private async Task<SampleAttempt> RenderOnceAsync(
        SampleRequest request,
        SamplePartition sample,
        CancellationToken budgetToken,
        CancellationToken takeToken)
    {
        if (request.Manifest.Engine is not { } declared
            || !_engines.TryGet(TemplateEngineNames.ToManifestName(declared), out var adapter))
        {
            // The take refuses an engine this deployment does not run before its checks run;
            // reaching this point is a composition defect, not a property of the version.
            throw new InvalidOperationException("the version's engine is not one this deployment offers");
        }

        if (request.Manifest.Outputs?.Pdf is null || request.Manifest.Outputs.Pdf.Verification is null)
        {
            // No declaration of the fields the page prints as they are: nothing can be bound
            // to the printed page, which is the refusal the verification itself gives.
            return FailedAttempt(new SampleFailure(
                SampleFailureKind.Verification,
                PackagePath.ManifestFileName,
                Reason: VerificationReasons.AnchorsUndeclared));
        }

        PresentationSettings presentation;
        try
        {
            presentation = SettingsOf(request.Manifest, _clock.GetUtcNow());
        }
        catch (PresentationUnavailable)
        {
            // The package's own locale declaration cannot be used: the declaration is the file to fix.
            return FailedAttempt(new SampleFailure(SampleFailureKind.EngineError, PackagePath.ManifestFileName));
        }

        var blockManifest = RenderManifestMapping.From(request.Manifest, declared, adapter.Version);
        if (blockManifest.DeclaresPageTotals)
        {
            return FailedAttempt(new SampleFailure(
                SampleFailureKind.Verification,
                PackagePath.ManifestFileName,
                Reason: VerificationReasons.PageAccounting));
        }

        var htmlManifest = HtmlManifestOf(request.Manifest, blockManifest);
        var blockPart = new PartRecordingAdapter(adapter, blockManifest);
        var htmlPart = htmlManifest is null ? blockPart : new PartRecordingAdapter(adapter, htmlManifest);

        // Preparing is not a render: a package the static checks passed parses here, and a
        // failure that is not would be a defect of the composition, never a refusal.
        var blockPrepared = Prepare(CacheKeyOf(request, adapter, blockManifest), blockManifest, blockPart, request.Files);
        var htmlPrepared = htmlManifest is null
            ? blockPrepared
            : Prepare(CacheKeyOf(request, adapter, htmlManifest), htmlManifest, htmlPart, request.Files);

        var render = new CompositionRender(presentation.Culture.Name, presentation.TimeZone.Id, presentation.Clock);
        var work = Path.Combine(_scratchRoot, Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        try
        {
            var html = await Compose(htmlPrepared, htmlPart, request, sample, render, presentation, budgetToken, takeToken);
            if (html.Attempt is { } htmlRefusal)
            {
                return htmlRefusal;
            }

            var block = await Compose(blockPrepared, blockPart, request, sample, render, presentation, budgetToken, takeToken);
            if (block.Attempt is { } blockRefusal)
            {
                return blockRefusal;
            }

            var composeMs = html.ElapsedMs + block.ElapsedMs;
            var pageSettings = PdfPageSettingsRules.Resolve(blockManifest.PdfOutput);
            if (pageSettings.Settings is not { } page)
            {
                return FailedAttempt(new SampleFailure(
                    SampleFailureKind.Verification,
                    PackagePath.ManifestFileName,
                    Reason: VerificationReasons.PageSettingsInvalid));
            }

            var pageRule = PageRule.From(page);
            var frame = PageFrame.FromMillimetres(
                (double)page.WidthMm,
                (double)page.HeightMm,
                (double)page.MarginTopMm,
                (double)page.MarginRightMm,
                (double)page.MarginBottomMm,
                (double)page.MarginLeftMm);

            var text = new TextUnitExtractor().Extract(block.Block!);
            var source = new SourceCheckInput(
                new PartitionFacts(BlockCount: 1, sample.Records.Count),
                new IndexFacts(BlockIndex: 0, sample.Records.Count, FirstRecordOrdinal: 1),
                new BlockFacts(
                    BlockIndex: 0,
                    FirstRecordOrdinal: 1,
                    [.. sample.Records.Select(record => new BlockRecord(record.Ordinal, record.Values))],
                    sample.Header),
                block.Block!,
                text,
                blockManifest.Pdf.Verification,
                IsFirstBlock: true);

            SourceCheckOutcome sourceChecks;
            long sourceChecksMs;
            try
            {
                sourceChecks = _verifier.CheckSource(source, out sourceChecksMs);
            }
            catch (Exception exception)
            {
                var attempt = Classify(exception, part: null, takeToken);
                if (attempt is null)
                {
                    throw;
                }

                return attempt;
            }

            if (!sourceChecks.Passed)
            {
                return FailedAttempt(VerificationOf(
                    request, sourceChecks.Reason, sourceChecks.RecordOrdinal, sourceChecks.AnchorPath,
                    block.Block!, html.Block!, htmlManifest ?? blockManifest, blockManifest, null));
            }

            Directory.CreateDirectory(work);
            var pdfPath = Path.Combine(work, "sample-0.pdf");
            PrintOutcome print;
            try
            {
                print = await _printer
                    .PrintAsync(pageRule.AppendTo(block.Block!.Text), pageRule, request.Files, pdfPath, budgetToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                var attempt = Classify(exception, part: null, takeToken);
                if (attempt is null)
                {
                    throw;
                }

                return attempt;
            }

            VerificationResult verification;
            try
            {
                verification = _verifier.Verify(new PrintedCheckInput(pdfPath, 0, frame, block.Block!, text, sourceChecksMs));
            }
            catch (Exception exception)
            {
                var attempt = Classify(exception, part: null, takeToken);
                if (attempt is null)
                {
                    throw;
                }

                return attempt;
            }

            if (!verification.Passed)
            {
                Delete(pdfPath);
                return FailedAttempt(VerificationOf(
                    request, verification.Reason, verification.RecordOrdinal, verification.AnchorPath,
                    block.Block!, html.Block!, htmlManifest ?? blockManifest, blockManifest, verification.BlockIndex));
            }

            Keep(request, pdfPath);
            var pdfBytes = new FileInfo(pdfPath).Length;
            return new SampleAttempt.Rendered(new SampleFacts(
                verification.Pages,
                html.Block!.Html.LongLength,
                pdfBytes,
                verification.Words,
                composeMs,
                print.Duration.TotalMilliseconds,
                verification.ReadAndAlignMs + verification.SourceChecksMs,
                Convert.ToHexStringLower(SHA256.HashData(html.Block!.Html))));
        }
        finally
        {
            Delete(work);
        }
    }

    private async Task<Composed> Compose(
        PreparedTemplate prepared,
        PartRecordingAdapter part,
        SampleRequest request,
        SamplePartition sample,
        CompositionRender render,
        PresentationSettings presentation,
        CancellationToken budgetToken,
        CancellationToken takeToken)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            var block = await _composer.ComposeAsync(
                new CompositionRequest(
                    prepared,
                    sample.Block,
                    sample.Header,
                    sample.Records,
                    render,
                    request.Manifest.Partitioning?.GroupKey),
                part,
                presentation,
                budgetToken);
            return new Composed(block, Stopwatch.GetElapsedTime(started).TotalMilliseconds, null);
        }
        catch (Exception exception)
        {
            var attempt = Classify(exception, part, takeToken);
            if (attempt is null)
            {
                throw;
            }

            return new Composed(null, Stopwatch.GetElapsedTime(started).TotalMilliseconds, attempt);
        }
    }

    /// <summary>
    /// Classifies one failure at the boundary where the exception and the part are known,
    /// or answers <see langword="null"/> for a class this renderer cannot name.
    /// </summary>
    private static SampleAttempt? Classify(Exception exception, PartRecordingAdapter? part, CancellationToken takeToken)
    {
        var file = part?.LastFile;
        switch (exception)
        {
            case OperationCanceledException when !takeToken.IsCancellationRequested:
                return new SampleAttempt.Unavailable(SampleUnavailableCause.RenderTimeout, file);

            case OperationCanceledException:
                return null;

            case TemplateGuardException guard when string.Equals(guard.Code, TemplateErrorCodes.RenderTimeout, StringComparison.Ordinal):
                return new SampleAttempt.Unavailable(SampleUnavailableCause.RenderTimeout, file);

            case TemplateGuardException guard when guard.Limit is null or "render-output":
                return FailedAttempt(new SampleFailure(SampleFailureKind.GuardOutput, file, Limit: guard.Limit));

            case TemplateGuardException guard:
                return FailedAttempt(new SampleFailure(SampleFailureKind.EngineLimit, file, Limit: guard.Limit));

            case PresentationRefusedException:
                // A value or a format cannot be presented while the part renders: the template's own.
                return FailedAttempt(new SampleFailure(SampleFailureKind.EngineError, file));

            case PrintTemporarilyFailedException:
                return new SampleAttempt.Unavailable(SampleUnavailableCause.RenderCrash, file);

            case PlaywrightException:
                return new SampleAttempt.Unavailable(SampleUnavailableCause.RenderCrash, file);

            case TemplatePackageException:
                // The engine failed while rendering a part that parsed; its own message is never shown.
                return FailedAttempt(new SampleFailure(SampleFailureKind.EngineError, file));

            default:
                return null;
        }
    }

    private static SampleFailure VerificationOf(
        SampleRequest request,
        string? reason,
        long? recordOrdinal,
        string? anchorPath,
        ComposedBlock block,
        ComposedBlock document,
        RenderManifest documentManifest,
        RenderManifest blockManifest,
        int? blockIndex)
    {
        _ = block;
        if (request.EmptySample)
        {
            return new SampleFailure(
                SampleFailureKind.EmptySample,
                EmptyDocumentPart(document, documentManifest),
                Reason: reason,
                AnchorPath: anchorPath,
                BlockIndex: blockIndex,
                RecordOrdinal: recordOrdinal);
        }

        return new SampleFailure(
            SampleFailureKind.Verification,
            FileOf(reason, blockManifest),
            AnchorPath: anchorPath,
            BlockIndex: blockIndex,
            RecordOrdinal: recordOrdinal,
            Reason: reason);
    }

    /// <summary>The package-relative file a verification failure is attributed to, by the reason's class.</summary>
    private static string? FileOf(string? reason, RenderManifest manifest)
    {
        if (reason is "anchor-mismatch" or "anchors-undeclared")
        {
            // The anchors are declared in the manifest; the check names it there.
            return null;
        }

        var row = manifest.PathOf(TemplatePart.Row);
        if (reason is "blank-page" or "no-page" or "page-size" or "page-settings-invalid" or "page-accounting")
        {
            return row ?? manifest.Stylesheets.FirstOrDefault() ?? PackagePath.ManifestFileName;
        }

        return row ?? PackagePath.ManifestFileName;
    }

    /// <summary>The document part whose output is empty in the zero-record composition.</summary>
    private static string? EmptyDocumentPart(ComposedBlock document, RenderManifest manifest)
    {
        var start = document.Fragments.FirstOrDefault(fragment => fragment.Kind == FragmentKind.DocumentStart);
        if (start is not null && string.IsNullOrWhiteSpace(document.TextOf(start)))
        {
            return manifest.PathOf(TemplatePart.DocumentStart);
        }

        var end = document.Fragments.FirstOrDefault(fragment => fragment.Kind == FragmentKind.DocumentEnd);
        if (end is not null && string.IsNullOrWhiteSpace(document.TextOf(end)))
        {
            return manifest.PathOf(TemplatePart.DocumentEnd);
        }

        return manifest.PathOf(TemplatePart.DocumentStart);
    }

    /// <summary>The manifest of the document-mode HTML, when it is not the block's own part set.</summary>
    private static RenderManifest? HtmlManifestOf(TemplateManifest declared, RenderManifest block)
    {
        var html = declared.Outputs?.Html?.Parts;
        if (html is null)
        {
            return null;
        }

        var parts = new Dictionary<TemplatePart, string>(5);
        Add(parts, TemplatePart.DocumentStart, html.DocumentStart);
        Add(parts, TemplatePart.GroupStart, html.GroupStart);
        Add(parts, TemplatePart.Row, html.Row);
        Add(parts, TemplatePart.GroupEnd, html.GroupEnd);
        Add(parts, TemplatePart.DocumentEnd, html.DocumentEnd);
        if (parts.Count == 0)
        {
            return null;
        }

        var same = parts.Count == block.Parts.Count
            && parts.All(entry => block.Parts.TryGetValue(entry.Key, out var path)
                && string.Equals(path, entry.Value, StringComparison.Ordinal));
        return same ? null : block with { Parts = parts };
    }

    private static void Add(Dictionary<TemplatePart, string> parts, TemplatePart part, string? path)
    {
        if (!string.IsNullOrEmpty(path))
        {
            parts[part] = path;
        }
    }

    private static PresentationSettings SettingsOf(TemplateManifest manifest, DateTimeOffset clock)
    {
        var locale = manifest.LocaleDefaults;
        if (locale is { Culture: { Length: > 0 } culture, TimeZone: { Length: > 0 } zone })
        {
            return PresentationSettingsResolver.Resolve(new PresentationValues(culture, zone, clock));
        }

        // No declared locale: the invariant culture and UTC, with the take's own start instant.
        return new PresentationSettings(CultureInfo.InvariantCulture, TimeZoneInfo.Utc, clock.ToUniversalTime());
    }

    private PreparedTemplate Prepare(string key, RenderManifest manifest, ITemplateEngineAdapter adapter, ITemplatePackageFiles files) =>
        _prepared.GetOrAdd(key, () => adapter.Prepare(manifest, files));

    private static string CacheKeyOf(SampleRequest request, ITemplateEngineAdapter adapter, RenderManifest manifest)
    {
        var package = request.SourceDescriptorSha256
            ?? request.TemplateId + "/" + request.Version;
        var parts = string.Join(
            ';',
            manifest.Parts.OrderBy(entry => entry.Key).Select(entry => TemplatePartNames.Of(entry.Key) + "=" + entry.Value));
        return package + "/" + adapter.Engine + "/" + adapter.Version + "/" + parts;
    }

    private static string EngineNameOf(TemplateManifest manifest) =>
        manifest.Engine is { } engine ? TemplateEngineNames.ToManifestName(engine) : "undeclared";

    private static SampleAttempt FailedAttempt(SampleFailure failure) => new SampleAttempt.Failed(failure);

    /// <summary>Keeps the verified sample's PDF where the demo named a directory, and only there.</summary>
    private static void Keep(SampleRequest request, string pdfPath)
    {
        if (request.EmptySample || request.OutputDirectory is not { Length: > 0 } directory)
        {
            return;
        }

        Directory.CreateDirectory(directory);
        File.Copy(pdfPath, Path.Combine(directory, request.TemplateId + "-" + request.Version + ".pdf"), overwrite: true);
    }

    private static void Delete(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
            // A leftover work area costs disk before it costs correctness.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>One composition: the block it produced, how long it took, or the refusal it ended with.</summary>
    private sealed record Composed(ComposedBlock? Block, double ElapsedMs, SampleAttempt? Attempt);
}
