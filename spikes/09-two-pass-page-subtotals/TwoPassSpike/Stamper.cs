using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace TwoPassSpike;

/// <summary>
/// Draws the second pass's values into an existing page, appending to its
/// content: the footer (brought forward, page subtotal, carried forward, and
/// "(continues)" on the pages the span rule names) in the bottom margin, and in
/// strategy "stamped" each row's amount on the row's last line, right-aligned in
/// the amount band. Every stamp is wrapped in a saved and restored graphics state.
/// Coordinates are points from the page's top-left corner.
/// </summary>
internal static class Stamper
{
    public const string FontFamily = LiberationFontResolver.FamilyName;
    public const double FooterSizePt = 8;
    public const double AmountSizePt = 9;
    public const double FooterLine1Mm = 285;
    public const double FooterLine2Mm = 291;
    public const double FooterLeftMm = 12;
    public const double FooterCentreMm = 105;
    public const double FooterRightMm = 198;
    public const double AmountRightMm = 197;

    /// <summary>
    /// Text encoding of the stamp font: the library's automatic choice. The one
    /// allowed adjustment is Unicode, if a stamp word is not extractable.
    /// </summary>
    public static readonly PdfFontEncoding Encoding = PdfFontEncoding.Automatic;

    /// <summary>
    /// The one constant vertical offset (points) the stamp probe may fix for the
    /// amounts: the measured mean difference between the stamped amount and the
    /// row's last word. Zero unless the probe sets it.
    /// </summary>
    public const double AmountOffsetPt = 0.0;

    /// <summary>
    /// Liberation Sans line metrics (hhea ascender 1854 and descender −434 of
    /// 2048 units): the vertical centre of a word's box lies this many ems above
    /// its baseline, in a browser's inline box and in a PDF text extractor alike.
    /// </summary>
    public const double CentreAboveBaselineEm = (1854.0 - 434.0) / 2.0 / 2048.0;

    private static readonly Lazy<XFont> FooterFont = new(() => new XFont(FontFamily, FooterSizePt, XFontStyleEx.Regular, new XPdfFontOptions(Encoding)));
    private static readonly Lazy<XFont> AmountFont = new(() => new XFont(FontFamily, AmountSizePt, XFontStyleEx.Regular, new XPdfFontOptions(Encoding)));

    public static double MmToPt(double mm) => mm * 72.0 / 25.4;

    /// <summary>Footer texts of one page, as the accounting rules read them.</summary>
    public static (string Left, string Centre, string Right) FooterTexts(PageValues values) =>
        ($"Brought forward: {PageAccounting.Format(values.Brought)}",
         $"Page subtotal: {PageAccounting.Format(values.Subtotal)}",
         $"Carried forward: {PageAccounting.Format(values.Carried)}");

    /// <summary>The baseline that puts a word's box centre on <paramref name="centreY"/>.</summary>
    public static double BaselineFor(double centreY, double sizePt) => centreY + (CentreAboveBaselineEm * sizePt) + AmountOffsetPt;

    public static void StampPage(PdfPage page, PageValues values, IEnumerable<AmountStamp> amounts, IEnumerable<(double X, double Y, double Side)> squares)
    {
        using var gfx = XGraphics.FromPdfPage(page, XGraphicsPdfPageOptions.Append);
        var state = gfx.Save();
        var (left, centre, right) = FooterTexts(values);
        var footer = FooterFont.Value;
        var line1 = MmToPt(FooterLine1Mm);
        Draw(gfx, left, footer, MmToPt(FooterLeftMm), line1, Anchor.Left);
        Draw(gfx, centre, footer, MmToPt(FooterCentreMm), line1, Anchor.Centre);
        Draw(gfx, right, footer, MmToPt(FooterRightMm), line1, Anchor.Right);
        if (values.Continues)
        {
            Draw(gfx, PaginationFixtures.Captions.ContinuesMark, footer, MmToPt(FooterRightMm), MmToPt(FooterLine2Mm), Anchor.Right);
        }

        foreach (var amount in amounts)
        {
            Draw(gfx, amount.Text, AmountFont.Value, MmToPt(AmountRightMm), BaselineFor(amount.Line.YCentre, AmountSizePt), Anchor.Right);
        }

        foreach (var (x, y, side) in squares)
        {
            gfx.DrawRectangle(XBrushes.Black, x - (side / 2), y - (side / 2), side, side);
        }

        gfx.Restore(state);
    }

    private enum Anchor
    {
        Left,
        Centre,
        Right,
    }

    /// <summary>Draws one text on its baseline; the horizontal anchor is applied from the measured width.</summary>
    private static void Draw(XGraphics gfx, string text, XFont font, double x, double baseline, Anchor anchor)
    {
        var width = gfx.MeasureString(text, font).Width;
        var left = anchor switch
        {
            Anchor.Left => x,
            Anchor.Centre => x - (width / 2),
            _ => x - width,
        };
        gfx.DrawString(text, font, XBrushes.Black, left, baseline, XStringFormats.BaseLineLeft);
    }
}
