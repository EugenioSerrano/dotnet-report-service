namespace ReportService.TemplatePackages.Manifest;

/// <summary>
/// The page a PDF template declares: its paper, its orientation after which the width
/// and height are written, and its four margins. Every value is a decimal, so the
/// digits the manifest stores are the digits the page rule and the verification use.
/// </summary>
/// <param name="PaperName">The paper as the manifest names it.</param>
/// <param name="Orientation">The orientation the manifest declares.</param>
/// <param name="WidthMm">The page's width in millimetres, after the orientation.</param>
/// <param name="HeightMm">The page's height in millimetres, after the orientation.</param>
/// <param name="MarginTopMm">The top margin in millimetres.</param>
/// <param name="MarginRightMm">The right margin in millimetres.</param>
/// <param name="MarginBottomMm">The bottom margin in millimetres.</param>
/// <param name="MarginLeftMm">The left margin in millimetres.</param>
public sealed record PdfPageSettings(
    string PaperName,
    PageOrientation Orientation,
    decimal WidthMm,
    decimal HeightMm,
    decimal MarginTopMm,
    decimal MarginRightMm,
    decimal MarginBottomMm,
    decimal MarginLeftMm)
{
    /// <summary>The width of the page's content area in millimetres.</summary>
    public decimal PrintableWidthMm => WidthMm - MarginLeftMm - MarginRightMm;

    /// <summary>The height of the page's content area in millimetres.</summary>
    public decimal PrintableHeightMm => HeightMm - MarginTopMm - MarginBottomMm;

    /// <summary>The manifest word of the page's orientation.</summary>
    public string OrientationName => PageOrientations.Name(Orientation);
}
