namespace ReportService.Pdf.Verification;

/// <summary>
/// The page a printed document must have, in the points the reader measures: its
/// width and height and its four margins. The frame is built from the page settings
/// the template declared, so the bands the checks read and the page size they compare
/// come from one declaration. The verification holds no template type: the frame is
/// plain numbers.
/// </summary>
/// <param name="WidthPt">The page's width in points.</param>
/// <param name="HeightPt">The page's height in points.</param>
/// <param name="MarginTopPt">The distance from the page's top edge to the content area.</param>
/// <param name="MarginRightPt">The distance from the content area to the page's right edge.</param>
/// <param name="MarginBottomPt">The distance from the content area to the page's bottom edge.</param>
/// <param name="MarginLeftPt">The distance from the page's left edge to the content area.</param>
public sealed record PageFrame(
    double WidthPt,
    double HeightPt,
    double MarginTopPt,
    double MarginRightPt,
    double MarginBottomPt,
    double MarginLeftPt)
{
    /// <summary>How many points one millimetre is.</summary>
    public const double PointsPerMillimetre = 72d / 25.4d;

    /// <summary>No margin and no page: the frame of a document read without one.</summary>
    public static PageFrame None { get; } = new(0, 0, 0, 0, 0, 0);

    /// <summary>The frame of a page declared in millimetres.</summary>
    /// <param name="widthMm">The page's width in millimetres.</param>
    /// <param name="heightMm">The page's height in millimetres.</param>
    /// <param name="topMm">The top margin in millimetres.</param>
    /// <param name="rightMm">The right margin in millimetres.</param>
    /// <param name="bottomMm">The bottom margin in millimetres.</param>
    /// <param name="leftMm">The left margin in millimetres.</param>
    public static PageFrame FromMillimetres(
        double widthMm,
        double heightMm,
        double topMm,
        double rightMm,
        double bottomMm,
        double leftMm) =>
        new(
            widthMm * PointsPerMillimetre,
            heightMm * PointsPerMillimetre,
            topMm * PointsPerMillimetre,
            rightMm * PointsPerMillimetre,
            bottomMm * PointsPerMillimetre,
            leftMm * PointsPerMillimetre);

    /// <summary>The frame's margins, for the band a word falls in.</summary>
    public PageMargins Margins => new(MarginTopPt, MarginRightPt, MarginBottomPt, MarginLeftPt);
}
