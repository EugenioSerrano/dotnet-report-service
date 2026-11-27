using System.Text.Json;
using ReportService.TemplatePackages.Manifest;
using ReportService.TemplatePackages.Packages;
using ReportService.TemplateSource.Checks;

namespace ReportService.TemplateChecks.PdfConstraints;

/// <summary>
/// A version that prints page totals reserves the band the values are written into.
/// This check reads that declaration: the rectangle must be present, positive and
/// inside the bottom margin band of the declared page so it can never move a row or
/// overflow the content area; the three labels the template prints must be present;
/// the amount field and the scale the values are read and written at must be usable;
/// and the accounting band and a declared global-numbering area must be disjoint — a
/// stamped page number drawn over an accounting value is a wrong report. The rule
/// states the disjointness from the footer's side as well, so a package that declares
/// only the footer is still checked.
/// </summary>
public sealed class AccountingFooterCheck : ITemplateVersionCheck
{
    /// <summary>The rule identifier this refusal is counted under.</summary>
    public const string RuleId = TemplateRefusal.Rules.AccountingFooter;

    /// <summary>The most fraction digits a declared scale may carry.</summary>
    public const int MaximumScale = 9;

    /// <inheritdoc />
    public string Name => "AccountingFooterCheck";

    /// <inheritdoc />
    public Task<TemplateCheckResult> CheckAsync(TemplateCandidate candidate, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        cancellationToken.ThrowIfCancellationRequested();

        var manifest = candidate.Manifest;
        var totals = manifest.Outputs?.Pdf?.PageTotals;
        if (totals is not { Enabled: true })
        {
            return Task.FromResult(TemplateCheckResult.Pass());
        }

        var footer = totals.Footer;
        if (footer is null)
        {
            return Task.FromResult(Refuse(
                "outputs.pdf.pageTotals.footer is absent while the printed totals are enabled.",
                "Declare the band's rectangle and the three labels it prints."));
        }

        if (footer.LeftMm is not { } left
            || footer.TopMm is not { } top
            || footer.WidthMm is not { } width
            || footer.HeightMm is not { } height)
        {
            return Task.FromResult(Refuse(
                "outputs.pdf.pageTotals.footer does not declare leftMm, topMm, widthMm and heightMm.",
                "Declare all four dimensions in millimetres."));
        }

        if (left < 0m || top < 0m || width <= 0m || height <= 0m)
        {
            return Task.FromResult(Refuse(
                "outputs.pdf.pageTotals.footer has a negative origin or a dimension that is not positive.",
                "Declare non-negative distances and positive dimensions."));
        }

        var page = PageOf(candidate);
        if (page is not null)
        {
            var band = page.HeightMm - page.MarginBottomMm;
            if (top < band || top + height > page.HeightMm)
            {
                return Task.FromResult(Refuse(
                    "outputs.pdf.pageTotals.footer does not lie inside the bottom margin band of the declared page.",
                    "Move the band below the bottom margin and inside the page."));
            }

            if (left + width > page.WidthMm)
            {
                return Task.FromResult(Refuse(
                    "outputs.pdf.pageTotals.footer runs past the page's right edge.",
                    "Reduce the width or move the band left."));
            }
        }

        if (footer.Labels is not { } labels
            || string.IsNullOrWhiteSpace(labels.BroughtForward)
            || string.IsNullOrWhiteSpace(labels.PageSubtotal)
            || string.IsNullOrWhiteSpace(labels.CarriedForward))
        {
            return Task.FromResult(Refuse(
                "outputs.pdf.pageTotals.footer.labels does not declare all three labels.",
                "Declare broughtForward, pageSubtotal and carriedForward in the template's language."));
        }

        if (string.IsNullOrWhiteSpace(totals.AmountField))
        {
            return Task.FromResult(Refuse(
                "outputs.pdf.pageTotals.amountField does not name the record field the totals sum.",
                "Name the record field that carries the amount."));
        }

        if (totals.Scale is { } scale
            && (scale < 0m || scale > MaximumScale || scale != decimal.Truncate(scale)))
        {
            return Task.FromResult(Refuse(
                "outputs.pdf.pageTotals.scale is not a whole number of fraction digits between 0 and 9.",
                "Declare the scale as a whole number, such as 2."));
        }

        var numbering = manifest.Outputs?.Pdf?.Numbering;
        if (numbering?.Area is { } area
            && area.LeftMm is { } areaLeft
            && area.TopMm is { } areaTop
            && area.WidthMm is { } areaWidth
            && area.HeightMm is { } areaHeight
            && Overlaps(left, top, width, height, areaLeft, areaTop, areaWidth, areaHeight))
        {
            return Task.FromResult(Refuse(
                "outputs.pdf.pageTotals.footer overlaps the global page-numbering area.",
                "Move the accounting band and the numbering area apart."));
        }

        return Task.FromResult(TemplateCheckResult.Pass());
    }

    private static bool Overlaps(
        decimal left,
        decimal top,
        decimal width,
        decimal height,
        decimal otherLeft,
        decimal otherTop,
        decimal otherWidth,
        decimal otherHeight) =>
        left < otherLeft + otherWidth
        && otherLeft < left + width
        && top < otherTop + otherHeight
        && otherTop < top + height;

    private static PdfPageSettings? PageOf(TemplateCandidate candidate)
    {
        using var document = JsonDocument.Parse(candidate.ManifestJson);
        if (!document.RootElement.TryGetProperty("outputs", out var outputs)
            || outputs.ValueKind != JsonValueKind.Object
            || !outputs.TryGetProperty("pdf", out var pdf))
        {
            return null;
        }

        return PdfPageSettingsRules.Resolve(pdf).Settings;
    }

    private static TemplateCheckResult Refuse(string problem, string fix) =>
        TemplateRefusal.Refuse(
            TemplateCheckCodes.TemplateInvalid,
            PackagePath.ManifestFileName,
            problem,
            fix);
}
