using TextIntegrityCheck;

namespace ReaderAgreement;

/// <summary>
/// The hermetic checks of the instrument (no container, no PDF): the extraction
/// file round trip, the coordinate conversion, the agreement measure at its
/// tolerance edges, the verdict comparison and the freeze check.
/// </summary>
internal static class SelfTest
{
    public static int Run()
    {
        var failures = 0;
        void Check(string name, bool ok)
        {
            Console.WriteLine($"{(ok ? "pass" : "FAIL")}  {name}");
            failures += ok ? 0 : 1;
        }

        // 1. Round trip.
        var text = Sample();
        var path = Path.Combine(Path.GetTempPath(), $"reader-agreement-selftest-{Environment.ProcessId}.json");
        ExtractionFile.From("test", "test", "00", text).Write(path);
        var back = ExtractionFile.Read(path).ToPdfText();
        File.Delete(path);
        Check("extraction file round trip keeps every field", back.Pages == text.Pages
            && back.PageSizes.SequenceEqual(text.PageSizes)
            && back.Words.SequenceEqual(text.Words));

        // 2. Coordinate conversion: a letter whose baseline is at y = 700 (origin bottom left) on a 792 pt page.
        const double pageHeight = 792;
        const double baselineY = 700;
        const double pointSize = 10;
        var yMax = pageHeight - baselineY;
        Check("baseline box: yMax = page height − baseline y = 92", Math.Abs(yMax - 92) < 1e-9);
        Check("baseline box: yMin = yMax − point size = 82", Math.Abs((yMax - pointSize) - 82) < 1e-9);
        Check("glyph box: top 710 on a 792 pt page maps to yMin 82", Math.Abs((pageHeight - 710) - 82) < 1e-9);

        // 2b. The PdfPig adapter on a synthetic PDF: "Hello" at baseline (72, 700), Helvetica 10 pt, on a 595 × 842 page.
        var builder = new UglyToad.PdfPig.Writer.PdfDocumentBuilder();
        var page = builder.AddPage(UglyToad.PdfPig.Content.PageSize.A4);
        var font = builder.AddStandard14Font(UglyToad.PdfPig.Fonts.Standard14Fonts.Standard14Font.Helvetica);
        page.AddText("Hello", 10, new UglyToad.PdfPig.Core.PdfPoint(72, 700), font);
        var synthetic = Path.Combine(Path.GetTempPath(), $"reader-agreement-selftest-{Environment.ProcessId}.pdf");
        File.WriteAllBytes(synthetic, builder.Build());
        var (asShipped, baseline, _) = PdfPigExtractor.Extract(synthetic);
        File.Delete(synthetic);
        var height = asShipped.PageSizes[0].Height;
        var a = asShipped.Words.Single();
        var b = baseline.Words.Single();
        Check($"adapter: one word 'Hello' on page 1 (read '{a.Text}', page {a.Page})", a.Text == "Hello" && a.Page == 1 && asShipped.Pages == 1);
        Check($"adapter: left edge at 72 (read {a.XMin:0.###})", Math.Abs(a.XMin - 72) < 0.5);
        Check($"adapter C1-B: yMax = page height − 700 = {height - 700:0.##} (read {b.YMax:0.###}), yMin 10 pt above", Math.Abs(b.YMax - (height - 700)) < 0.01 && Math.Abs(b.YMax - b.YMin - 10) < 0.01);
        Check($"adapter C1-A: the glyph box lies above the baseline, within the em box (yMin {a.YMin:0.##}, yMax {a.YMax:0.##})", a.YMax <= height - 700 + 0.5 && a.YMin >= height - 700 - 10.5 && a.YMin < a.YMax);

        // 3. Agreement at the tolerance edges.
        Check("identical readings agree", Agreement.Compare("t", "t", text, Sample(), null).Agrees);
        Check("0.9 pt horizontal shift agrees", Agreement.Compare("t", "t", text, Shift(Sample(), 0.9, 0), null).Agrees);
        Check("1.1 pt horizontal shift disagrees", !Agreement.Compare("t", "t", text, Shift(Sample(), 1.1, 0), null).Agrees);
        Check("2.9 pt vertical shift agrees", Agreement.Compare("t", "t", text, Shift(Sample(), 0, 2.9), null).Agrees);
        Check("3.1 pt vertical shift disagrees", !Agreement.Compare("t", "t", text, Shift(Sample(), 0, 3.1), null).Agrees);
        var split = Sample();
        var w = split.Words[1];
        split.Words[1] = w with { XMax = w.XMin + ((w.XMax - w.XMin) / 2), Text = "Desc" };
        split.Words.Insert(2, w with { XMin = w.XMin + ((w.XMax - w.XMin) / 2), Text = "ription" });
        var segmentation = Agreement.Compare("t", "t", text, split, null);
        Check("a word split in two with equal band text is a segmentation difference", segmentation.Agrees && segmentation.SegmentationOnly && segmentation.UnpairedReference == 1);
        var moved = Sample();
        moved.Words[1] = moved.Words[1] with { Page = 2 };
        Check("a word on another page disagrees", !Agreement.Compare("t", "t", text, moved, null).Agrees);
        var altered = Sample();
        altered.Words[1] = altered.Words[1] with { Text = "Descriptiom" };
        Check("a changed letter disagrees", !Agreement.Compare("t", "t", text, altered, null).Agrees);
        var fewerPages = Sample();
        var lessPages = new PdfText { Pages = 1, PageSizes = [fewerPages.PageSizes[0]], Words = [.. fewerPages.Words.Where(x => x.Page == 1)] };
        Check("a lost page disagrees", !Agreement.Compare("t", "t", text, lessPages, null).Agrees);

        // 4. Verdict comparison.
        var pass = new RuleVerdict("TI-2", "pass", [], null);
        var fail = new RuleVerdict("TI-2", "fail", ["row-lost"], 2);
        Check("equal passes agree", Verdicts.Same(pass, pass with { }));
        Check("pass against fail disagrees", !Verdicts.Same(pass, fail));
        Check("fails with another page disagree", !Verdicts.Same(fail, fail with { Page = 3 }));
        Check("fails with other classes disagree", !Verdicts.Same(fail, fail with { Classes = ["row-duplicated"] }));
        Check("equal fails agree", Verdicts.Same(fail, fail with { Classes = ["row-lost"] }));

        // 5. Freeze check refuses a missing or different freeze.
        var empty = Path.Combine(Path.GetTempPath(), $"reader-agreement-selftest-{Environment.ProcessId}");
        Directory.CreateDirectory(empty);
        Check("no freeze file refuses the stage", Freeze.Check(empty) is not null);
        File.WriteAllText(Path.Combine(empty, "freeze.json"), "{\"settingsSha256\":\"x\",\"pdfSetSha256\":\"x\",\"files\":{}}");
        Check("a different freeze refuses the stage", Freeze.Check(empty) is not null);
        Directory.Delete(empty, recursive: true);

        // 6. The fixed set.
        Check("the set holds 42 PDFs: 30 judged, 12 agreement only, 10 regenerated",
            PdfSet.All.Count == 42 && PdfSet.All.Count(e => e.Use == "judged") == 30 && PdfSet.All.Count(e => e.Use == "agreement") == 12 && PdfSet.All.Count(e => e.Regenerated) == 10);
        Check("every PDF id is unique", PdfSet.All.Select(e => e.Id).Distinct().Count() == PdfSet.All.Count);
        Check("every judged rule has a known-bad PDF in the set",
            Judged.TextRules.Concat(Judged.StrictRules.Select(Judged.StrictName)).All(r => PdfSet.All.Any(e => e.KnownBad.Contains(r))));

        Console.WriteLine(failures == 0 ? "selftest: all checks pass" : $"selftest: {failures} failure(s)");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>Writes a two-page PDF with words in the header band, the content area and the footer band (never one of the measured PDFs).</summary>
    public static int Synthetic(string path)
    {
        var builder = new UglyToad.PdfPig.Writer.PdfDocumentBuilder();
        var font = builder.AddStandard14Font(UglyToad.PdfPig.Fonts.Standard14Fonts.Standard14Font.Helvetica);
        for (var n = 1; n <= 2; n++)
        {
            var page = builder.AddPage(UglyToad.PdfPig.Content.PageSize.A4);
            page.AddText("Synthetic report", 12, new UglyToad.PdfPig.Core.PdfPoint(40, 810), font);
            page.AddText($"Row {n} description 12.50", 9, new UglyToad.PdfPig.Core.PdfPoint(40, 700), font);
            page.AddText($"Page {n} of 2", 8, new UglyToad.PdfPig.Core.PdfPoint(280, 20), font);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllBytes(path, builder.Build());
        Console.WriteLine($"Written {path}.");
        return 0;
    }

    /// <summary>Two pages; header, content and footer words in the declared geometry (header above 15 mm, footer below 279 mm).</summary>
    private static PdfText Sample() => new()
    {
        Pages = 2,
        PageSizes = [(595.28, 841.89), (595.28, 841.89)],
        Words =
        [
            new PdfWord(1, 40, 20, 80, 30, "Title"),
            new PdfWord(1, 40, 100, 120, 110, "Description"),
            new PdfWord(1, 130, 100, 160, 110, "12.50"),
            new PdfWord(1, 40, 115, 90, 125, "second"),
            new PdfWord(1, 40, 800, 120, 810, "Footer"),
            new PdfWord(2, 40, 100, 120, 110, "Other"),
        ],
    };

    private static PdfText Shift(PdfText text, double dx, double dy) => new()
    {
        Pages = text.Pages,
        PageSizes = text.PageSizes,
        Words = [.. text.Words.Select(w => w with { XMin = w.XMin + dx, XMax = w.XMax + dx, YMin = w.YMin + dy, YMax = w.YMax + dy })],
    };
}
