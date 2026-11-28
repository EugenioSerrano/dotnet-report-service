using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using ReportService.Data;
using ReportService.DataContract;
using ReportService.Templates;
using ReportService.TemplatePackages.Packages;
using ReportService.TemplateSource.Checks;

namespace ReportService.TemplateChecks.Sample;

/// <summary>
/// The take's thirteenth and last check: the version is printed once more, with a
/// record whose string field is exactly at the row bound, through the same port,
/// composer, printer and post-render verification a job uses, and the version is
/// taken only if every character of that record reached the page. A failure of the
/// verification refuses the version <c>TEMPLATE_INVALID</c> naming the part, the
/// position's path and the reason, and never a value; a render that could not run or
/// did not finish is retryable and never a refusal; and the verdict that the
/// contract admits the bound in no position is a pass, recorded with the cap that
/// applies to each position.
/// <para>
/// The check adds no comparison logic of its own: the reference of the printed text
/// is the delivered verification's, reached through the delivered
/// <see cref="ISampleRenderer"/>. A derivation that cannot produce a record the
/// contract admits - a position it cannot decide, a record larger than one block, or
/// a candidate the version's own schema refuses for another reason - is the
/// service's own limitation: the take fails without a refusal, and the version is
/// never refused for it.
/// </para>
/// <para>
/// Every decision is observable on the check's own structured log record, carrying
/// the check's name, the chosen position and its shape, the derivation, the record's
/// wire size, the positions skipped and the cap that skipped each, whether the value
/// is anchor-bound, the value's hash, the page count and the three timings - fields
/// and numbers only, never a value.
/// </para>
/// </summary>
public sealed class AtBoundSampleCheck(
    TemplateCheckSession session,
    ISampleRenderer renderer,
    ILogger<AtBoundSampleCheck>? logger = null)
    : ITemplateVersionCheck
{
    /// <summary>The check's name, as a log record says it.</summary>
    public const string CheckName = "at-bound-sample";

    /// <summary>The rule label this check's refusals and observations carry.</summary>
    public const string RuleId = "at-bound-sample";

    private const string SampleFile = "sample.json";

    private const string NotApplicableOutcome = "not-applicable";

    private const string AcceptedOutcome = "accepted";

    private const string RefusedOutcome = "refused";

    private readonly TemplateCheckSession _session = session ?? throw new ArgumentNullException(nameof(session));

    private readonly ISampleRenderer _renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));

    private readonly ILogger<AtBoundSampleCheck>? _logger = logger;

    /// <inheritdoc />
    public string Name => CheckName;

    /// <inheritdoc />
    public async Task<TemplateCheckResult> CheckAsync(TemplateCandidate candidate, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        cancellationToken.ThrowIfCancellationRequested();

        if (candidate.Manifest.Outputs?.Pdf is null)
        {
            // The gate is about a PDF's printed page; a version with no PDF output has none
            // to judge, and the scope is recorded rather than invented.
            Observed(null, notApplicable: true, facts: null);
            return TemplateCheckResult.Pass();
        }

        var pdf = candidate.Manifest.Outputs!.Pdf!;
        var files = new FolderTemplatePackageFiles(candidate.PackageDirectory);
        var schemaPath = candidate.Manifest.DataContract?.Schema;
        if (schemaPath is null || !files.Exists(schemaPath))
        {
            throw new AtBoundDerivationException(
                "schema",
                "the package holds no schema the at-bound record could be derived from.");
        }

        var schemaBytes = ReadBytes(files, schemaPath);
        JsonNode schemaRoot;
        try
        {
            schemaRoot = JsonNode.Parse(Encoding.UTF8.GetString(schemaBytes))
                ?? throw new AtBoundDerivationException("schema", "the package's schema is not a document.");
        }
        catch (JsonException)
        {
            throw new AtBoundDerivationException("schema", "the package's schema is not a document.");
        }

        var contract = _session.Contract(candidate);
        var ownsContract = false;
        if (contract is null)
        {
            var compilation = DataContractCompiler.Compile(schemaBytes, candidate.SourceDescriptorSha256, schemaPath);
            contract = compilation.Contract
                ?? throw new AtBoundDerivationException("schema", "the package's schema cannot be compiled.");
            ownsContract = true;
        }

        try
        {
            if (!files.Exists(SampleFile))
            {
                throw new AtBoundDerivationException(
                    "sample",
                    "the package holds no sample the at-bound record could be derived from.");
            }

            var sampleBytes = ReadBytes(files, SampleFile);
            var anchors = pdf.Verification?.Anchors?.Item ?? [];
            var result = AtBoundSample.Build(new AtBoundSampleInput(
                candidate,
                contract,
                schemaRoot,
                sampleBytes,
                PdfRowBound.Bytes,
                anchors));

            if (!result.Applies)
            {
                Observed(result, notApplicable: true, facts: null);
                return TemplateCheckResult.Pass();
            }

            var request = new SampleRequest(
                candidate.TemplateId,
                candidate.Version,
                candidate.Manifest,
                files,
                result.Document!,
                candidate.SourceDescriptorSha256);

            var attempt = await _renderer.RenderAsync(request, cancellationToken).ConfigureAwait(false);
            switch (attempt)
            {
                case SampleAttempt.Unavailable unavailable:
                    throw new SampleRenderUnavailableException(unavailable.Cause, unavailable.Part);

                case SampleAttempt.Failed failed:
                    if (failed.Failure.Kind is SampleFailureKind.Unreadable
                        or SampleFailureKind.EmptySample
                        or SampleFailureKind.OverBound)
                    {
                        throw new AtBoundDerivationException(
                            "render",
                            "the renderer refused the at-bound snapshot for a reason of the derivation's own making ("
                            + failed.Failure.Kind + ").");
                    }

                    var refusal = Refusal(failed.Failure, result.Chosen, candidate);
                    Refused(failed.Failure, result.Chosen, refusal.Reason);
                    return refusal;

                case SampleAttempt.Rendered rendered:
                    Observed(result, notApplicable: false, rendered.Facts);
                    return TemplateCheckResult.Pass();

                default:
                    throw new AtBoundDerivationException("render", "the renderer answered a state this check does not know.");
            }
        }
        finally
        {
            if (ownsContract)
            {
                contract.Dispose();
            }
        }
    }

    private static TemplateCheckResult Refusal(
        SampleFailure failure,
        AtBoundPosition? position,
        TemplateCandidate candidate)
    {
        var path = position?.Path ?? "$";
        var file = IsAnchorReason(failure.Reason)
            ? PackagePath.ManifestFileName
            : failure.Part ?? RowFile(candidate) ?? PackagePath.ManifestFileName;
        var reason = failure.Reason;

        if (IsAnchorReason(reason))
        {
            return TemplateRefusal.Refuse(
                TemplateCheckCodes.TemplateInvalid,
                file,
                "the field declared as an anchor is not printed as it is at " + path + " (" + reason + ").",
                "Print the field as it is, or remove it from the anchor list.");
        }

        if (reason is "text-dropped" or "text-altered" or "text-extra")
        {
            return TemplateRefusal.Refuse(
                TemplateCheckCodes.TemplateInvalid,
                file,
                "the record at the row bound is not printed completely at " + path + " (" + reason + ").",
                "Print the field as it is, with every character, across as many pages as the row needs.");
        }

        if (reason is "blank-page" or "no-page" or "page-size" or "page-settings-invalid")
        {
            return TemplateRefusal.Refuse(
                TemplateCheckCodes.TemplateInvalid,
                file,
                "the page that prints the record at the row bound carries no content at " + path + " (" + reason + ").",
                "Correct the page settings or the parts so that every page carries its content.");
        }

        if (failure.Kind is SampleFailureKind.EngineError or SampleFailureKind.EngineLimit or SampleFailureKind.GuardOutput or SampleFailureKind.ImageSource)
        {
            return TemplateRefusal.Refuse(
                TemplateCheckCodes.TemplateInvalid,
                file,
                "the part could not render the record at the row bound at " + path + " (" + Token(failure.Kind) + ").",
                "Correct the template so that it renders the record at the row bound.");
        }

        return TemplateRefusal.Refuse(
            TemplateCheckCodes.TemplateInvalid,
            file,
            "the record at the row bound does not pass the printed page's check at " + path + " (" + (reason ?? "not-printed") + ").",
            "Correct the part the page prints from.");
    }

    private void Refused(SampleFailure failure, AtBoundPosition? position, string? reason)
    {
        if (_logger is null)
        {
            return;
        }

        var state = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["check"] = CheckName,
            ["code"] = TemplateCheckCodes.TemplateInvalid,
            ["rule"] = RuleId,
            ["outcome"] = RefusedOutcome,
            ["position"] = position?.Path ?? "$",
            ["shape"] = position?.Shape.ToString() ?? "(none)",
            ["reason"] = reason ?? "(none)",
        };
        if (failure.Part is { } part)
        {
            state["part"] = part;
        }

        _logger.Log(
            LogLevel.Warning,
            new EventId(1, "AtBoundRefused"),
            state,
            exception: null,
            static (values, _) => "checked=" + values["check"] + " outcome=" + values["outcome"]
                + " rule=" + values["rule"] + " position=" + values["position"]);
    }

    private void Observed(AtBoundSampleResult? result, bool notApplicable, SampleFacts? facts)
    {
        if (_logger is null)
        {
            return;
        }

        var state = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["check"] = CheckName,
            ["outcome"] = notApplicable ? NotApplicableOutcome : AcceptedOutcome,
            ["position"] = result?.Chosen?.Path ?? "(none)",
        };

        if (result is null)
        {
            state["scope"] = "no-pdf-output";
        }
        else
        {
            state["shape"] = result.Chosen?.Shape.ToString() ?? "(none)";
            state["derivation"] = result.Derivation.ToString();
            state["recordBytes"] = result.RecordBytes;
            state["anchorBound"] = result.AnchorBound;
            state["valueSha256"] = result.ValueSha256.Length == 0 ? "(none)" : result.ValueSha256;
            state["skipped"] = result.Skipped.Count;
            state["caps"] = SkipsOf(result);
            if (result.NotApplicable is { } notApplicableReason)
            {
                state["notApplicable"] = notApplicableReason;
            }
        }

        if (facts is not null)
        {
            state["pages"] = facts.Pages;
            state["composeMs"] = facts.ComposeMs;
            state["printMs"] = facts.PrintMs;
            state["verifyMs"] = facts.VerifyMs;
        }

        _logger.Log(
            LogLevel.Information,
            new EventId(2, "AtBoundObserved"),
            state,
            exception: null,
            static (values, _) => "checked=" + values["check"] + " outcome=" + values["outcome"]
                + " position=" + values["position"]);
    }

    private static string SkipsOf(AtBoundSampleResult result) =>
        result.Skipped.Count == 0
            ? "(none)"
            : string.Join(
                "; ",
                result.Skipped.Select(position => position.Path + "=" + (position.Cap ?? position.Undecided ?? "undecided")));

    private static byte[] ReadBytes(ITemplatePackageFiles files, string path)
    {
        using var stream = files.OpenRead(path);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static string? RowFile(TemplateCandidate candidate) =>
        candidate.Manifest.Outputs?.Pdf?.Parts?.Row ?? candidate.Manifest.Outputs?.Html?.Parts?.Row;

    private static bool IsAnchorReason(string? reason) =>
        reason is "anchor-mismatch" or "anchors-undeclared";

    private static string Token(SampleFailureKind kind) => kind switch
    {
        SampleFailureKind.EngineError => "engine-error",
        SampleFailureKind.EngineLimit => "engine-limit",
        SampleFailureKind.GuardOutput => "output-limit",
        SampleFailureKind.ImageSource => "image-source",
        _ => "not-printed",
    };
}
