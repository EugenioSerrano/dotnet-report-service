using System.Globalization;
using System.Text;

namespace ReportService.PdfOracle;

/// <summary>One line of the content area: words of one page with the same vertical centre.</summary>
/// <param name="Page">The page it printed on.</param>
/// <param name="Words">Its words, left to right.</param>
internal sealed record ContentLine(int Page, IReadOnlyList<PdfWord> Words)
{
    /// <summary>Its words as one text.</summary>
    public string Text => string.Join(' ', Words.Select(word => word.Text));
}

/// <summary>One detail row as it printed: the line holding its key and every line it continued on.</summary>
internal sealed class ItemRow(string key, int? number, int page)
{
    /// <summary>The value of the row key column the row printed.</summary>
    public string Key { get; } = key;

    /// <summary>The key as a record number, when it is one.</summary>
    public int? Number { get; } = number;

    /// <summary>The page the row began on.</summary>
    public int AnchorPage { get; } = page;

    /// <summary>Its words but the key, each with the column it printed in.</summary>
    public List<(string Column, PdfWord Word)> Words { get; } = [];

    /// <summary>The words of its key, printed where the row begins.</summary>
    public List<PdfWord> NumberWords { get; } = [];

    /// <summary>The pages it printed on.</summary>
    public SortedSet<int> Pages { get; } = [page];
}

/// <summary>A printed document placed on the page the package declares.</summary>
internal sealed class PlacedDocument
{
    /// <summary>Every word of the header band, by page (index = page − 1).</summary>
    public required IReadOnlyList<IReadOnlyList<PdfWord>> HeaderBand { get; init; }

    /// <summary>Every word of the footer band, by page (index = page − 1).</summary>
    public required IReadOnlyList<IReadOnlyList<PdfWord>> FooterBand { get; init; }

    /// <summary>The content area's lines, in reading order: page, then vertical position, then horizontal position.</summary>
    public required IReadOnlyList<ContentLine> Lines { get; init; }

    /// <summary>The words whose box crosses the content area's top or bottom edge.</summary>
    public required IReadOnlyList<PdfWord> Crossing { get; init; }
}

/// <summary>
/// Places the words of a printed document on the page its package declares: the
/// header band above the top margin, the footer band below the bottom margin, the
/// content area between them, and the content words grouped into lines by their
/// vertical centre and read left to right. The order the text tool reports is not
/// used: the reading is the boxes'.
/// </summary>
internal static class Placement
{
    /// <summary>How far two words' vertical centres may differ and still be one line.</summary>
    public const double LineTolerancePt = 2.0;

    /// <summary>Places <paramref name="extraction"/> on the page the package declares.</summary>
    public static PlacedDocument Place(PdfExtraction extraction, ExpectationDocument expectation)
    {
        ArgumentNullException.ThrowIfNull(extraction);
        ArgumentNullException.ThrowIfNull(expectation);

        var top = expectation.ContentTopPt;
        var bottom = expectation.ContentBottomPt;
        var header = Enumerable.Range(0, extraction.Pages).Select(_ => new List<PdfWord>()).ToList();
        var footer = Enumerable.Range(0, extraction.Pages).Select(_ => new List<PdfWord>()).ToList();
        var content = new List<PdfWord>();
        var crossing = new List<PdfWord>();

        foreach (var word in extraction.Words)
        {
            var page = word.Page - 1;
            if (page < 0 || page >= extraction.Pages)
            {
                throw new OracleInstrumentException($"A word is reported on page {word.Page.ToString(CultureInfo.InvariantCulture)}, outside the document.");
            }

            if ((word.YMin < top && word.YMax > top) || (word.YMin < bottom && word.YMax > bottom))
            {
                crossing.Add(word);
            }

            if (word.YCentre < top)
            {
                header[page].Add(word);
            }
            else if (word.YCentre > bottom)
            {
                footer[page].Add(word);
            }
            else
            {
                content.Add(word);
            }
        }

        content.Sort(static (left, right) => left.Page != right.Page
            ? left.Page.CompareTo(right.Page)
            : left.YCentre != right.YCentre
                ? left.YCentre.CompareTo(right.YCentre)
                : left.XMin.CompareTo(right.XMin));

        var lines = new List<ContentLine>();
        List<PdfWord>? current = null;
        var anchor = 0.0;
        foreach (var word in content)
        {
            if (current is null || word.Page != current[0].Page || word.YCentre - anchor > LineTolerancePt)
            {
                current = [];
                anchor = word.YCentre;
                lines.Add(new ContentLine(word.Page, current));
            }

            current.Add(word);
        }

        // A line's words are read left to right: the order above is by page, then by
        // vertical centre, then by horizontal position.
        foreach (var band in header.Concat(footer))
        {
            band.Sort(static (left, right) => left.XMin.CompareTo(right.XMin));
        }

        return new PlacedDocument
        {
            HeaderBand = header,
            FooterBand = footer,
            Lines = lines,
            Crossing = crossing,
        };
    }

    /// <summary>The column a word printed in, by the centre of its box.</summary>
    public static string ColumnOf(PdfWord word, ExpectationDocument expectation)
    {
        ArgumentNullException.ThrowIfNull(word);
        ArgumentNullException.ThrowIfNull(expectation);

        foreach (var column in expectation.Columns)
        {
            if (word.XCentre < ExpectationDocument.Points.Of(column.EndMm))
            {
                return column.Column;
            }
        }

        return expectation.Columns[^1].Column;
    }
}

/// <summary>The content lines of a document, classified into its regions.</summary>
internal sealed class ClassifiedDocument
{
    /// <summary>The lines printed before the detail table: the document header.</summary>
    public required IReadOnlyList<ContentLine> Lead { get; init; }

    /// <summary>The pages whose column captions printed, in the order they printed.</summary>
    public required IReadOnlyList<int> HeadRowPages { get; init; }

    /// <summary>The detail rows, in the order they printed.</summary>
    public required IReadOnlyList<ItemRow> Rows { get; init; }

    /// <summary>The lines printed after the detail table.</summary>
    public required IReadOnlyList<ContentLine> Trail { get; init; }

    /// <summary>The content lines that belong to no region: a document that printed something else.</summary>
    public required IReadOnlyList<ContentLine> Unclassified { get; init; }
}

/// <summary>
/// Classifies the content lines of a printed document: the document header before
/// the column captions, one detail row per line that begins with a record number,
/// the continuation lines below a row's number, and the lines after the table.
/// </summary>
internal static class Classification
{
    /// <summary>Classifies <paramref name="placed"/> against the fixture it was printed from.</summary>
    public static ClassifiedDocument Classify(PlacedDocument placed, FixtureSource fixture, ExpectationDocument expectation)
    {
        ArgumentNullException.ThrowIfNull(placed);
        ArgumentNullException.ThrowIfNull(fixture);
        ArgumentNullException.ThrowIfNull(expectation);

        if (expectation.Grouped)
        {
            throw new OracleInstrumentException("The package begins groups; this judge reads documents without them.");
        }

        if (expectation.BlockTotal)
        {
            throw new OracleInstrumentException("The package prints a block total; this judge reads documents without one.");
        }

        var lines = placed.Lines;
        var headRowN = Normalisation.N(string.Join(' ', expectation.HeadRow));
        var trailStart = TrailStart(lines, fixture, expectation);

        var headIndex = FindIndex(lines, trailStart, line => Normalisation.N(line.Text) == headRowN);
        var firstRow = FindIndex(lines, trailStart, line => IsRowAnchor(line, expectation));
        var start = headIndex >= 0 ? headIndex : firstRow;

        var rows = new List<ItemRow>();
        var headRowPages = new List<int>();
        var unclassified = new List<(int Index, ContentLine Line)>();
        var lastTableLine = start - 1;
        if (start >= 0)
        {
            ItemRow? current = null;
            for (var index = start; index < trailStart; index++)
            {
                var line = lines[index];
                if (Normalisation.N(line.Text) == headRowN)
                {
                    headRowPages.Add(line.Page);
                    lastTableLine = index;
                    continue;
                }

                var keyWords = line.Words.Where(word => Placement.ColumnOf(word, expectation) == expectation.RowKey).ToList();
                if (keyWords.Count > 0)
                {
                    var key = string.Concat(keyWords.Select(word => word.Text));
                    if (IsRecordNumber(key))
                    {
                        current = new ItemRow(key, int.Parse(key, CultureInfo.InvariantCulture), line.Page);
                        current.NumberWords.AddRange(keyWords);
                        foreach (var word in keyWords)
                        {
                            current.Words.Add((expectation.RowKey, word));
                        }

                        rows.Add(current);
                        AddWords(current, line, keyWords, expectation);
                        lastTableLine = index;
                    }
                    else
                    {
                        current = null;
                        unclassified.Add((index, line));
                    }

                    continue;
                }

                if (current is not null)
                {
                    AddWords(current, line, [], expectation);
                    lastTableLine = index;
                }
                else
                {
                    unclassified.Add((index, line));
                }
            }
        }

        // A declaration that names no trailer still prints one: the lines after the last
        // detail row are the document's closing text, not lines that belong nowhere. They
        // are covered by the text the composition holds, not by a member this declaration
        // would have to name.
        var trail = expectation.Trail is not null
            ? lines.Skip(trailStart).ToList()
            : unclassified.Where(entry => entry.Index > lastTableLine).Select(entry => entry.Line).ToList();
        var orphans = unclassified
            .Where(entry => expectation.Trail is not null || entry.Index <= lastTableLine)
            .Select(entry => entry.Line)
            .ToList();

        return new ClassifiedDocument
        {
            Lead = start < 0 ? lines.Take(trailStart).ToList() : lines.Take(start).ToList(),
            HeadRowPages = headRowPages,
            Rows = rows,
            Trail = trail,
            Unclassified = orphans,
        };
    }

    /// <summary>Whether a line begins a detail row: its key column holds a record number.</summary>
    public static bool IsRowAnchor(ContentLine line, ExpectationDocument expectation)
    {
        ArgumentNullException.ThrowIfNull(line);
        ArgumentNullException.ThrowIfNull(expectation);

        var keyWords = line.Words.Where(word => Placement.ColumnOf(word, expectation) == expectation.RowKey).ToList();
        return keyWords.Count > 0 && IsRecordNumber(string.Concat(keyWords.Select(word => word.Text)));
    }

    private static bool IsRecordNumber(string text) =>
        text.Length is > 0 and < 9 && text.All(char.IsAsciiDigit);

    private static void AddWords(ItemRow row, ContentLine line, IReadOnlyCollection<PdfWord> skip, ExpectationDocument expectation)
    {
        row.Pages.Add(line.Page);
        foreach (var word in line.Words)
        {
            if (skip.Contains(word))
            {
                continue;
            }

            row.Words.Add((Placement.ColumnOf(word, expectation), word));
        }
    }

    private static int FindIndex(IReadOnlyList<ContentLine> lines, int end, Func<ContentLine, bool> predicate)
    {
        for (var index = 0; index < end; index++)
        {
            if (predicate(lines[index]))
            {
                return index;
            }
        }

        return -1;
    }

    /// <summary>
    /// Where the lines after the detail table begin. The table prints no end mark,
    /// so the region is the run of last lines whose normalised text is exactly the
    /// text the package prints there; a document that prints none of it leaves the
    /// region empty, and the check reports the missing text.
    /// </summary>
    private static int TrailStart(IReadOnlyList<ContentLine> lines, FixtureSource fixture, ExpectationDocument expectation)
    {
        if (expectation.Trail is null)
        {
            return lines.Count;
        }

        var expected = Normalisation.N(fixture.ValueOf(expectation.Trail));
        if (expected.Length == 0)
        {
            return lines.Count;
        }

        var suffix = new List<string>();
        var length = 0;
        for (var index = lines.Count - 1; index >= 0; index--)
        {
            var text = Normalisation.N(lines[index].Text);
            suffix.Add(text);
            length += text.Length;
            if (length == expected.Length)
            {
                suffix.Reverse();
                return string.Equals(string.Concat(suffix), expected, StringComparison.Ordinal) ? index : lines.Count;
            }

            if (length > expected.Length)
            {
                return lines.Count;
            }
        }

        return lines.Count;
    }
}
