using System.Globalization;
using System.Text.Json;
using ReportService.Pdf.Assembly.PageCodes;
using ReportService.Pdf.Verification.PageCodes;
using ReportService.TemplatePackages.Manifest;
using ReportService.TemplatePackages.Packages;
using ReportService.TemplateSource.Checks;

namespace ReportService.TemplateChecks.PdfConstraints;

/// <summary>
/// A version that declares a per-page code is checked here, before its sample is
/// printed: the declaration's own rules (a well-formed generator name, options, an
/// area of 5 to 100 mm a side, a value template of the closed tokens, a compiling
/// linear-time pattern with a bounded match, a value of at most 128 bytes); the
/// area inside the printable box and clear of the numbering area and the accounting
/// footer; a generator of that name registered in the deployment that accepts the
/// options and the area; and that generator's worst-case content within 64 KiB and,
/// written as the assembly writes it, within 16 KiB stored. A package that declares
/// no code passes.
/// </summary>
public sealed class PageCodeDeclarationCheck(IEnumerable<PageCodeGeneratorRegistry> registries) : ITemplateVersionCheck
{
    /// <summary>The name the check is registered and ordered under.</summary>
    public const string CheckName = "PageCodeDeclarationCheck";

    private readonly PageCodeGeneratorRegistry _registry =
        registries?.FirstOrDefault() ?? new PageCodeGeneratorRegistry([]);

    /// <inheritdoc />
    public string Name => CheckName;

    /// <inheritdoc />
    public Task<TemplateCheckResult> CheckAsync(TemplateCandidate candidate, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        cancellationToken.ThrowIfCancellationRequested();
        var pageCode = candidate.Manifest.Outputs?.Pdf?.PageCode;
        if (pageCode is null)
        {
            return Task.FromResult(TemplateCheckResult.Pass());
        }

        var problems = new List<PageCodeProblem>();
        var declaration = PageCodeDeclaration.Create(PageCodeManifest.Raw(pageCode), problems);
        if (declaration is null)
        {
            var first = problems[0];
            return Task.FromResult(Refuse("outputs.pdf.pageCode." + first.Field + ": " + first.Problem + ".", Sentence(first.Fix)));
        }

        return Task.FromResult(
            Bound(declaration, candidate)
            ?? Geometry(declaration, candidate)
            ?? Generator(declaration)
            ?? TemplateCheckResult.Pass());
    }

    private static TemplateCheckResult? Bound(PageCodeDeclaration declaration, TemplateCandidate candidate)
    {
        var id = candidate.Manifest.Id ?? string.Empty;
        var version = candidate.Manifest.Version ?? string.Empty;
        var bound = declaration.Source.StaticBound(id, version);
        if (bound <= PageCodeSource.MaximumValueBytes)
        {
            return null;
        }

        return declaration.Source.UsesMatch
            ? Refuse(
                "outputs.pdf.pageCode.source can produce a value of " + Number(bound) + " bytes; the largest value is "
                    + Number(PageCodeSource.MaximumValueBytes) + " bytes.",
                "Declare source.match.maxLength of at most " + Number(declaration.Source.LargestMatchLength(id, version))
                    + ", or shorten the value template.")
            : Refuse(
                "outputs.pdf.pageCode.source.value can produce a value of " + Number(bound) + " bytes; the largest value is "
                    + Number(PageCodeSource.MaximumValueBytes) + " bytes.",
                "Shorten the value template.");
    }

    private static TemplateCheckResult? Geometry(PageCodeDeclaration declaration, TemplateCandidate candidate)
    {
        var page = PageOf(candidate);
        if (page is null)
        {
            return null;
        }

        var area = declaration.Area;
        var left = (double)page.MarginLeftMm;
        var top = (double)page.MarginTopMm;
        var right = (double)(page.WidthMm - page.MarginRightMm);
        var bottom = (double)(page.HeightMm - page.MarginBottomMm);
        if (area.LeftMm < left || area.TopMm < top
            || area.LeftMm + area.WidthMm > right || area.TopMm + area.HeightMm > bottom)
        {
            return Refuse(
                "outputs.pdf.pageCode.area does not lie inside the printable box (the page less its margins).",
                "Move or shrink the area so that it lies inside the margins.");
        }

        var pdf = candidate.Manifest.Outputs!.Pdf!;
        if (pdf.Numbering?.Area is { LeftMm: { } nLeft, TopMm: { } nTop, WidthMm: { } nWidth, HeightMm: { } nHeight }
            && Overlaps(area, (double)nLeft, (double)nTop, (double)nWidth, (double)nHeight))
        {
            return Refuse(
                "outputs.pdf.pageCode.area overlaps outputs.pdf.numbering.area.",
                "Keep the code and the page number in two separate areas.");
        }

        if (pdf.PageTotals is { Enabled: true, Footer: { LeftMm: { } fLeft, TopMm: { } fTop, WidthMm: { } fWidth, HeightMm: { } fHeight } }
            && Overlaps(area, (double)fLeft, (double)fTop, (double)fWidth, (double)fHeight))
        {
            return Refuse(
                "outputs.pdf.pageCode.area overlaps the accounting footer (outputs.pdf.pageTotals.footer).",
                "Move the area clear of the accounting footer.");
        }

        return null;
    }

    private TemplateCheckResult? Generator(PageCodeDeclaration declaration)
    {
        if (!_registry.TryResolve(declaration.Generator, out var registered))
        {
            var names = _registry.Names;
            return Refuse(
                "outputs.pdf.pageCode.generator names \"" + TemplateRefusal.Echo(declaration.Generator)
                    + "\", and no page-code generator of that name is registered in this deployment.",
                names.Count == 0
                    ? "Register a page-code generator in the deployment (PageCodes settings), or remove outputs.pdf.pageCode."
                    : "Name a registered generator (" + string.Join(", ", names) + "), or register one in the deployment.");
        }

        var check = registered.Generator.Validate(declaration.Options, declaration.Area.Size, PageCodeSource.MaximumValueBytes);
        if (!check.Accepted)
        {
            return Refuse(
                "outputs.pdf.pageCode: the generator \"" + declaration.Generator + "\" refuses the declaration: " + check.Problem + ".",
                Sentence(check.Fix!));
        }

        var worst = check.WorstCaseContent!;
        if (PageCodeContentValidator.Problem(worst) is { } problem)
        {
            return Refuse(
                "outputs.pdf.pageCode: the generator \"" + declaration.Generator + "\" can draw content the product does not accept ("
                    + problem + ").",
                "Use a generator that draws only paths and fills within " + Number(PageCodeContentValidator.MaximumContentBytes) + " bytes.");
        }

        var stored = PageCodeFormWriter.StoredSize(worst, declaration.Area.Size);
        if (stored > PageCodeContentValidator.MaximumStoredBytes)
        {
            return Refuse(
                "outputs.pdf.pageCode: the generator \"" + declaration.Generator + "\" can draw a code of " + Number(stored)
                    + " bytes as stored; the largest is " + Number(PageCodeContentValidator.MaximumStoredBytes) + " bytes.",
                "Use a smaller code or other generator options.");
        }

        return null;
    }

    private static bool Overlaps(PageCodeRectangle area, double left, double top, double width, double height) =>
        area.LeftMm < left + width && left < area.LeftMm + area.WidthMm
        && area.TopMm < top + height && top < area.TopMm + area.HeightMm;

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

    private static string Sentence(string text) =>
        text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..] + (text.EndsWith('.') ? string.Empty : ".");

    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static TemplateCheckResult Refuse(string problem, string fix) =>
        TemplateRefusal.Refuse(
            TemplateCheckCodes.TemplateInvalid,
            PackagePath.ManifestFileName,
            problem,
            fix);
}

/// <summary>Reads a manifest's per-page code into the declaration's raw values.</summary>
public static class PageCodeManifest
{
    /// <summary>The raw values of a declared per-page code.</summary>
    /// <param name="pageCode">The manifest's declaration.</param>
    /// <returns>The raw values.</returns>
    public static PageCodeDeclaration.Raw Raw(TemplatePageCode pageCode)
    {
        ArgumentNullException.ThrowIfNull(pageCode);
        return new PageCodeDeclaration.Raw(
            pageCode.Generator,
            pageCode.Options,
            (double?)pageCode.Area?.LeftMm,
            (double?)pageCode.Area?.TopMm,
            (double?)pageCode.Area?.WidthMm,
            (double?)pageCode.Area?.HeightMm,
            pageCode.Source?.Value,
            pageCode.Source?.Match?.Pattern,
            pageCode.Source?.Match?.MaxLength);
    }
}
