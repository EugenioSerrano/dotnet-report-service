using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace PdfAssemblySpike;

/// <summary>
/// Page geometry and fonts of the page numbers: DejaVu Sans, the numbering area
/// at the top centre of the page where only the page number is drawn, and the
/// bottom 15 mm band of the earlier experiment, which is only observed here.
/// </summary>
internal static class PageLayout
{
    public static readonly double PageWidth = XUnit.FromMillimeter(210).Point;
    public static readonly double PageHeight = XUnit.FromMillimeter(297).Point;
    public static readonly double FooterBandHeight = XUnit.FromMillimeter(15).Point;
    public static readonly double ContentMargin = XUnit.FromMillimeter(5).Point;
    public static readonly double NumberBaselineFromBottom = XUnit.FromMillimeter(10).Point;

    /// <summary>Numbering area, measured from the top-left corner of the page: x from 70 mm to 140 mm.</summary>
    public static readonly double NumberingAreaLeft = XUnit.FromMillimeter(70).Point;

    public static readonly double NumberingAreaRight = XUnit.FromMillimeter(140).Point;

    /// <summary>Numbering area, measured from the top edge of the page: y from 4 mm to 14 mm.</summary>
    public static readonly double NumberingAreaTop = XUnit.FromMillimeter(4).Point;

    public static readonly double NumberingAreaBottom = XUnit.FromMillimeter(14).Point;

    /// <summary>Baseline of the page number, 10 mm below the top edge of the page.</summary>
    public static readonly double NumberBaselineFromTop = XUnit.FromMillimeter(10).Point;

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
