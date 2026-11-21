using ReportService.TemplateSource.Checks;

namespace ReportService.TemplateChecks.PdfConstraints;

/// <summary>
/// A version that declares PDF output and turns the printed page totals on asks
/// for a capability this build does not have: the totals are not printed, and a
/// report with silently missing numbers is worse than a refusal. The declaration is
/// the manifest's own, so this check reads the typed view and nothing else — any
/// other shape of the member is refused by the structural rules before it runs.
/// </summary>
public sealed class PageAccountingCheck : ITemplateVersionCheck
{
    /// <summary>The rule identifier this refusal is counted under, shared with the take's log dimension.</summary>
    public const string RuleId = TemplateRefusal.Rules.PageAccounting;

    /// <summary>The word the render-side guard uses for the same declaration.</summary>
    public const string ReasonToken = "page-accounting";

    /// <inheritdoc />
    public string Name => "PageAccountingCheck";

    /// <inheritdoc />
    public Task<TemplateCheckResult> CheckAsync(TemplateCandidate candidate, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        cancellationToken.ThrowIfCancellationRequested();

        if (candidate.Manifest.Outputs?.Pdf?.PageTotals is { Enabled: true })
        {
            return Task.FromResult(TemplateRefusal.Refuse(
                TemplateCheckCodes.UnsupportedCapability,
                ReportService.TemplatePackages.Packages.PackagePath.ManifestFileName,
                "the template declares page subtotals (outputs.pdf.pageTotals), which this service does not print yet.",
                "Print the totals in the template or turn them off."));
        }

        return Task.FromResult(TemplateCheckResult.Pass());
    }
}
