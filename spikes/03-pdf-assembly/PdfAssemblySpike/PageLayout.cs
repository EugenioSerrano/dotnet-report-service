using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace PdfAssemblySpike;

/// <summary>
/// Page geometry and fonts shared by the partial PDFs and the page numbers:
/// A4 portrait, DejaVu Sans, and a reserved empty footer band (the bottom
/// 15 mm) where only the page number is drawn.
/// </summary>
internal static class PageLayout
{
    public static readonly double PageWidth = XUnit.FromMillimeter(210).Point;
    public static readonly double PageHeight = XUnit.FromMillimeter(297).Point;
    public static readonly double FooterBandHeight = XUnit.FromMillimeter(15).Point;
    public static readonly double ContentMargin = XUnit.FromMillimeter(5).Point;
    public static readonly double NumberBaselineFromBottom = XUnit.FromMillimeter(10).Point;

    /// <summary>Top edge of the footer band, measured from the top of the page.</summary>
    public static double FooterBandTop => PageHeight - FooterBandHeight;

    /// <summary>Lowest point content may reach: 5 mm above the footer band.</summary>
    public static double ContentBottomLimit => FooterBandTop - ContentMargin;

    /// <summary>
    /// Text encoding of every font. The library's default is chosen
    /// automatically per font; text extraction is checked before measuring.
    /// </summary>
    public static readonly PdfFontEncoding Encoding = PdfFontEncoding.Automatic;

    private static readonly Lazy<XFont> HeadingFont = new(() => Create(14, XFontStyleEx.Bold));
    private static readonly Lazy<XFont> MarkerFont = new(() => Create(9, XFontStyleEx.Regular));
    private static readonly Lazy<XFont> BodyFont = new(() => Create(10, XFontStyleEx.Regular));
    private static readonly Lazy<XFont> TableFont = new(() => Create(8, XFontStyleEx.Regular));
    private static readonly Lazy<XFont> NumberFont = new(() => Create(8, XFontStyleEx.Regular));

    public static XFont Heading => HeadingFont.Value;

    public static XFont Marker => MarkerFont.Value;

    public static XFont Body => BodyFont.Value;

    public static XFont Table => TableFont.Value;

    public static XFont PageNumber => NumberFont.Value;

    private static XFont Create(double size, XFontStyleEx style) =>
        new(DejaVuFontResolver.FamilyName, size, style, new XPdfFontOptions(Encoding));
}
