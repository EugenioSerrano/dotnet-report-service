using PaginationFixtures;

namespace TextIntegrityCheck;

/// <summary>A line of the content area: words of one page with the same vertical centre (±2 pt), left to right.</summary>
internal sealed class Line
{
    public required int Page { get; init; }

    public required List<PdfWord> Words { get; init; }

    public string Text => string.Join(' ', Words.Select(w => w.Text));
}

/// <summary>
/// A printed PDF as the checker reads it: every word placed in the header band,
/// the content area or the footer band by its box (declared geometry), and the
/// content words grouped into lines in the checker's own reading order (page,
/// then vertical position, then horizontal position) — poppler's flow order is not used.
/// </summary>
internal sealed class PrintedDocument
{
    public const double LineTolerancePt = 2.0;

    public required int Pages { get; init; }

    public required List<(double Width, double Height)> PageSizes { get; init; }

    public required List<Line> ContentLines { get; init; }

    /// <summary>Header-band words of each page (index = page − 1).</summary>
    public required List<List<PdfWord>> HeaderWords { get; init; }

    /// <summary>Footer-band words of each page (index = page − 1).</summary>
    public required List<List<PdfWord>> FooterWords { get; init; }

    /// <summary>Words whose box crosses the content area's top or bottom boundary.</summary>
    public required List<PdfWord> CrossingWords { get; init; }

    /// <summary>Ligature code points (U+FB00–U+FB06) in the raw extraction.</summary>
    public required int RawLigatures { get; init; }

    public static double TopPt => DeclaredGeometry.MmToPt(DeclaredGeometry.ContentTopMm);

    public static double BottomPt => DeclaredGeometry.MmToPt(DeclaredGeometry.ContentBottomMm);

    public static PrintedDocument From(PdfText text)
    {
        var top = TopPt;
        var bottom = BottomPt;
        var pageCount = Math.Max(text.Pages, text.PageSizes.Count);
        var header = Enumerable.Range(0, pageCount).Select(_ => new List<PdfWord>()).ToList();
        var footer = Enumerable.Range(0, pageCount).Select(_ => new List<PdfWord>()).ToList();
        var content = new List<PdfWord>();
        var crossing = new List<PdfWord>();
        var ligatures = 0;
        foreach (var word in text.Words)
        {
            ligatures += word.Text.Count(c => c >= 'ﬀ' && c <= 'ﬆ');
            if ((word.YMin < top && word.YMax > top) || (word.YMin < bottom && word.YMax > bottom))
            {
                crossing.Add(word);
            }

            if (word.YCentre < top)
            {
                header[word.Page - 1].Add(word);
            }
            else if (word.YCentre > bottom)
            {
                footer[word.Page - 1].Add(word);
            }
            else
            {
                content.Add(word);
            }
        }

        content.Sort((a, b) => a.Page != b.Page ? a.Page.CompareTo(b.Page) : a.YCentre != b.YCentre ? a.YCentre.CompareTo(b.YCentre) : a.XMin.CompareTo(b.XMin));
        var lines = new List<Line>();
        Line? current = null;
        double anchor = 0;
        foreach (var word in content)
        {
            if (current is null || word.Page != current.Page || word.YCentre - anchor > LineTolerancePt)
            {
                current = new Line { Page = word.Page, Words = [] };
                anchor = word.YCentre;
                lines.Add(current);
            }

            current.Words.Add(word);
        }

        foreach (var line in lines)
        {
            line.Words.Sort((a, b) => a.XMin.CompareTo(b.XMin));
        }

        foreach (var list in header.Concat(footer))
        {
            list.Sort((a, b) => a.XMin.CompareTo(b.XMin));
        }

        return new PrintedDocument
        {
            Pages = text.Pages,
            PageSizes = text.PageSizes,
            ContentLines = lines,
            HeaderWords = header,
            FooterWords = footer,
            CrossingWords = crossing,
            RawLigatures = ligatures,
        };
    }

    /// <summary>A copy whose line and word lists can be changed without touching this one.</summary>
    public PrintedDocument Clone() => new()
    {
        Pages = Pages,
        PageSizes = [.. PageSizes],
        ContentLines = [.. ContentLines.Select(l => new Line { Page = l.Page, Words = [.. l.Words] })],
        HeaderWords = [.. HeaderWords.Select(w => new List<PdfWord>(w))],
        FooterWords = [.. FooterWords.Select(w => new List<PdfWord>(w))],
        CrossingWords = [.. CrossingWords],
        RawLigatures = RawLigatures,
    };
}
