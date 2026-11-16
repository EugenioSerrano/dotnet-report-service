namespace ReportService.Pdf.Verification;

/// <summary>
/// What the page-geometry check found: whether every page is the declared page, and
/// when one is not, its number and the size that was read against the size the
/// declaration expected. Sizes are layout facts, never data.
/// </summary>
/// <param name="Passed">Whether every page is the declared page.</param>
/// <param name="Page">The first page that is not, from one.</param>
/// <param name="MeasuredWidthPt">The measured page's width in points.</param>
/// <param name="MeasuredHeightPt">The measured page's height in points.</param>
/// <param name="ExpectedWidthPt">The declared page's width in points.</param>
/// <param name="ExpectedHeightPt">The declared page's height in points.</param>
public sealed record PageGeometryOutcome(
    bool Passed,
    int? Page,
    double? MeasuredWidthPt,
    double? MeasuredHeightPt,
    double? ExpectedWidthPt,
    double? ExpectedHeightPt)
{
    /// <summary>Every page is the declared page.</summary>
    public static PageGeometryOutcome Pass { get; } = new(true, null, null, null, null, null);
}

/// <summary>
/// Compares every printed page's size with the page the template declared, within one
/// point on each side, at the size the page is displayed at - its media box with the
/// rotation applied, which the reader reports as the page's width and height. A
/// document with no page at all has no geometry to compare: that failure belongs to
/// the page check, which runs first.
/// </summary>
public sealed class PageGeometryCheck
{
    /// <summary>How far a printed page's side may be from the declared one, in points.</summary>
    public const double TolerancePt = 1.0;

    /// <summary>Checks every page of <paramref name="reading"/> against <paramref name="frame"/>.</summary>
    /// <param name="reading">The document as the reader read it.</param>
    /// <param name="frame">The page the template declared.</param>
    public PageGeometryOutcome Check(PdfReading reading, PageFrame frame)
    {
        ArgumentNullException.ThrowIfNull(reading);
        ArgumentNullException.ThrowIfNull(frame);

        foreach (var page in reading.Pages)
        {
            if (Math.Abs(page.Width - frame.WidthPt) > TolerancePt || Math.Abs(page.Height - frame.HeightPt) > TolerancePt)
            {
                return new PageGeometryOutcome(false, page.Number, page.Width, page.Height, frame.WidthPt, frame.HeightPt);
            }
        }

        return PageGeometryOutcome.Pass;
    }
}
