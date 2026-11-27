using ReportService.Pdf.Verification;
using ReportService.Pdf.Verification.PageCodes;

namespace ReportService.Pdf.Assembly.PageCodes;

/// <summary>
/// The per-page code an assembly draws: the template's declaration, the deployment's
/// generator for it, the taken template's identity the source may read, the margins
/// the page text is read with, and the numbering area whose text the source never
/// reads.
/// </summary>
/// <param name="Declaration">The template's declaration.</param>
/// <param name="Generator">The registered generator the declaration names.</param>
/// <param name="TemplateId">The taken template's identifier.</param>
/// <param name="TemplateVersion">The taken template's version.</param>
/// <param name="Margins">The template's margins, in points.</param>
/// <param name="NumberingArea">The numbering area, when the template declares one.</param>
/// <param name="PrintableBox">The page less its margins, which the code's area must lie inside, when known.</param>
/// <param name="Footer">The accounting footer, which the code's area must not overlap, when the template declares one.</param>
public sealed record AssemblyPageCode(
    PageCodeDeclaration Declaration,
    RegisteredPageCodeGenerator Generator,
    string TemplateId,
    string TemplateVersion,
    PageMargins Margins,
    NumberingArea? NumberingArea,
    PageCodeText.Region? PrintableBox = null,
    PageCodeText.Region? Footer = null)
{
    /// <summary>The printable box of a page frame: the page less its margins.</summary>
    /// <param name="frame">The template's page frame.</param>
    /// <returns>The box, in points from the page's top-left corner.</returns>
    public static PageCodeText.Region PrintableBoxOf(PageFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        return new PageCodeText.Region(
            frame.MarginLeftPt,
            frame.MarginTopPt,
            frame.WidthPt - frame.MarginLeftPt - frame.MarginRightPt,
            frame.HeightPt - frame.MarginTopPt - frame.MarginBottomPt);
    }

    /// <summary>The numbering area as a text region, when declared.</summary>
    public PageCodeText.Region? NumberingRegion => NumberingArea is { } area
        ? new PageCodeText.Region(area.LeftPt, area.TopPt, area.WidthPt, area.HeightPt)
        : null;

    /// <summary>The code's area as a text region.</summary>
    public PageCodeText.Region CodeRegion => Declaration.Area.Region;

    /// <summary>The value one page's code carries, or <see langword="null"/> when the source yields none.</summary>
    /// <param name="page">The page's words.</param>
    /// <param name="pageNumber">The page's number.</param>
    /// <param name="pageTotal">The document's page count.</param>
    /// <returns>The value.</returns>
    public string? ValueFor(PrintedPage page, int pageNumber, int pageTotal) =>
        Declaration.Source.Evaluate(new PageCodeInputs(
            PageCodeText.Of(page, NumberingRegion, CodeRegion),
            pageNumber,
            pageTotal,
            TemplateId,
            TemplateVersion));

    /// <summary>
    /// The content of one page's code: the generator's content for the page's value,
    /// checked against the product's content rules; <see langword="null"/> with a
    /// reason when the page has no value or the content is refused.
    /// </summary>
    /// <param name="page">The page's words.</param>
    /// <param name="pageNumber">The page's number.</param>
    /// <param name="pageTotal">The document's page count.</param>
    /// <param name="reason">Why there is no content.</param>
    /// <returns>The content bytes.</returns>
    public byte[]? ContentFor(PrintedPage page, int pageNumber, int pageTotal, out string? reason)
    {
        var value = ValueFor(page, pageNumber, pageTotal);
        if (value is null)
        {
            reason = "the page code's source yields no value on a page";
            return null;
        }

        var generated = Generator.Generator.Generate(value, Declaration.Options, Declaration.Area.Size);
        if (generated.Content is not { } content)
        {
            reason = "the page code's generator refused a page's value";
            return null;
        }

        if (PageCodeContentValidator.Problem(content) is not null)
        {
            reason = "the page code's generator returned content that is not allowed";
            return null;
        }

        reason = null;
        return content;
    }
}
