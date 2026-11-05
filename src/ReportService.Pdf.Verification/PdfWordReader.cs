using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace ReportService.Pdf.Verification;

/// <summary>Which band of the page a word was read in: the header, the content area or the footer.</summary>
public enum ReadingBand
{
    /// <summary>Inside the template's declared margins: the content area.</summary>
    Content,

    /// <summary>Above the top margin.</summary>
    Header,

    /// <summary>Below the bottom margin.</summary>
    Footer,
}

/// <summary>
/// One word of a printed page, in the frame the checks compare in: points, origin
/// top left, y downwards. The content index is the position of the word's first
/// letter in the page's own painting order, which is what makes the reading follow
/// the order the document was painted in rather than the order of its boxes.
/// </summary>
/// <param name="Text">The word as the reader extracted it.</param>
/// <param name="Left">The distance of its left edge from the page's left edge.</param>
/// <param name="Top">The distance of its top edge from the page's top edge.</param>
/// <param name="Right">The distance of its right edge from the page's left edge.</param>
/// <param name="Bottom">The distance of its bottom edge from the page's top edge.</param>
/// <param name="ContentIndex">The index of its first letter in the page's letters.</param>
/// <param name="Band">The band of the page it falls in.</param>
public sealed record PrintedWord(
    string Text,
    double Left,
    double Top,
    double Right,
    double Bottom,
    int ContentIndex,
    ReadingBand Band)
{
    /// <summary>The vertical centre of the word.</summary>
    public double VerticalCentre => (Top + Bottom) / 2;
}

/// <summary>One page of a printed document: its size and the words read from it, in painting order.</summary>
/// <param name="Number">The page's number, from one.</param>
/// <param name="Width">The page's width in points.</param>
/// <param name="Height">The page's height in points.</param>
/// <param name="Words">The words, in the order the page paints them.</param>
public sealed record PrintedPage(int Number, double Width, double Height, IReadOnlyList<PrintedWord> Words);

/// <summary>A printed document as it was read: its pages, each with its words in painting order.</summary>
/// <param name="Pages">The pages, in order.</param>
public sealed record PdfReading(IReadOnlyList<PrintedPage> Pages)
{
    /// <summary>How many words the document holds.</summary>
    public int WordCount => Pages.Sum(page => page.Words.Count);
}

/// <summary>A document the reader could not open or read, which fails the document it belongs to.</summary>
public sealed class PdfUnreadableException : Exception
{
    /// <summary>The document could not be read as a document.</summary>
    public PdfUnreadableException()
        : base("the printed document could not be read")
    {
    }

    /// <summary>The document could not be read as a document; <paramref name="innerException"/> says why.</summary>
    /// <param name="innerException">The cause, for the log.</param>
    public PdfUnreadableException(Exception innerException)
        : base("the printed document could not be read", innerException)
    {
    }
}

/// <summary>
/// Reads a printed document: every page's words with their boxes, converted to the
/// frame the checks compare in, and each word's position in the page's painting
/// order. It opens the document for reading and closes it when the reading ends; a
/// document it cannot open or read is a failure of the document, never an empty
/// reading, because an empty reading would pass a check it cannot answer.
/// </summary>
public static class PdfWordReader
{
    /// <summary>Reads the document at <paramref name="path"/>.</summary>
    /// <param name="path">The file of the printed document.</param>
    /// <param name="margins">The template's declared margins, in points.</param>
    /// <returns>The pages of the document, each with its words.</returns>
    /// <exception cref="PdfUnreadableException">The document cannot be opened or read.</exception>
    public static PdfReading Read(string path, PageMargins margins)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(margins);

        try
        {
            using var document = PdfDocument.Open(path);
            var pages = new List<PrintedPage>(document.NumberOfPages);
            for (var number = 1; number <= document.NumberOfPages; number++)
            {
                pages.Add(ReadPage(document.GetPage(number), number, margins));
            }

            return new PdfReading(pages);
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
        {
            throw new PdfUnreadableException(exception);
        }
    }

    private static PrintedPage ReadPage(Page page, int number, PageMargins margins)
    {
        var letters = page.Letters;
        var positions = new Dictionary<Letter, int>(LetterIdentity.Instance);
        for (var index = 0; index < letters.Count; index++)
        {
            positions[letters[index]] = index;
        }

        var words = new List<(PrintedWord Word, int Position)>();
        foreach (var word in page.GetWords())
        {
            if (string.IsNullOrEmpty(word.Text))
            {
                continue;
            }

            var box = word.BoundingBox;
            var top = page.Height - box.Top;
            var bottom = page.Height - box.Bottom;
            words.Add((
                new PrintedWord(
                    word.Text,
                    box.Left,
                    top,
                    box.Right,
                    bottom,
                    FirstLetterPosition(word.Letters, positions),
                    PlacementConvention.BandOf(top, bottom, page.Height, margins)),
                words.Count));
        }

        // A word the page's letters do not hold keeps the end of the reading, so the
        // order is always total and never depends on a geometry the document lacks.
        var ordered = words
            .OrderBy(pair => pair.Word.ContentIndex)
            .ThenBy(pair => pair.Position)
            .Select(pair => pair.Word)
            .ToList();
        return new PrintedPage(number, page.Width, page.Height, ordered);
    }

    private static int FirstLetterPosition(IReadOnlyList<Letter> wordLetters, Dictionary<Letter, int> pagePositions)
    {
        var lowest = int.MaxValue;
        foreach (var letter in wordLetters)
        {
            if (pagePositions.TryGetValue(letter, out var index) && index < lowest)
            {
                lowest = index;
            }
        }

        return lowest;
    }

    /// <summary>
    /// The identity comparison of the reader's letters: the word hands out the page's
    /// own instances, so the position is found by reference and never by a value the
    /// reader might repeat.
    /// </summary>
    private sealed class LetterIdentity : IEqualityComparer<Letter>
    {
        /// <summary>The single instance of the comparison.</summary>
        public static LetterIdentity Instance { get; } = new();

        /// <inheritdoc />
        public bool Equals(Letter? x, Letter? y) => ReferenceEquals(x, y);

        /// <inheritdoc />
        public int GetHashCode(Letter obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
    }
}
