using System.Globalization;
using System.Security.Cryptography;
using PaginationFixtures;
using PdfAssemblySpike;
using TextIntegrityCheck;

namespace PageAccountingCheck;

/// <summary>One failing item of the unchanged-outside checks: check (UO-1 … UO-4), class, page, detail.</summary>
internal sealed record CheckFailure(string Check, string Class, int? Page, string Detail);

/// <summary>Pixels of one pass-2 page against its matching reference.</summary>
internal sealed record PagePixels(int Page, string Reference, long Changed, long Outside, long OverInk, string RenderSha256);

/// <summary>The result of UO-1 … UO-4 for one pass-1 / pass-2 pair.</summary>
internal sealed class UnchangedResult
{
    public List<CheckFailure> Failures { get; } = [];

    public List<string> Pass1Boxes { get; set; } = [];

    public List<string> Pass2Boxes { get; set; } = [];

    public string BoxVerdict { get; set; } = string.Empty;

    /// <summary>True when a pass-1 page box is not the Chromium box: a stop-and-ask condition, never judged.</summary>
    public bool Pass1BoxUnexpected { get; set; }

    public RoundedReference? Rounded { get; set; }

    public List<PagePixels> Pages { get; } = [];

    public int NewWords { get; set; }

    public int WordsLost { get; set; }

    public int WordsChanged { get; set; }

    public int WordsOutside { get; set; }

    public int NewWordsOverInk { get; set; }

    /// <summary>For the control that adds a square: whether every reference pixel under it is white.</summary>
    public bool? SquareOnWhite { get; set; }

    /// <summary>The colour check (when asked for): per page, changed pixels and those outside the allowed region or over ink.</summary>
    public List<PagePixels> ColourPages { get; } = [];

    public List<CheckFailure> ColourFailures { get; } = [];
}

/// <summary>
/// The PDF unchanged outside the reserved area. Regions in mm from the declared
/// geometry, top-left origin, never from the second pass: R-F the bottom margin
/// band (y from 279 to 297) on every page; R-A the amount band of the content area
/// (x from 168 to 198, y from 15 to 279) in strategy "stamped" only. The allowed
/// region is R-F (and R-A) rounded outward to whole pixels at 72 dpi and widened by
/// one pixel. UO-1: no changed pixel outside it; UO-2: every changed pixel white in
/// the reference, and no new word over a pass-1 word; UO-3: every pass-1 word kept
/// with its text and box (±0.05 pt), every new word inside the allowed region;
/// UO-4: the page-box rule, which also chooses each page's matching reference.
/// </summary>
internal static class UnchangedOutside
{
    public const double WordTolerancePt = 0.05;

    public static double Pt(double mm) => DeclaredGeometry.MmToPt(mm);

    /// <summary>The allowed region in points (and pixels at 72 dpi), widened by one unit on every side.</summary>
    public static bool AllowedPixel(int x, int y, bool stamped)
    {
        if (y >= (int)Math.Floor(Pt(279)) - 1)
        {
            return true;
        }

        return stamped
            && x >= (int)Math.Floor(Pt(168)) - 1 && x < (int)Math.Ceiling(Pt(198)) + 1
            && y >= (int)Math.Floor(Pt(15)) - 1 && y < (int)Math.Ceiling(Pt(279)) + 1;
    }

    public static bool AllowedBox(PdfWord word, bool stamped)
    {
        if (word.YMin >= Math.Floor(Pt(279)) - 1)
        {
            return true;
        }

        return stamped
            && word.XMin >= Math.Floor(Pt(168)) - 1 && word.XMax <= Math.Ceiling(Pt(198)) + 1
            && word.YMin >= Math.Floor(Pt(15)) - 1 && word.YMax <= Math.Ceiling(Pt(279)) + 1;
    }

    public static async Task<UnchangedResult> CheckAsync(
        string pass1Pdf, string pass2Pdf, PdfReading pass1, PdfReading pass2, bool stamped, string workDirectory,
        (int Page, double X, double Y, double Side)? square, TimeSpan timeout, CancellationToken cancellation, bool colour = false)
    {
        var result = new UnchangedResult();
        Directory.CreateDirectory(workDirectory);

        // UO-4 page boxes, read as written with qpdf, compared in decimal.
        var boxes1 = await Qpdf.PagesAsync(pass1Pdf, workDirectory, timeout, cancellation);
        var boxes2 = await Qpdf.PagesAsync(pass2Pdf, workDirectory, timeout, cancellation);
        result.Pass1Boxes = Distinct(boxes1);
        result.Pass2Boxes = Distinct(boxes2);
        if (boxes1.Any(p => p.MediaBox is null || !Qpdf.Equal(p.MediaBox.Numbers, Qpdf.SourceBox) || (p.CropBox is not null && !Qpdf.Equal(p.CropBox.Numbers, Qpdf.SourceBox))))
        {
            result.Pass1BoxUnexpected = true;
            result.Failures.Add(new CheckFailure("UO-4", "pass1-box-unexpected", null, $"pass-1 boxes {string.Join(", ", result.Pass1Boxes)}"));
        }

        if (boxes1.Count != boxes2.Count)
        {
            result.Failures.Add(new CheckFailure("UO-4", "page-count-differs", null, $"{boxes1.Count} pass-1 pages, {boxes2.Count} pass-2 pages"));
            result.BoxVerdict = "fail";
            return result;
        }

        var classes = new List<string>();
        foreach (var page in boxes2)
        {
            var media = page.MediaBox is null ? Qpdf.Differs : Qpdf.Classify(page.MediaBox.Numbers);
            var crop = page.CropBox is null ? null : Qpdf.Classify(page.CropBox.Numbers);
            var pageClass = media == Qpdf.Differs || crop == Qpdf.Differs || (crop is not null && crop != media) ? Qpdf.Differs : media;
            classes.Add(pageClass);
            if (pageClass == Qpdf.Differs)
            {
                result.Failures.Add(new CheckFailure("UO-4", Qpdf.Differs, page.Page, $"/MediaBox [{page.MediaBox?.Text}]" + (page.CropBox is null ? string.Empty : $", /CropBox [{page.CropBox.Text}]")));
            }
        }

        result.BoxVerdict = classes.Contains(Qpdf.Differs) ? "fail"
            : classes.Contains(Qpdf.Source) && classes.Contains(Qpdf.Rounded) ? "box-mixed"
            : classes.Contains(Qpdf.Rounded) ? "box-rounded"
            : "box-source";

        string? roundedPath = null;
        if (classes.Contains(Qpdf.Rounded) && !result.Pass1BoxUnexpected)
        {
            roundedPath = Path.Combine(workDirectory, $"ref-rounded-{Guid.NewGuid():N}.pdf");
            result.Rounded = await Qpdf.BuildRoundedAsync(pass1Pdf, roundedPath, workDirectory, timeout, cancellation);
            if (!result.Rounded.Pass)
            {
                result.Failures.Add(new CheckFailure("UO-4", "ref-rounded-failed", null, result.Rounded.Failure ?? string.Empty));
                roundedPath = null;
            }
        }

        try
        {
            // UO-1, UO-2: pixels, one page at a time.
            for (var page = 1; page <= boxes2.Count; page++)
            {
                var pageClass = classes[page - 1];
                string? reference = pageClass switch
                {
                    Qpdf.Source when !result.Pass1BoxUnexpected => pass1Pdf,
                    Qpdf.Rounded when roundedPath is not null => roundedPath,
                    _ => null,
                };
                if (reference is null)
                {
                    continue;
                }

                var candidate = await RenderAsync(pass2Pdf, page, workDirectory, timeout, cancellation);
                var basis = await RenderAsync(reference, page, workDirectory, timeout, cancellation);
                var sha = Convert.ToHexStringLower(SHA256.HashData(candidate.Pixels));
                if (candidate.Width != basis.Width || candidate.Height != basis.Height)
                {
                    result.Failures.Add(new CheckFailure("UO-1", "size-differs", page, $"{candidate.Width}x{candidate.Height} against {basis.Width}x{basis.Height}"));
                    result.Pages.Add(new PagePixels(page, pageClass == Qpdf.Rounded ? "ref-rounded" : "pass-1", -1, -1, -1, sha));
                    continue;
                }

                long changed = 0;
                long outside = 0;
                long overInk = 0;
                for (var i = 0; i < candidate.Pixels.Length; i++)
                {
                    if (candidate.Pixels[i] == basis.Pixels[i])
                    {
                        continue;
                    }

                    changed++;
                    if (!AllowedPixel(i % candidate.Width, i / candidate.Width, stamped))
                    {
                        outside++;
                    }

                    if (basis.Pixels[i] != 255)
                    {
                        overInk++;
                    }
                }

                result.Pages.Add(new PagePixels(page, pageClass == Qpdf.Rounded ? "ref-rounded" : "pass-1", changed, outside, overInk, sha));
                if (outside > 0)
                {
                    result.Failures.Add(new CheckFailure("UO-1", "outside-reserved", page, string.Create(CultureInfo.InvariantCulture, $"{outside} changed pixel(s) outside the allowed region")));
                }

                if (overInk > 0)
                {
                    result.Failures.Add(new CheckFailure("UO-2", "stamp-over-ink", page, string.Create(CultureInfo.InvariantCulture, $"{overInk} changed pixel(s) not white in the reference")));
                }

                if (square is { } s && s.Page == page)
                {
                    var x0 = (int)Math.Floor(s.X - (s.Side / 2));
                    var x1 = (int)Math.Ceiling(s.X + (s.Side / 2));
                    var y0 = (int)Math.Floor(s.Y - (s.Side / 2));
                    var y1 = (int)Math.Ceiling(s.Y + (s.Side / 2));
                    var white = true;
                    for (var y = y0; y < y1; y++)
                    {
                        for (var x = x0; x < x1; x++)
                        {
                            white &= basis.Pixels[(y * basis.Width) + x] == 255;
                        }
                    }

                    result.SquareOnWhite = white;
                }

                if (colour)
                {
                    await ColourAsync(result, pass2Pdf, reference, page, pageClass == Qpdf.Rounded ? "ref-rounded" : "pass-1", stamped, workDirectory, timeout, cancellation);
                }
            }
        }
        finally
        {
            if (roundedPath is not null)
            {
                File.Delete(roundedPath);
            }
        }

        // UO-2 (words) and UO-3.
        for (var page = 1; page <= Math.Min(pass1.Pages, pass2.Pages); page++)
        {
            var before = AllWords(pass1.Document, page);
            var after = AllWords(pass2.Document, page);
            var unmatched = new List<PdfWord>(after);
            var lost = new List<PdfWord>();
            foreach (var word in before)
            {
                var match = unmatched.FindIndex(w => w.Text == word.Text && SameBox(w, word));
                if (match >= 0)
                {
                    unmatched.RemoveAt(match);
                }
                else
                {
                    lost.Add(word);
                }
            }

            foreach (var word in lost)
            {
                var changed = unmatched.FindIndex(w => SameBox(w, word));
                if (changed >= 0)
                {
                    result.WordsChanged++;
                    result.Failures.Add(new CheckFailure("UO-3", "word-changed", page, $"'{word.Text}' became '{unmatched[changed].Text}'"));
                    unmatched.RemoveAt(changed);
                }
                else
                {
                    result.WordsLost++;
                    result.Failures.Add(new CheckFailure("UO-3", "word-lost", page, $"'{word.Text}' at {Box(word)}"));
                }
            }

            foreach (var word in unmatched)
            {
                result.NewWords++;
                if (!AllowedBox(word, stamped))
                {
                    result.WordsOutside++;
                    result.Failures.Add(new CheckFailure("UO-3", "word-outside", page, $"new word '{word.Text}' at {Box(word)}"));
                }

                if (before.Any(w => Overlaps(w, word)))
                {
                    result.NewWordsOverInk++;
                    result.Failures.Add(new CheckFailure("UO-2", "stamp-over-ink", page, $"new word '{word.Text}' over a pass-1 word"));
                }
            }
        }

        return result;
    }

    private static List<string> Distinct(List<QpdfPage> pages) =>
        [.. pages.GroupBy(p => (p.MediaBox?.Text ?? "none") + (p.CropBox is null ? string.Empty : " crop " + p.CropBox.Text), StringComparer.Ordinal)
            .Select(g => string.Create(CultureInfo.InvariantCulture, $"[{g.Key}] x{g.Count()}"))];

    private static List<PdfWord> AllWords(PrintedDocument document, int page) =>
        [.. (page <= document.HeaderWords.Count ? document.HeaderWords[page - 1] : new List<PdfWord>())
            .Concat(document.ContentLines.Where(l => l.Page == page).SelectMany(l => l.Words))
            .Concat(page <= document.FooterWords.Count ? document.FooterWords[page - 1] : new List<PdfWord>())];

    private static bool SameBox(PdfWord a, PdfWord b) =>
        Math.Abs(a.XMin - b.XMin) <= WordTolerancePt && Math.Abs(a.XMax - b.XMax) <= WordTolerancePt
        && Math.Abs(a.YMin - b.YMin) <= WordTolerancePt && Math.Abs(a.YMax - b.YMax) <= WordTolerancePt;

    private static bool Overlaps(PdfWord a, PdfWord b) => a.XMin < b.XMax && b.XMin < a.XMax && a.YMin < b.YMax && b.YMin < a.YMax;

    private static string Box(PdfWord w) => string.Create(CultureInfo.InvariantCulture, $"x {w.XMin:0.##}–{w.XMax:0.##}, y {w.YMin:0.##}–{w.YMax:0.##}");

    /// <summary>
    /// The colour check of one page: both PDFs rendered in colour at 72 dpi (PPM), compared
    /// like UO-1 and UO-2 — no changed pixel outside the allowed region, every changed pixel
    /// white (255, 255, 255) in the reference; no tolerance.
    /// </summary>
    private static async Task ColourAsync(UnchangedResult result, string pass2Pdf, string reference, int page, string referenceName, bool stamped, string workDirectory, TimeSpan timeout, CancellationToken cancellation)
    {
        var candidate = await RenderColourAsync(pass2Pdf, page, workDirectory, timeout, cancellation);
        var basis = await RenderColourAsync(reference, page, workDirectory, timeout, cancellation);
        var sha = Convert.ToHexStringLower(SHA256.HashData(candidate.Pixels));
        if (candidate.Width != basis.Width || candidate.Height != basis.Height)
        {
            result.ColourFailures.Add(new CheckFailure("colour", "size-differs", page, $"{candidate.Width}x{candidate.Height} against {basis.Width}x{basis.Height}"));
            result.ColourPages.Add(new PagePixels(page, referenceName, -1, -1, -1, sha));
            return;
        }

        long changed = 0;
        long outside = 0;
        long overInk = 0;
        for (var i = 0; i < candidate.Width * candidate.Height; i++)
        {
            var o = i * 3;
            if (candidate.Pixels[o] == basis.Pixels[o] && candidate.Pixels[o + 1] == basis.Pixels[o + 1] && candidate.Pixels[o + 2] == basis.Pixels[o + 2])
            {
                continue;
            }

            changed++;
            if (!AllowedPixel(i % candidate.Width, i / candidate.Width, stamped))
            {
                outside++;
            }

            if (basis.Pixels[o] != 255 || basis.Pixels[o + 1] != 255 || basis.Pixels[o + 2] != 255)
            {
                overInk++;
            }
        }

        result.ColourPages.Add(new PagePixels(page, referenceName, changed, outside, overInk, sha));
        if (outside > 0)
        {
            result.ColourFailures.Add(new CheckFailure("colour", "outside-reserved", page, string.Create(CultureInfo.InvariantCulture, $"{outside} changed colour pixel(s) outside the allowed region")));
        }

        if (overInk > 0)
        {
            result.ColourFailures.Add(new CheckFailure("colour", "stamp-over-ink", page, string.Create(CultureInfo.InvariantCulture, $"{overInk} changed colour pixel(s) not white in the reference")));
        }
    }

    /// <summary>Renders one page in colour (PPM) at 72 dpi with pdftoppm, loads it and deletes the file.</summary>
    public static async Task<PpmImage> RenderColourAsync(string pdf, int page, string workDirectory, TimeSpan timeout, CancellationToken cancellation)
    {
        var prefix = Path.Combine(workDirectory, $"colour-{Guid.NewGuid():N}");
        var p = page.ToString(CultureInfo.InvariantCulture);
        var result = await Tool.RunAsync("pdftoppm", ["-r", "72", "-f", p, "-l", p, "-singlefile", pdf, prefix], timeout, cancellation);
        var file = prefix + ".ppm";
        try
        {
            if (result.ExitCode != 0 || !File.Exists(file))
            {
                throw new InvalidDataException($"pdftoppm exited {result.ExitCode}: {result.StandardError.Split('\n')[0]}");
            }

            return PpmImage.Load(file);
        }
        finally
        {
            File.Delete(file);
        }
    }

    /// <summary>Renders one page to a grayscale PGM at 72 dpi with pdftoppm, loads it and deletes the file.</summary>
    public static async Task<PgmImage> RenderAsync(string pdf, int page, string workDirectory, TimeSpan timeout, CancellationToken cancellation)
    {
        var prefix = Path.Combine(workDirectory, $"page-{Guid.NewGuid():N}");
        var p = page.ToString(CultureInfo.InvariantCulture);
        var result = await Tool.RunAsync("pdftoppm", ["-gray", "-r", "72", "-f", p, "-l", p, "-singlefile", pdf, prefix], timeout, cancellation);
        var file = prefix + ".pgm";
        try
        {
            if (result.ExitCode != 0 || !File.Exists(file))
            {
                throw new InvalidDataException($"pdftoppm exited {result.ExitCode}: {result.StandardError.Split('\n')[0]}");
            }

            return PgmImage.Load(file);
        }
        finally
        {
            File.Delete(file);
        }
    }
}
