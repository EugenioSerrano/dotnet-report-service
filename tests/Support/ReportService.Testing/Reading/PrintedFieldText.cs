using System.Text;
using ReportService.Pdf.Verification;

namespace ReportService.Testing.Reading;

/// <summary>The class of the first difference an independent reading reports.</summary>
public static class FieldDifference
{
    /// <summary>A character the source holds is absent from the printed text.</summary>
    public const string Missing = "missing-character";

    /// <summary>A character the printed text holds is absent from the source.</summary>
    public const string Extra = "extra-character";

    /// <summary>The first difference is neither an insertion nor a deletion.</summary>
    public const string Altered = "altered-character";

    /// <summary>The printed text holds the character a reader uses when it cannot map a glyph.</summary>
    public const string Replacement = "replacement-character";

    /// <summary>The printed text ends before the source is consumed.</summary>
    public const string Truncated = "truncated-tail";
}

/// <summary>What one independent reading found: a contiguous match, or the class and position of the first difference.</summary>
/// <param name="Passed">Whether the source appears as a contiguous run.</param>
/// <param name="Index">The position of the run in the normalised printed text, when it passed.</param>
/// <param name="Length">The length of the run in the normalised printed text, when it passed.</param>
/// <param name="FirstPage">The page the run starts on, when it passed.</param>
/// <param name="LastPage">The page the run ends on, when it passed.</param>
/// <param name="Difference">The class of the first difference, when it failed.</param>
/// <param name="Position">The position of the first difference in the normalised source, when it failed.</param>
/// <param name="Page">The page the first difference was read on, when it failed.</param>
public sealed record FieldMatch(
    bool Passed,
    int Index,
    int Length,
    int FirstPage,
    int LastPage,
    string? Difference,
    int Position,
    int Page)
{
    /// <summary>A reading that found the whole source, contiguous.</summary>
    public static FieldMatch Match(int index, int length, int firstPage, int lastPage) =>
        new(true, index, length, firstPage, lastPage, null, index, firstPage);

    /// <summary>A reading that did not find the source.</summary>
    public static FieldMatch Fail(string difference, int position, int page) =>
        new(false, 0, 0, 0, 0, difference, position, page);
}

/// <summary>
/// One independent reading of a printed document: the words of its content area in
/// painting order, the repeated table head dropped from every continuation page,
/// and the normalisation the product's verification uses. The comparison looks for
/// the source's normalised characters as one contiguous run; on failure it reports
/// the class and position of the first difference and never a character value.
/// </summary>
public sealed class PrintedFieldReading
{
    private readonly int[] pages;

    /// <summary>Reads the words of <paramref name="reading"/> into the field's sequence.</summary>
    /// <param name="reading">The document as the product reader read it.</param>
    /// <param name="headRow">The package's declared column captions, in order.</param>
    /// <param name="columnStartPt">The field column's left edge in points, or <see langword="null"/> for every column.</param>
    /// <param name="columnEndPt">The field column's right edge in points, or <see langword="null"/> for every column.</param>
    public PrintedFieldReading(
        PdfReading reading,
        IReadOnlyList<string> headRow,
        double? columnStartPt = null,
        double? columnEndPt = null)
    {
        ArgumentNullException.ThrowIfNull(reading);
        ArgumentNullException.ThrowIfNull(headRow);
        Pages = reading.Pages;

        var head = string.Concat(headRow.Select(Normalisation.Normalise));
        var builder = new StringBuilder();
        var charPages = new List<int>();
        foreach (var page in reading.Pages)
        {
            var words = page.Words.Where(word => word.Band == ReadingBand.Content).ToList();
            if (page.Number > 1 && head.Length > 0)
            {
                words = DropHead(words, head);
            }

            if (columnStartPt is { } start && columnEndPt is { } end)
            {
                words = words.Where(word =>
                {
                    var centre = (word.Left + word.Right) / 2d;
                    return centre >= start && centre <= end;
                }).ToList();
            }

            foreach (var word in words)
            {
                var text = Normalisation.Normalise(word.Text);
                builder.Append(text);
                for (var index = 0; index < text.Length; index++)
                {
                    charPages.Add(page.Number);
                }
            }
        }

        Text = builder.ToString();
        pages = [.. charPages];
    }

    /// <summary>The pages of the document as the reader returned them.</summary>
    public IReadOnlyList<PrintedPage> Pages { get; }

    /// <summary>The content text, each word normalised and concatenated in painting order.</summary>
    public string Text { get; }

    /// <summary>Finds <paramref name="source"/> as one contiguous run of the content text.</summary>
    /// <param name="source">The value the field was composed from.</param>
    public FieldMatch Find(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var expected = Normalisation.Normalise(source);
        if (expected.Length == 0)
        {
            return FieldMatch.Match(0, 0, 0, 0);
        }

        var found = Text.IndexOf(expected, StringComparison.Ordinal);
        if (found >= 0)
        {
            return FieldMatch.Match(found, expected.Length, PageAt(found), PageAt(found + expected.Length - 1));
        }

        var start = LocateStart(expected);
        if (start < 0)
        {
            return FieldMatch.Fail(FieldDifference.Missing, 0, PageAt(0));
        }

        var prefix = CommonPrefix(Text.AsSpan(start), expected.AsSpan());
        var position = prefix;
        if (start + prefix < Text.Length && Text[start + prefix] == Normalisation.ReplacementCharacter)
        {
            return FieldMatch.Fail(FieldDifference.Replacement, position, PageAt(start + prefix));
        }

        if (start + prefix >= Text.Length)
        {
            return FieldMatch.Fail(FieldDifference.Truncated, position, PageAt(Text.Length - 1));
        }

        if (TailMatches(Text, start + prefix, expected, prefix + 1))
        {
            return FieldMatch.Fail(FieldDifference.Missing, position, PageAt(start + prefix));
        }

        if (TailMatches(expected, prefix, Text, start + prefix + 1))
        {
            return FieldMatch.Fail(FieldDifference.Extra, position, PageAt(start + prefix));
        }

        return FieldMatch.Fail(FieldDifference.Altered, position, PageAt(start + prefix));
    }

    // The head a table repeats at the top of a continuation page: the page's first
    // content words whose normalised text is exactly the declared captions.
    private static List<PrintedWord> DropHead(List<PrintedWord> words, string head)
    {
        var length = 0;
        var count = 0;
        while (count < words.Count && length < head.Length)
        {
            length += Normalisation.Normalise(words[count].Text).Length;
            count++;
        }

        return length == head.Length
            && string.Concat(words.Take(count).Select(word => Normalisation.Normalise(word.Text))) == head
            ? words.Skip(count).ToList()
            : words;
    }

    // The source's start: the longest prefix of the source that the printed text
    // holds, tried from the longest window so an ordinary page's header words are
    // never mistaken for the field.
    private int LocateStart(string expected)
    {
        for (var length = Math.Min(64, expected.Length); length >= 1; length /= 2)
        {
            var index = Text.IndexOf(expected[..length], StringComparison.Ordinal);
            if (index >= 0)
            {
                return index;
            }

            if (length == 1)
            {
                break;
            }
        }

        return -1;
    }

    private static int CommonPrefix(ReadOnlySpan<char> left, ReadOnlySpan<char> right)
    {
        var length = Math.Min(left.Length, right.Length);
        var index = 0;
        while (index < length && left[index] == right[index])
        {
            index++;
        }

        return index;
    }

    private static bool TailMatches(string left, int leftStart, string right, int rightStart) =>
        left.AsSpan(leftStart).SequenceEqual(right.AsSpan(rightStart));

    private int PageAt(int index) => pages.Length == 0 ? 1 : pages[Math.Clamp(index, 0, pages.Length - 1)];
}

/// <summary>Reads a printed document and compares one field with the source it was composed from.</summary>
public static class PrintedFieldText
{
    /// <summary>Reads <paramref name="pdfPath"/> and prepares the field's sequence for a comparison.</summary>
    /// <param name="pdfPath">The printed document.</param>
    /// <param name="topPt">The template's top margin in points.</param>
    /// <param name="rightPt">The template's right margin in points.</param>
    /// <param name="bottomPt">The template's bottom margin in points.</param>
    /// <param name="leftPt">The template's left margin in points.</param>
    /// <param name="headRow">The package's declared column captions, in order.</param>
    /// <param name="columnStartPt">The field column's left edge in points, or <see langword="null"/> for every column.</param>
    /// <param name="columnEndPt">The field column's right edge in points, or <see langword="null"/> for every column.</param>
    public static PrintedFieldReading Read(
        string pdfPath,
        double topPt,
        double rightPt,
        double bottomPt,
        double leftPt,
        IReadOnlyList<string> headRow,
        double? columnStartPt = null,
        double? columnEndPt = null) =>
        new(PdfWordReader.Read(pdfPath, new PageMargins(topPt, rightPt, bottomPt, leftPt)), headRow, columnStartPt, columnEndPt);
}
