namespace ReportService.PdfOracle;

/// <summary>One printed word, as the text tool reports it.</summary>
/// <param name="Page">The page it printed on, from 1.</param>
/// <param name="Text">The characters it printed.</param>
/// <param name="XMin">The left edge of its box, in points from the page's left.</param>
/// <param name="YMin">The top edge of its box, in points from the page's top.</param>
/// <param name="XMax">The right edge of its box.</param>
/// <param name="YMax">The bottom edge of its box.</param>
internal sealed record PdfWord(int Page, string Text, double XMin, double YMin, double XMax, double YMax)
{
    /// <summary>The horizontal centre of the box.</summary>
    public double XCentre => (XMin + XMax) / 2.0;

    /// <summary>The vertical centre of the box.</summary>
    public double YCentre => (YMin + YMax) / 2.0;
}

/// <summary>A printed document as the text tool reports it: its pages, their sizes and every word.</summary>
/// <param name="Pages">How many pages the reading covers.</param>
/// <param name="ReportedPages">How many pages the file alone says it has.</param>
/// <param name="PageSizes">The width and height of each page, in points, in page order.</param>
/// <param name="Words">Every word, in the order the tool reports it.</param>
internal sealed class PdfExtraction(
    int pages,
    int reportedPages,
    IReadOnlyList<(double Width, double Height)> pageSizes,
    IReadOnlyList<PdfWord> words)
{
    /// <summary>How many pages the text reading covers.</summary>
    public int Pages { get; } = pages;

    /// <summary>How many pages the file's own information reports.</summary>
    public int ReportedPages { get; } = reportedPages;

    /// <summary>The width and height of each page, in points, in page order.</summary>
    public IReadOnlyList<(double Width, double Height)> PageSizes { get; } = pageSizes;

    /// <summary>Every word, in the order the tool reports it.</summary>
    public IReadOnlyList<PdfWord> Words { get; } = words;

    /// <summary>The same document with another word list; page sizes and counts stay as they were read.</summary>
    public PdfExtraction WithWords(IReadOnlyList<PdfWord> words)
    {
        ArgumentNullException.ThrowIfNull(words);

        return new PdfExtraction(Pages, ReportedPages, PageSizes, words);
    }
}

/// <summary>The reading of a file failed: the file, the tool or its output could not be used.</summary>
public sealed class OracleInstrumentException(string message, Exception? innerException = null)
    : Exception(message, innerException);
