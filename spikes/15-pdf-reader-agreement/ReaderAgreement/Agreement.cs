using PaginationFixtures;
using TextIntegrityCheck;

namespace ReaderAgreement;

/// <summary>Distribution of absolute differences, in points.</summary>
internal sealed record Spread(int Count, double Max, double P99, double Median)
{
    public static Spread Of(List<double> values)
    {
        if (values.Count == 0)
        {
            return new Spread(0, 0, 0, 0);
        }

        values.Sort();
        double At(double q) => values[Math.Min(values.Count - 1, (int)Math.Ceiling(q * values.Count) - 1 < 0 ? 0 : (int)Math.Ceiling(q * values.Count) - 1)];
        return new Spread(values.Count, Math.Round(values[^1], 4), Math.Round(At(0.99), 4), Math.Round(At(0.5), 4));
    }
}

/// <summary>Agreement of one reader's reading of one PDF with the reference reading (measure fixed before any run).</summary>
internal sealed class AgreementResult
{
    public string Pdf { get; set; } = string.Empty;

    public string Configuration { get; set; } = string.Empty;

    public int ReferencePages { get; set; }

    public int CandidatePages { get; set; }

    public bool PagesEqual { get; set; }

    public int BandMismatches { get; set; }

    public List<string> BandMismatchSamples { get; set; } = [];

    public int ReferenceWords { get; set; }

    public int CandidateWords { get; set; }

    public int Matched { get; set; }

    public int UnpairedReference { get; set; }

    public int UnpairedCandidate { get; set; }

    /// <summary>Every unpaired word lies on a page whose band texts are all equal.</summary>
    public bool SegmentationOnly { get; set; }

    public bool ColumnsCompared { get; set; }

    public int PlacementMismatches { get; set; }

    public List<string> PlacementSamples { get; set; } = [];

    public int HorizontalOutside { get; set; }

    public int VerticalOutside { get; set; }

    public Spread DxMin { get; set; } = new(0, 0, 0, 0);

    public Spread DxMax { get; set; } = new(0, 0, 0, 0);

    public Spread DyCentre { get; set; } = new(0, 0, 0, 0);

    public bool Agrees { get; set; }
}

internal static class Agreement
{
    private const int Samples = 5;

    private enum Band
    {
        Header,
        Content,
        Footer,
    }

    private sealed record Place(Band Band, int Line, string Column);

    /// <summary>Compares a candidate's reading with the reference reading. geometry: the fixture's declared geometry, or null when unknown (columns are then not compared).</summary>
    public static AgreementResult Compare(string pdf, string configuration, PdfText reference, PdfText candidate, string? geometry)
    {
        var result = new AgreementResult
        {
            Pdf = pdf,
            Configuration = configuration,
            ReferencePages = reference.Pages,
            CandidatePages = candidate.Pages,
            PagesEqual = reference.Pages == candidate.Pages,
            ReferenceWords = reference.Words.Count,
            CandidateWords = candidate.Words.Count,
            ColumnsCompared = geometry is not null,
        };
        var refDoc = PrintedDocument.From(reference);
        var candDoc = PrintedDocument.From(candidate);
        var refPlaces = Places(refDoc, geometry);
        var candPlaces = Places(candDoc, geometry);
        var pages = Math.Max(refDoc.HeaderWords.Count, candDoc.HeaderWords.Count);
        var pagesWithBandMismatch = new HashSet<int>();
        for (var page = 1; page <= pages; page++)
        {
            foreach (var band in new[] { Band.Header, Band.Content, Band.Footer })
            {
                var a = BandText(refDoc, page, band);
                var b = BandText(candDoc, page, band);
                if (!string.Equals(a, b, StringComparison.Ordinal))
                {
                    result.BandMismatches++;
                    pagesWithBandMismatch.Add(page);
                    if (result.BandMismatchSamples.Count < Samples)
                    {
                        result.BandMismatchSamples.Add($"page {page} {band}: first difference at {FirstDifference(a, b)} ({Around(a, b)})");
                    }
                }
            }
        }

        var dxMin = new List<double>();
        var dxMax = new List<double>();
        var dy = new List<double>();
        var unpairedPages = new HashSet<int>();
        var byPage = candidate.Words.GroupBy(w => w.Page).ToDictionary(g => g.Key, g => g.ToList());
        foreach (var pageGroup in reference.Words.GroupBy(w => w.Page))
        {
            var pool = new Dictionary<string, List<PdfWord>>(StringComparer.Ordinal);
            foreach (var w in byPage.GetValueOrDefault(pageGroup.Key) ?? [])
            {
                var key = TextNormalisation.N(w.Text);
                if (key.Length == 0)
                {
                    continue;
                }

                if (!pool.TryGetValue(key, out var list))
                {
                    pool[key] = list = [];
                }

                list.Add(w);
            }

            foreach (var w in Ordered(refDoc, pageGroup.Key))
            {
                var key = TextNormalisation.N(w.Text);
                if (key.Length == 0)
                {
                    continue;
                }

                if (!pool.TryGetValue(key, out var candidates) || candidates.Count == 0)
                {
                    result.UnpairedReference++;
                    unpairedPages.Add(pageGroup.Key);
                    continue;
                }

                var best = candidates.MinBy(c => Square(c.XMin - w.XMin) + Square(c.YCentre - w.YCentre))!;
                candidates.Remove(best);
                result.Matched++;
                var x0 = Math.Abs(best.XMin - w.XMin);
                var x1 = Math.Abs(best.XMax - w.XMax);
                var yc = Math.Abs(best.YCentre - w.YCentre);
                dxMin.Add(x0);
                dxMax.Add(x1);
                dy.Add(yc);
                if (x0 > Settings.HorizontalTolerancePt || x1 > Settings.HorizontalTolerancePt)
                {
                    result.HorizontalOutside++;
                }

                if (yc > Settings.VerticalCentreTolerancePt)
                {
                    result.VerticalOutside++;
                }

                var p = refPlaces.GetValueOrDefault(w);
                var q = candPlaces.GetValueOrDefault(best);
                if (p != q)
                {
                    result.PlacementMismatches++;
                    if (result.PlacementSamples.Count < Samples)
                    {
                        result.PlacementSamples.Add($"page {w.Page} '{w.Text}': reference {p}, candidate {q}");
                    }
                }
            }

            foreach (var rest in pool.Values.Where(l => l.Count > 0))
            {
                result.UnpairedCandidate += rest.Count;
                unpairedPages.Add(pageGroup.Key);
            }
        }

        // Candidate words on pages the reference has no word on.
        var referencePages = reference.Words.Select(w => w.Page).ToHashSet();
        foreach (var (page, words) in byPage.Where(kv => !referencePages.Contains(kv.Key)))
        {
            result.UnpairedCandidate += words.Count(w => TextNormalisation.N(w.Text).Length > 0);
            unpairedPages.Add(page);
        }

        result.SegmentationOnly = unpairedPages.Count > 0 && !unpairedPages.Overlaps(pagesWithBandMismatch);
        result.DxMin = Spread.Of(dxMin);
        result.DxMax = Spread.Of(dxMax);
        result.DyCentre = Spread.Of(dy);
        result.Agrees = result.PagesEqual
            && result.BandMismatches == 0
            && (unpairedPages.Count == 0 || result.SegmentationOnly)
            && result.PlacementMismatches == 0
            && result.HorizontalOutside == 0
            && result.VerticalOutside == 0;
        return result;
    }

    /// <summary>A page's words in the checker's reading order: header band, content lines, footer band.</summary>
    public static IEnumerable<PdfWord> Ordered(PrintedDocument document, int page)
    {
        if (page - 1 < document.HeaderWords.Count)
        {
            foreach (var w in document.HeaderWords[page - 1])
            {
                yield return w;
            }
        }

        foreach (var line in document.ContentLines.Where(l => l.Page == page))
        {
            foreach (var w in line.Words)
            {
                yield return w;
            }
        }

        if (page - 1 < document.FooterWords.Count)
        {
            foreach (var w in document.FooterWords[page - 1])
            {
                yield return w;
            }
        }
    }

    private static string BandText(PrintedDocument document, int page, Band band) => band switch
    {
        Band.Header => page - 1 < document.HeaderWords.Count ? TextNormalisation.N(string.Concat(document.HeaderWords[page - 1].Select(w => w.Text))) : string.Empty,
        Band.Footer => page - 1 < document.FooterWords.Count ? TextNormalisation.N(string.Concat(document.FooterWords[page - 1].Select(w => w.Text))) : string.Empty,
        _ => TextNormalisation.N(string.Concat(document.ContentLines.Where(l => l.Page == page).SelectMany(l => l.Words).Select(w => w.Text))),
    };

    private static Dictionary<PdfWord, Place> Places(PrintedDocument document, string? geometry)
    {
        var columns = geometry is null
            ? []
            : DeclaredGeometry.Columns(geometry).Select(c => (c.Column, Start: DeclaredGeometry.MmToPt(c.StartMm), End: DeclaredGeometry.MmToPt(c.EndMm))).ToList();
        string Column(PdfWord w) => geometry is null ? "-" : columns.FirstOrDefault(c => w.XCentre >= c.Start && w.XCentre < c.End).Column ?? "none";
        var places = new Dictionary<PdfWord, Place>(ReferenceEqualityComparer.Instance);
        foreach (var list in document.HeaderWords)
        {
            foreach (var w in list)
            {
                places[w] = new Place(Band.Header, 0, Column(w));
            }
        }

        foreach (var list in document.FooterWords)
        {
            foreach (var w in list)
            {
                places[w] = new Place(Band.Footer, 0, Column(w));
            }
        }

        foreach (var group in document.ContentLines.GroupBy(l => l.Page))
        {
            var ordinal = 0;
            foreach (var line in group)
            {
                foreach (var w in line.Words)
                {
                    places[w] = new Place(Band.Content, ordinal, Column(w));
                }

                ordinal++;
            }
        }

        return places;
    }

    private static double Square(double v) => v * v;

    private static int FirstDifference(string a, string b)
    {
        var n = Math.Min(a.Length, b.Length);
        for (var i = 0; i < n; i++)
        {
            if (a[i] != b[i])
            {
                return i;
            }
        }

        return n;
    }

    private static string Around(string a, string b)
    {
        var i = FirstDifference(a, b);
        string Cut(string s) => s.Substring(Math.Max(0, i - 10), Math.Min(30, Math.Max(0, s.Length - Math.Max(0, i - 10))));
        return $"reference '{Cut(a)}' / candidate '{Cut(b)}'";
    }
}
