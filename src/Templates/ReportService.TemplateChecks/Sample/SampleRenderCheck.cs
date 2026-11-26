using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging;
using ReportService.ServiceDefaults;
using ReportService.TemplatePackages.Manifest;
using ReportService.TemplatePackages.Packages;
using ReportService.TemplateSource.Checks;
using ReportService.Templates;

namespace ReportService.TemplateChecks.Sample;

/// <summary>
/// The take's last check and its twelfth: the version is rendered as its author's
/// own sample declares it, to HTML and to PDF, and then once more with zero
/// records, before anything is written. It refuses only what a deterministic
/// property of the version decides - the engine raised, an engine limit was
/// reached, the caller-side guard stopped the output, the sample is larger than one
/// block, the sample does not follow the delivered format, the printed page does not
/// carry what the parts composed, or the empty report prints nothing - and it names
/// the package-relative file the author has to correct.
/// <para>
/// A render that could not run (a crash, a lost browser) or did not finish in time
/// is not one of those: <see cref="SampleAttempt.Unavailable"/> is raised as
/// <see cref="SampleRenderUnavailableException"/> and the take fails retryably, so
/// the service's own outage or a deadline never refuses the author's version.
/// </para>
/// <para>
/// The check holds no engine, no browser and no PDF reader: it reads the sample
/// through <see cref="ITemplatePackageFiles"/>, partitions it, and asks
/// <see cref="ISampleRenderer"/> for the two renders. The static <c>SampleCheck</c>
/// (check 3) already refused a sample that does not follow the data format or its
/// schema; the partition's read is the one a direct caller needs, and it is a
/// refusal in the delivered <c>sample-format</c> grammar, never an exception.
/// </para>
/// <para>
/// Every decision is observable on its own surfaces: one log record whose category
/// is this check's, carrying the check's name, the rule label, the package-relative
/// file and the diagnostic positions, and a <c>template.sample</c> span on the
/// process's own <c>ReportService</c> source. Neither ever carries a value, a
/// printed text, the engine's own message, a storage key, a URL or a host path;
/// the take's own event is not extended.
/// </para>
/// </summary>
public sealed class SampleRenderCheck(ISampleRenderer renderer, ILogger<SampleRenderCheck>? logger = null)
    : ITemplateVersionCheck
{
    /// <summary>The check's name, as a log record says it.</summary>
    public const string CheckName = "sample-render";

    /// <summary>The span one sample decision opens, on the delivered <c>ReportService</c> source.</summary>
    public const string SpanName = "template.sample";

    /// <summary>The outcome a refused sample is logged and spanned with.</summary>
    public const string RefusedOutcome = "refused";

    /// <summary>The outcome an accepted sample is logged and spanned with.</summary>
    public const string AcceptedOutcome = "accepted";

    private const string SampleFile = "sample.json";

    private static readonly ActivitySource Source = new(TelemetryDefaults.ActivitySourceName);

    private readonly ISampleRenderer _renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));

    private readonly ILogger<SampleRenderCheck>? _logger = logger;

    /// <inheritdoc />
    public string Name => CheckName;

    /// <inheritdoc />
    public async Task<TemplateCheckResult> CheckAsync(TemplateCandidate candidate, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        var engine = EngineOf(candidate);
        var files = new FolderTemplatePackageFiles(candidate.PackageDirectory);
        if (!files.Exists(SampleFile))
        {
            // Exactly this case is the missing-sample refusal; a package that holds the
            // sample but cannot be read is the package's own failure, not this sentence.
            var missing = TemplateRefusal.Refuse(
                TemplateCheckCodes.TemplateInvalid,
                SampleFile,
                "the sample is missing.",
                "Add the sample the take renders.");
            Refused(engine, TemplateRefusal.Rules.SampleMissing, SampleFile, failure: null);
            return missing;
        }

        var sample = ReadSample(files);
        var read = await SamplePartition.ReadAsync(sample, cancellationToken).ConfigureAwait(false);
        if (read is SampleRead.OverBound overBound)
        {
            return Refusal(
                new SampleFailure(
                    SampleFailureKind.OverBound,
                    SampleFile,
                    Records: overBound.Records,
                    RecordBytes: overBound.RecordBytes,
                    Bound: overBound.Bound),
                candidate,
                engine);
        }

        if (read is SampleRead.Unreadable unreadable)
        {
            return Refusal(
                new SampleFailure(SampleFailureKind.Unreadable, SampleFile, Reason: unreadable.Reason),
                candidate,
                engine);
        }

        var request = new SampleRequest(
            candidate.TemplateId,
            candidate.Version,
            candidate.Manifest,
            files,
            sample,
            candidate.SourceDescriptorSha256);

        var ordinary = await RenderAsync(request, cancellationToken).ConfigureAwait(false);
        if (ordinary is SampleAttempt.Failed failed)
        {
            return Refusal(failed.Failure, candidate, engine);
        }

        var empty = await RenderAsync(request with { EmptySample = true }, cancellationToken).ConfigureAwait(false);
        if (empty is SampleAttempt.Failed failedEmpty)
        {
            return Refusal(failedEmpty.Failure, candidate, engine);
        }

        if (ordinary is SampleAttempt.Rendered rendered)
        {
            Accepted(engine, rendered.Facts);
        }

        return TemplateCheckResult.Pass();
    }

    /// <summary>The rule label a sample failure is recorded under.</summary>
    /// <param name="failure">The failure a render reported.</param>
    public static string RuleOf(SampleFailure failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        return TemplateRefusal.Rules.SampleOf(failure.Kind);
    }

    private async Task<SampleAttempt> RenderAsync(SampleRequest request, CancellationToken cancellationToken)
    {
        var attempt = await _renderer.RenderAsync(request, cancellationToken).ConfigureAwait(false);
        if (attempt is SampleAttempt.Unavailable unavailable)
        {
            throw new SampleRenderUnavailableException(unavailable.Cause, unavailable.Part);
        }

        return attempt;
    }

    private static ReadOnlyMemory<byte> ReadSample(ITemplatePackageFiles files)
    {
        using var stream = files.OpenRead(SampleFile);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static string EngineOf(TemplateCandidate candidate) =>
        candidate.Manifest.Engine is { } declared
            ? TemplateEngineNames.ToManifestName(declared)
            : "the declared engine";

    private TemplateCheckResult Refusal(SampleFailure failure, TemplateCandidate candidate, string engine)
    {
        var file = FileOf(failure, candidate);
        var result = Map(failure, engine, file);
        Refused(engine, RuleOf(failure), file, failure);
        return result;
    }

    private static TemplateCheckResult Map(SampleFailure failure, string engine, string file)
    {
        return failure.Kind switch
        {
            SampleFailureKind.EngineError => TemplateRefusal.Refuse(
                TemplateCheckCodes.TemplateInvalid,
                file,
                $"the part could not be rendered by {engine}.",
                "Correct the template so that it renders."),

            SampleFailureKind.EngineLimit => TemplateRefusal.Refuse(
                TemplateCheckCodes.TemplateInvalid,
                file,
                $"{engine}'s {failure.Limit ?? "limit"} was reached while rendering this part.",
                "Reduce the work the template does per record."),

            SampleFailureKind.GuardOutput => TemplateRefusal.Refuse(
                TemplateCheckCodes.TemplateInvalid,
                file,
                "the render produced more output than a template may produce (16 Mi characters per call, 32 MiB per block).",
                "Print less per record or per block."),

            SampleFailureKind.OverBound => TemplateRefusal.Refuse(
                TemplateCheckCodes.TemplateInvalid,
                file,
                "the sample holds " + Number(failure.Records ?? 0) + " records and " + Number(failure.RecordBytes ?? 0)
                    + " bytes of record data, where one block holds at most " + Number(ReportService.Data.DataContractLimits.BlockRecords)
                    + " records and 1 MiB.",
                "Reduce the sample to a single block's worth of records."),

            SampleFailureKind.Unreadable => TemplateRefusal.Refuse(
                TemplateCheckCodes.TemplateInvalid,
                file,
                "the sample does not follow the data format (" + (failure.Reason ?? "unreadable") + ").",
                "Correct the sample."),

            SampleFailureKind.EmptySample => TemplateRefusal.Refuse(
                TemplateCheckCodes.TemplateInvalid,
                file,
                "the sample cannot print a report with no records (" + (failure.Reason ?? "no-page") + ").",
                "Make the document parts render their header for zero records."),

            _ => VerificationRefusal(failure, file),
        };
    }

    private static TemplateCheckResult VerificationRefusal(SampleFailure failure, string file)
    {
        if (IsAnchorReason(failure.Reason))
        {
            var field = failure.AnchorPath is null ? string.Empty : failure.AnchorPath + " is";
            return TemplateRefusal.Refuse(
                TemplateCheckCodes.TemplateInvalid,
                file,
                field.Length == 0
                    ? "the package declares no field it prints as it is, so the page cannot be verified."
                    : field + " declared as an anchor but the template does not print it as it is.",
                "Print the field as it is, or remove it from the anchor list.");
        }

        if (string.Equals(failure.Reason, "row-without-text", StringComparison.Ordinal))
        {
            return TemplateRefusal.Refuse(
                TemplateCheckCodes.TemplateInvalid,
                file,
                "row " + Number(failure.RecordOrdinal ?? 0) + " of the sample prints no text.",
                "Correct the row part so that every row prints the values it carries.");
        }

        if (string.Equals(failure.Reason, "page-settings-invalid", StringComparison.Ordinal))
        {
            return TemplateRefusal.Refuse(
                TemplateCheckCodes.TemplateInvalid,
                file,
                "the page settings are missing or not usable.",
                "Declare outputs.pdf.paper, orientation and the four margins.");
        }

        if (string.Equals(failure.Reason, "accounting-footer-missing", StringComparison.Ordinal))
        {
            return TemplateRefusal.Refuse(
                TemplateCheckCodes.TemplateInvalid,
                file,
                "the sample printed no accounting footer on a page.",
                "Declare outputs.pdf.pageTotals.footer and keep it inside the bottom margin band.");
        }

        if (IsPageReason(failure.Reason))
        {
            return TemplateRefusal.Refuse(
                TemplateCheckCodes.TemplateInvalid,
                file,
                "the sample produced a page with no content.",
                "Correct the page settings or the parts so that every page carries its content.");
        }

        return TemplateRefusal.Refuse(
            TemplateCheckCodes.TemplateInvalid,
            file,
            "the printed page does not carry what the parts composed (" + failure.Reason + ").",
            "Correct the part the page prints from.");
    }

    /// <summary>The package-relative file a failure is attributed to, by the class the table names.</summary>
    private static string FileOf(SampleFailure failure, TemplateCandidate candidate)
    {
        if (failure.Kind == SampleFailureKind.Verification && IsAnchorReason(failure.Reason))
        {
            // The anchors are declared in the manifest, and that is the file to correct.
            return PackagePath.ManifestFileName;
        }

        if (!string.IsNullOrEmpty(failure.Part))
        {
            return failure.Part;
        }

        return RowFile(candidate) ?? PackagePath.ManifestFileName;
    }

    private static string? RowFile(TemplateCandidate candidate) =>
        candidate.Manifest.Outputs?.Pdf?.Parts?.Row ?? candidate.Manifest.Outputs?.Html?.Parts?.Row;

    /// <summary>The verification reasons that belong to the declared anchors.</summary>
    private static bool IsAnchorReason(string? reason) =>
        reason is not null
        && (string.Equals(reason, "anchor-mismatch", StringComparison.Ordinal)
            || string.Equals(reason, "anchors-undeclared", StringComparison.Ordinal));

    /// <summary>The verification reasons that are a page-level cause.</summary>
    private static bool IsPageReason(string? reason) =>
        reason is "blank-page" or "no-page" or "page-size" or "page-settings-invalid" or "layout-guard" or "unreadable";

    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>Writes the refusal's one log record and its span; both name positions, never a value.</summary>
    private void Refused(string engine, string rule, string file, SampleFailure? failure)
    {
        using var span = Source.StartActivity(SpanName);
        if (span is not null)
        {
            span.SetTag("engine", engine);
            span.SetTag("outcome", RefusedOutcome);
            span.SetTag("rule", rule);
            span.SetTag("file", file);
            if (failure?.Part is { } part)
            {
                span.SetTag("part", part);
            }

            if (failure?.AnchorPath is { } anchor)
            {
                span.SetTag("anchor", anchor);
            }

            if (failure?.BlockIndex is { } block)
            {
                span.SetTag("block", block);
            }

            if (failure?.RecordOrdinal is { } record)
            {
                span.SetTag("record", record);
            }
        }

        if (_logger is null)
        {
            return;
        }

        var state = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["check"] = CheckName,
            ["code"] = TemplateCheckCodes.TemplateInvalid,
            ["rule"] = rule,
            ["file"] = file,
            ["engine"] = engine,
            ["outcome"] = RefusedOutcome,
        };
        if (failure?.Part is { } logPart)
        {
            state["part"] = logPart;
        }

        if (failure?.AnchorPath is { } logAnchor)
        {
            state["anchor"] = logAnchor;
        }

        if (failure?.BlockIndex is { } logBlock)
        {
            state["block"] = logBlock;
        }

        if (failure?.RecordOrdinal is { } logRecord)
        {
            state["record"] = logRecord;
        }

        _logger.Log(
            LogLevel.Warning,
            new EventId(1, "SampleRefused"),
            state,
            exception: null,
            static (values, _) => "checked=" + values["check"] + " outcome=" + values["outcome"] + " rule=" + values["rule"]);
    }

    /// <summary>Writes the accepted sample's one log record and its span with the figures the take reads.</summary>
    private void Accepted(string engine, SampleFacts facts)
    {
        using var span = Source.StartActivity(SpanName);
        if (span is not null)
        {
            span.SetTag("engine", engine);
            span.SetTag("outcome", AcceptedOutcome);
            span.SetTag("pages", facts.Pages);
        }

        if (_logger is null)
        {
            return;
        }

        var state = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["check"] = CheckName,
            ["engine"] = engine,
            ["outcome"] = AcceptedOutcome,
            ["pages"] = facts.Pages,
            ["htmlBytes"] = facts.HtmlBytes,
            ["pdfBytes"] = facts.PdfBytes,
            ["words"] = facts.Words,
            ["composeMs"] = facts.ComposeMs,
            ["printMs"] = facts.PrintMs,
            ["verifyMs"] = facts.VerifyMs,
        };

        _logger.Log(
            LogLevel.Information,
            new EventId(2, "SampleAccepted"),
            state,
            exception: null,
            static (values, _) => "checked=" + values["check"] + " outcome=" + values["outcome"] + " pages=" + values["pages"]);
    }
}
