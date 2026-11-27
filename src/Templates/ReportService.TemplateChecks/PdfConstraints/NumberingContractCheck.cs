using System.Globalization;
using System.Text.Json;
using ReportService.Pdf.Assembly;
using ReportService.TemplatePackages.Manifest;
using ReportService.TemplatePackages.Packages;
using ReportService.TemplateSource.Checks;

namespace ReportService.TemplateChecks.PdfConstraints;

/// <summary>
/// A version that declares the global page-numbering contract is checked here: the
/// area must lie inside the page's displayed frame with positive dimensions, the
/// font must be a package-relative file the package holds, the family must be
/// named, the size must be a positive number of points no larger than 72, the
/// wording must carry exactly one <c>{X}</c> and one <c>{Y}</c> and no other
/// placeholder, the culture must be a BCP 47 tag, and the stamp at the deployment's
/// configured page capacity must fit the area at the declared font and size. A package
/// that declares page totals must keep the numbering area out of the accounting
/// footer's band. A package that declares no contract passes: it is still taken,
/// and it is not assemblable.
/// </summary>
public sealed class NumberingContractCheck(TemplateCheckSession session, NumberingCapacity capacity) : ITemplateVersionCheck
{
    /// <summary>The rule identifier this refusal is counted under.</summary>
    public const string RuleId = TemplateRefusal.Rules.NumberingContract;

    /// <summary>The largest font size a numbering contract may declare, in points.</summary>
    public const decimal MaximumFontSizePt = 72m;

    private readonly TemplateCheckSession _session = session ?? throw new ArgumentNullException(nameof(session));
    private readonly NumberingCapacity _capacity = capacity ?? throw new ArgumentNullException(nameof(capacity));

    /// <inheritdoc />
    public string Name => "NumberingContractCheck";

    /// <inheritdoc />
    public Task<TemplateCheckResult> CheckAsync(TemplateCandidate candidate, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        cancellationToken.ThrowIfCancellationRequested();

        var numbering = candidate.Manifest.Outputs?.Pdf?.Numbering;
        if (numbering is null)
        {
            return Task.FromResult(TemplateCheckResult.Pass());
        }

        var view = _session.View(candidate);
        var page = PageOf(candidate);
        if (page is null)
        {
            return Task.FromResult(TemplateCheckResult.Pass());
        }

        return Task.FromResult(
            Area(numbering, page)
            ?? Font(numbering, view)
            ?? Wording(numbering)
            ?? Culture(numbering)
            ?? Fit(numbering, view, _capacity.MaximumPages)
            ?? FooterBand(numbering, page, candidate)
            ?? TemplateCheckResult.Pass());
    }

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

    private static TemplateCheckResult? Area(TemplatePdfNumbering numbering, PdfPageSettings page)
    {
        var area = numbering.Area;
        if (area?.LeftMm is not { } left
            || area.TopMm is not { } top
            || area.WidthMm is not { } width
            || area.HeightMm is not { } height)
        {
            return Refuse(
                "outputs.pdf.numbering.area does not declare leftMm, topMm, widthMm and heightMm.",
                "Declare all four dimensions in millimetres.");
        }

        if (left < 0m || top < 0m || width <= 0m || height <= 0m)
        {
            return Refuse(
                "outputs.pdf.numbering.area has a negative origin or a dimension that is not positive.",
                "Declare non-negative distances and positive dimensions.");
        }

        if (left + width > page.WidthMm || top + height > page.HeightMm)
        {
            return Refuse(
                "outputs.pdf.numbering.area does not lie inside the page.",
                "Move the area inside the declared paper and orientation.");
        }

        return null;
    }

    private static TemplateCheckResult? Font(TemplatePdfNumbering numbering, PackageView view)
    {
        var font = numbering.Font;
        if (font is null || string.IsNullOrWhiteSpace(font.Family))
        {
            return Refuse(
                "outputs.pdf.numbering.font does not name a family.",
                "Declare the packaged font's family.");
        }

        if (font.File is not { } file || !PackagePath.IsValid(file) || !view.Exists(file))
        {
            return Refuse(
                "outputs.pdf.numbering.font.file is not a package-relative file the package holds.",
                "Name the packaged font file with a package-relative path.");
        }

        if (font.SizePt is not { } size || size <= 0m || size > MaximumFontSizePt)
        {
            return Refuse(
                "outputs.pdf.numbering.font.sizePt is outside the range from 0 (excluded) to 72 points.",
                "Declare a positive size no larger than 72 points.");
        }

        return null;
    }

    private static TemplateCheckResult? Wording(TemplatePdfNumbering numbering)
    {
        if (!PageNumbering.IsWellFormedFormat(numbering.Format))
        {
            return Refuse(
                "outputs.pdf.numbering.format does not carry exactly one {X} and one {Y} and no other placeholder.",
                "Write the wording with one {X} and one {Y}.");
        }

        return null;
    }

    private static TemplateCheckResult? Culture(TemplatePdfNumbering numbering)
    {
        if (string.IsNullOrWhiteSpace(numbering.Culture) || !IsCulture(numbering.Culture))
        {
            return Refuse(
                "outputs.pdf.numbering.culture is not a BCP 47 culture tag.",
                "Declare a culture such as es-AR or en-US.");
        }

        return null;
    }

    private static bool IsCulture(string name)
    {
        if (name.Length > 64 || name.Any(character => char.IsWhiteSpace(character)))
        {
            return false;
        }

        try
        {
            _ = CultureInfo.GetCultureInfo(name);
            return true;
        }
        catch (CultureNotFoundException)
        {
            return false;
        }
    }

    private static TemplateCheckResult? Fit(TemplatePdfNumbering numbering, PackageView view, int pageCapacity)
    {
        var area = new NumberingArea(
            (double)numbering.Area!.LeftMm!.Value,
            (double)numbering.Area.TopMm!.Value,
            (double)numbering.Area.WidthMm!.Value,
            (double)numbering.Area.HeightMm!.Value);
        byte[] bytes;
        try
        {
            bytes = view.ReadBytes(numbering.Font!.File!);
        }
        catch (IOException)
        {
            return Refuse(
                "outputs.pdf.numbering.font.file cannot be read.",
                "Store the packaged font as a file the take can read.");
        }

        var font = new NumberingFont(
            numbering.Font.Family!,
            numbering.Font.File!,
            (double)numbering.Font.SizePt!.Value,
            bytes);
        var contract = new PageNumbering(area, font, numbering.Format!, numbering.Culture!);
        var fit = NumberingFit.Measure(contract, pageCapacity);
        if (fit.Fits)
        {
            return null;
        }

        return Refuse(
            fit.Reason == "font-unreadable"
                ? "outputs.pdf.numbering.font.file is not a readable TrueType font."
                : "outputs.pdf.numbering.format does not fit outputs.pdf.numbering.area at the declared font and size at the deployment's document capacity of "
                    + pageCapacity.ToString(CultureInfo.InvariantCulture) + " pages.",
            fit.Reason == "font-unreadable"
                ? "Store a readable TrueType font file."
                : "Enlarge the area, reduce the size or shorten the wording.");
    }

    private static TemplateCheckResult? FooterBand(TemplatePdfNumbering numbering, PdfPageSettings page, TemplateCandidate candidate)
    {
        if (candidate.Manifest.Outputs?.Pdf?.PageTotals is not { Enabled: true })
        {
            return null;
        }

        var top = (double)numbering.Area!.TopMm!.Value;
        var height = (double)numbering.Area.HeightMm!.Value;
        var band = (double)(page.HeightMm - page.MarginBottomMm);
        if (top < band && top + height > band)
        {
            return Refuse(
                "outputs.pdf.numbering.area intersects the accounting footer's band below the bottom margin.",
                "Move the numbering area above the footer's band.");
        }

        return null;
    }

    private static TemplateCheckResult Refuse(string problem, string fix) =>
        TemplateRefusal.Refuse(
            TemplateCheckCodes.TemplateInvalid,
            PackagePath.ManifestFileName,
            problem,
            fix);
}
