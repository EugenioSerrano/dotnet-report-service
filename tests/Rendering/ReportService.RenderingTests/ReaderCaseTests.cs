using System.Globalization;
using System.Text;
using System.Text.Json;
using ReportService.Pdf.Verification;
using ReportService.Templates;

namespace ReportService.RenderingTests;

/// <summary>
/// A printed document kept as a reader case: the table document of the page
/// experiment, whose page five is the one placement the reader is known to group
/// differently. The document is read by the product's reader and held to the
/// reference reading the experiment committed beside it: the page count, every word
/// with its band, the diagnostic grouping of page five, and the thirty-eight words
/// that page groups one line lower than the reference reading. Any change in the
/// reader fails this case, which is the point: the reading it produces is the measure
/// every printed document is held to.
/// </summary>
public sealed class ReaderCaseTests
{
    /// <summary>The document's file, kept beside its reading.</summary>
    private const string Document = "s2-table-s2-T300u-split-t1.00-f0.90-on-r1.pdf";

    /// <summary>The SHA-256 of the document, as the experiment recorded it.</summary>
    private const string DocumentSha256 = "97c9f8491d3fc2bb9efd32aaace1bc3c686f2be9d58cf39edc013526f5627dcc";

    /// <summary>The SHA-256 of the committed reference reading, byte for byte.</summary>
    private const string OracleSha256 = "c84c23933af54615079b918da82c451772c9e1e88f3f62a1dbcf9174a81dc303";

    /// <summary>How many pages the document holds.</summary>
    private const int Pages = 16;

    /// <summary>How many words the document prints.</summary>
    private const int Words = 3857;

    /// <summary>How many words page five prints.</summary>
    private const int PageFiveWords = 58;

    /// <summary>How many words the product's reading groups one line lower than the reference reading.</summary>
    private const int LineOrdinalDifference = 38;

    /// <summary>The margins the document was printed with.</summary>
    private static PageMargins Margins => PlacementConvention.FromMillimetres(new PageMarginsMm(15, 12, 18, 12));

    private static string Folder => Path.Combine(AppContext.BaseDirectory, "ReaderCases");

    private static string DocumentPath => Path.Combine(Folder, Document);

    private static string OraclePath => Path.Combine(Folder, "T300u.oracle.json");

    [Fact]
    public void TheDocumentIsTheOneTheExperimentRecorded()
    {
        Assert.Equal(DocumentSha256, Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(DocumentPath))));
    }

    [Fact]
    public void TheOracleReadingIsTheOneTheExperimentProduced()
    {
        Assert.Equal(OracleSha256, Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(OraclePath))));
    }

    [Fact]
    public void TheReadingIsTheOneThatWasCommitted()
    {
        var reading = PdfWordReader.Read(DocumentPath, Margins);
        var expected = File.ReadAllText(Path.Combine(Folder, "T300u.words.tsv"), Encoding.UTF8);

        Assert.Equal(Pages, reading.Pages.Count);
        Assert.Equal(Words, reading.WordCount);
        Assert.Equal(expected, Describe(reading));
    }

    [Fact]
    public void ThreeReadingsOfTheDocumentAreIdentical()
    {
        var first = Describe(PdfWordReader.Read(DocumentPath, Margins));
        var second = Describe(PdfWordReader.Read(DocumentPath, Margins));
        var third = Describe(PdfWordReader.Read(DocumentPath, Margins));

        Assert.Equal(first, second);
        Assert.Equal(second, third);
    }

    [Fact]
    public void TheDiagnosticGroupingOfPageFiveIsTheOneThatWasCommitted()
    {
        var reading = PdfWordReader.Read(DocumentPath, Margins);
        var page = reading.Pages[4];
        var lines = PlacementConvention.Lines(page.Words);

        int LineOf(PrintedWord word)
        {
            for (var index = 0; index < lines.Count; index++)
            {
                foreach (var candidate in lines[index])
                {
                    if (ReferenceEquals(candidate, word))
                    {
                        return index;
                    }
                }
            }

            return -1;
        }

        var fingerprint = new StringBuilder();
        foreach (var word in page.Words)
        {
            fingerprint.Append(LineOf(word).ToString(CultureInfo.InvariantCulture)).Append(' ');
        }

        Assert.Equal(PageFiveWords, page.Words.Count);
        Assert.Equal(File.ReadAllText(Path.Combine(Folder, "T300u.page5-lines.txt"), Encoding.UTF8), fingerprint.ToString());
    }

    /// <summary>
    /// The line-ordinal difference the measure recorded for this document: page five of
    /// the product's reading groups one line lower than the reference reading for 38
    /// words, the first of them the mark a continued row begins with. The two readers'
    /// boxes are up to 1.83 pt apart vertically and are never compared as equal; only
    /// the line ordinal is compared.
    /// </summary>
    [Fact]
    public void PageFiveGroupsThirtyEightWordsOneLineLowerThanTheReferenceReading()
    {
        var reading = PdfWordReader.Read(DocumentPath, Margins);
        var oracle = OracleReading.Load(OraclePath);
        var reference = oracle.Words();
        var product = WordsOf(reading);

        Assert.Equal(Pages, oracle.Pages);
        Assert.Equal(Pages, reading.Pages.Count);
        Assert.Equal(Words, reference.Count);
        Assert.Equal(Words, product.Count);

        var matched = 0;
        var unpaired = 0;
        var bandMismatches = 0;
        var differing = new List<(int Page, string Text, int Reference, int Candidate)>();
        foreach (var page in Enumerable.Range(1, oracle.Pages))
        {
            var referenceWords = reference.Where(word => word.Page == page).ToList();
            var productWords = product.Where(word => word.Page == page).ToList();
            var referenceLines = LinesOf(referenceWords);
            var productLines = LinesOf(productWords);

            var pool = new Dictionary<string, List<Word>>(StringComparer.Ordinal);
            foreach (var word in productWords)
            {
                var key = Normalisation.Normalise(word.Text);
                if (key.Length == 0)
                {
                    continue;
                }

                if (!pool.TryGetValue(key, out var candidates))
                {
                    pool[key] = candidates = [];
                }

                candidates.Add(word);
            }

            // The reference reading is walked in the measure's order - its header band,
            // its content lines and its footer band - and every word pairs with the close
            // word of the same text, never with the word that happens to hold its position.
            foreach (var word in MeasureOrder(referenceWords, referenceLines))
            {
                var key = Normalisation.Normalise(word.Text);
                if (key.Length == 0)
                {
                    continue;
                }

                if (!pool.TryGetValue(key, out var candidates) || candidates.Count == 0)
                {
                    unpaired++;
                    continue;
                }

                var best = candidates.MinBy(candidate =>
                    Square(candidate.Left - word.Left) + Square(candidate.VerticalCentre - word.VerticalCentre))!;
                candidates.Remove(best);
                matched++;
                if (best.Band != word.Band)
                {
                    bandMismatches++;
                    continue;
                }

                if (!referenceLines.TryGetValue(word, out var referenceLine) || !productLines.TryGetValue(best, out var productLine))
                {
                    continue;
                }

                if (productLine == referenceLine + 1)
                {
                    differing.Add((page, word.Text, referenceLine, productLine));
                }
            }
        }

        Assert.Equal(0, unpaired);
        Assert.Equal(Words, matched);
        Assert.Equal(0, bandMismatches);
        Assert.Equal(LineOrdinalDifference, differing.Count);
        Assert.Equal((5, "(continues)", 0, 1), differing[0]);
        Assert.All(differing, entry => Assert.Equal(5, entry.Page));
    }

    private static double Square(double value) => value * value;

    private static List<Word> WordsOf(PdfReading reading) =>
    [
        .. reading.Pages.SelectMany(page => page.Words.Select(word =>
            new Word(page.Number, word.Text, word.Left, word.Top, word.Right, word.Bottom, word.Band))),
    ];

    /// <summary>
    /// The line each word of one page falls in: the page's content words, top to bottom,
    /// grouped at two points of vertical centre by the convention the diagnostics use.
    /// The ordinal is the line's position on its page, from zero.
    /// </summary>
    private static Dictionary<Word, int> LinesOf(IReadOnlyList<Word> pageWords)
    {
        var content = pageWords
            .Where(word => word.Band == ReadingBand.Content)
            .OrderBy(word => word.VerticalCentre)
            .ThenBy(word => word.Left)
            .ToList();
        var owner = new Dictionary<PrintedWord, Word>(ReferenceEqualityComparer.Instance);
        var printed = new List<PrintedWord>(content.Count);
        foreach (var word in content)
        {
            var candidate = new PrintedWord(word.Text, word.Left, word.Top, word.Right, word.Bottom, 0, ReadingBand.Content);
            printed.Add(candidate);
            owner[candidate] = word;
        }

        var lines = PlacementConvention.Lines(printed);
        var ordinals = new Dictionary<Word, int>(ReferenceEqualityComparer.Instance);
        for (var line = 0; line < lines.Count; line++)
        {
            foreach (var candidate in lines[line])
            {
                ordinals[owner[candidate]] = line;
            }
        }

        return ordinals;
    }

    /// <summary>The page's words in the order the measure walks them.</summary>
    private static IEnumerable<Word> MeasureOrder(IReadOnlyList<Word> pageWords, Dictionary<Word, int> lines)
    {
        foreach (var word in pageWords.Where(word => word.Band == ReadingBand.Header).OrderBy(word => word.Left))
        {
            yield return word;
        }

        foreach (var group in lines.GroupBy(entry => entry.Value).OrderBy(group => group.Key))
        {
            // The content lines are walked top to bottom, each left to right, as the
            // measure walks them.
            foreach (var entry in group.OrderBy(entry => entry.Key.Left))
            {
                yield return entry.Key;
            }
        }

        foreach (var word in pageWords.Where(word => word.Band == ReadingBand.Footer).OrderBy(word => word.Left))
        {
            yield return word;
        }
    }

    /// <summary>The reading as the committed file holds it: page, band and word, one per line.</summary>
    private static string Describe(PdfReading reading)
    {
        var builder = new StringBuilder();
        foreach (var page in reading.Pages)
        {
            foreach (var word in page.Words)
            {
                builder.Append(page.Number.ToString(CultureInfo.InvariantCulture))
                    .Append('\t')
                    .Append(word.Band)
                    .Append('\t')
                    .Append(word.Text)
                    .Append('\n');
            }
        }

        return builder.ToString();
    }

    /// <summary>One word of a reading, with the page it was read on.</summary>
    /// <param name="Page">The page, from one.</param>
    /// <param name="Text">The word as it was read.</param>
    /// <param name="Left">Its left edge, in points from the page's left edge.</param>
    /// <param name="Top">Its top edge, in points from the page's top edge.</param>
    /// <param name="Right">Its right edge, in points from the page's left edge.</param>
    /// <param name="Bottom">Its bottom edge, in points from the page's top edge.</param>
    /// <param name="Band">The band of the page it falls in.</param>
    private sealed class Word(int page, string text, double left, double top, double right, double bottom, ReadingBand band)
    {
        public int Page { get; } = page;

        public string Text { get; } = text;

        public double Left { get; } = left;

        public double Top { get; } = top;

        public double Right { get; } = right;

        public double Bottom { get; } = bottom;

        public ReadingBand Band { get; } = band;

        /// <summary>The vertical centre of the word.</summary>
        public double VerticalCentre => (Top + Bottom) / 2;
    }

    /// <summary>The committed reference reading of the document, in the experiment's own format.</summary>
    private sealed record OracleReading
    {
        public int Pages { get; init; }

        public List<double[]> PageSizes { get; init; } = [];

        public List<double[]> Boxes { get; init; } = [];

        public List<string> Texts { get; init; } = [];

        public static OracleReading Load(string path) =>
            JsonSerializer.Deserialize<OracleReading>(File.ReadAllText(path), new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })
            ?? throw new InvalidDataException($"The reading {path} is empty.");

        /// <summary>The words of the reading, on the pages they were read on.</summary>
        public List<Word> Words()
        {
            var margins = Margins;
            var words = new List<Word>(Texts.Count);
            for (var index = 0; index < Texts.Count; index++)
            {
                var box = Boxes[index];
                var top = box[2];
                var bottom = box[4];
                words.Add(new Word((int)box[0], Texts[index], box[1], top, box[3], bottom, BandOf(top, bottom, margins)));
            }

            return words;
        }

        /// <summary>The band of a word, from the declared margins the document was printed with.</summary>
        private static ReadingBand BandOf(double top, double bottom, PageMargins margins)
        {
            var centre = (top + bottom) / 2;
            var declaredHeight = PlacementConvention.FromMillimetres(new PageMarginsMm(297, 0, 0, 0)).Top;
            if (centre < margins.Top)
            {
                return ReadingBand.Header;
            }

            return centre > declaredHeight - margins.Bottom ? ReadingBand.Footer : ReadingBand.Content;
        }
    }
}
