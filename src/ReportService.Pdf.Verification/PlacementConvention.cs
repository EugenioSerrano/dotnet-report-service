using ReportService.Templates;

namespace ReportService.Pdf.Verification;

/// <summary>The template's declared margins, in points, in the frame the checks compare in.</summary>
/// <param name="Top">The distance from the top edge of the page to the content area.</param>
/// <param name="Right">The distance from the content area to the right edge.</param>
/// <param name="Bottom">The distance from the content area to the bottom edge.</param>
/// <param name="Left">The distance from the left edge of the page to the content area.</param>
public sealed record PageMargins(double Top, double Right, double Bottom, double Left)
{
    /// <summary>No margin: every word of the page is content.</summary>
    public static PageMargins None { get; } = new(0, 0, 0, 0);
}

/// <summary>
/// Where a word sits on its page, and which words belong together: the band a word
/// falls in is decided by its vertical centre against the template's declared
/// margins, so a page's header and footer are excluded from the content checks
/// without the reader having to know how the document marks them.
/// </summary>
public static class PlacementConvention
{
    /// <summary>How many points one millimetre is; the template declares its margins in millimetres.</summary>
    public const double PointsPerMillimetre = 72d / 25.4d;

    /// <summary>The vertical distance within which two words are read on one line.</summary>
    public const double LineTolerancePoints = 2d;

    /// <summary>The template's margins, converted to points.</summary>
    /// <param name="margins">The declared margins, in millimetres.</param>
    public static PageMargins FromMillimetres(PageMarginsMm margins)
    {
        ArgumentNullException.ThrowIfNull(margins);
        return new PageMargins(
            margins.Top * PointsPerMillimetre,
            margins.Right * PointsPerMillimetre,
            margins.Bottom * PointsPerMillimetre,
            margins.Left * PointsPerMillimetre);
    }

    /// <summary>The band a word falls in, from its vertical extent and the page's height.</summary>
    /// <param name="top">The distance of the word's top edge from the page's top edge.</param>
    /// <param name="bottom">The distance of the word's bottom edge from the page's top edge.</param>
    /// <param name="pageHeight">The height of the page.</param>
    /// <param name="margins">The template's margins, in points.</param>
    public static ReadingBand BandOf(double top, double bottom, double pageHeight, PageMargins margins)
    {
        ArgumentNullException.ThrowIfNull(margins);
        var centre = (top + bottom) / 2d;
        if (centre < margins.Top)
        {
            return ReadingBand.Header;
        }

        return centre > pageHeight - margins.Bottom ? ReadingBand.Footer : ReadingBand.Content;
    }

    /// <summary>
    /// The normalised text of a page's content words, in the order the page painted
    /// them. The header and footer bands are excluded: the page's own elements are
    /// not part of the block this check holds the document to.
    /// </summary>
    /// <param name="page">The page as it was read.</param>
    public static string ContentText(PrintedPage page)
    {
        ArgumentNullException.ThrowIfNull(page);
        return string.Concat(page.Words
            .Where(word => word.Band == ReadingBand.Content)
            .Select(word => Normalisation.Normalise(word.Text)));
    }

    /// <summary>
    /// The words of a page grouped into lines, top to bottom, for the diagnostics
    /// and the reader's regression case. No verdict depends on a line: a document's
    /// correctness is decided in painting order, so two readings that group lines
    /// differently still reach the same answer.
    /// </summary>
    /// <param name="words">The words of one page.</param>
    /// <returns>The lines, each with its words in painting order, from the top of the page.</returns>
    public static IReadOnlyList<IReadOnlyList<PrintedWord>> Lines(IReadOnlyList<PrintedWord> words)
    {
        ArgumentNullException.ThrowIfNull(words);
        var lines = new List<(double Centre, List<PrintedWord> Words)>();
        foreach (var word in words)
        {
            var line = lines.FindIndex(candidate => Math.Abs(candidate.Centre - word.VerticalCentre) <= LineTolerancePoints);
            if (line < 0)
            {
                lines.Add((word.VerticalCentre, [word]));
                continue;
            }

            lines[line].Words.Add(word);
        }

        return lines
            .OrderBy(candidate => candidate.Centre)
            .Select(candidate => (IReadOnlyList<PrintedWord>)candidate.Words)
            .ToList();
    }
}
