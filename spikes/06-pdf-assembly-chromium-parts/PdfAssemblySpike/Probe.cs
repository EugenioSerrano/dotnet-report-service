using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace PdfAssemblySpike;

/// <summary>
/// Pre-flight checks of the behaviour the measurements rely on, on the producer's
/// probe set (two Chromium parts of ten pages), check (b) first: (b) what appending
/// a page number adds to a page, the page box after import judged by the page-box
/// rule, and the pixels against the matching reference, which must equal the expected
/// figures; then (a) the words and their positions survive merging and stamping, (c)
/// closing sources after import and the structural check, (d) the numbering area
/// is empty on the parts.
/// </summary>
internal static partial class Probe
{
    /// <summary>Changed pixels per stamped page inside the numbering area, as expected from the earlier reproduction.</summary>
    private const long ExpectedStampMin = 191;

    private const long ExpectedStampMax = 226;

    public static int Run(string partsDir, string outDir, string? fontDir)
    {
        DejaVuFontResolver.Install(fontDir);
        Directory.CreateDirectory(outDir);
        string manifestPath = Path.Combine(partsDir, "manifest.json");
        var manifest = SetManifest.Load(manifestPath);
        int total = manifest.TargetPages;
        string temp = Path.Combine(outDir, "probe-temp");
        Directory.CreateDirectory(temp);
        var report = new Dictionary<string, object?>
        {
            ["probeSet"] = new { manifest.TargetPages, parts = manifest.Parts.Select(p => new { p.Order, p.File, p.Pages, p.Sha256 }) },
            ["fontEncodingOption"] = PageLayout.Encoding.ToString(),
            ["stampWrappedInGraphicsState"] = PageStamper.WrapInGraphicsState,
            ["sourcesKeptOpenUntilSave"] = Assembler.KeepSourcesOpenUntilSave,
            ["numberingArea"] = string.Create(CultureInfo.InvariantCulture,
                $"x {PageLayout.NumberingAreaLeft:F3}-{PageLayout.NumberingAreaRight:F3} pt, y {PageLayout.NumberingAreaTop:F3}-{PageLayout.NumberingAreaBottom:F3} pt from the top-left corner"),
            ["numberingAreaPixels"] = PixelRect.NumberingArea.ToString(),
        };
        bool pass = true;

        // The four outputs of the probe set: V1 merge, V2 merge + stamp, V4 qpdf merge, V5 stamped parts + qpdf merge.
        string v1 = Path.Combine(outDir, "probe-v1-merge.pdf");
        string v2 = Path.Combine(outDir, "probe-v2-merge-stamp.pdf");
        string v4 = Path.Combine(outDir, "probe-v4-qpdf-merge.pdf");
        string v5 = Path.Combine(outDir, "probe-v5-stamp-qpdf-merge.pdf");
        Assembler.MergeParts(manifest, manifestPath, v1, stamp: false);
        Assembler.MergeParts(manifest, manifestPath, v2, stamp: true);
        string args4 = Path.Combine(temp, "v4.args");
        Assembler.WriteQpdfArguments(manifest, manifestPath, null, v4, args4);
        var qpdf4 = ExternalTool.Run(Verifier.Qpdf, ["@" + args4]);
        string stampedParts = Path.Combine(temp, "stamped-parts");
        Directory.CreateDirectory(stampedParts);
        Assembler.StampEachPart(manifest, manifestPath, stampedParts);
        string args5 = Path.Combine(temp, "v5.args");
        Assembler.WriteQpdfArguments(manifest, manifestPath, stampedParts, v5, args5);
        var qpdf5 = ExternalTool.Run(Verifier.Qpdf, ["@" + args5]);
        report["qpdfMergeExitCodes"] = new { v4 = qpdf4.ExitCode, v5 = qpdf5.ExitCode, v4Error = qpdf4.StandardError.Trim(), v5Error = qpdf5.StandardError.Trim() };
        var map = manifest.PageMap();
        string part1 = SetManifest.PartPath(manifestPath, manifest.Parts[0]);
        string renderDir = Path.Combine(temp, "render");
        Directory.CreateDirectory(renderDir);
        var references = new Dictionary<int, Dictionary<int, string>>();
        foreach (var part in manifest.Parts)
        {
            references[part.Order] = Verifier.Render(SetManifest.PartPath(manifestPath, part), 1, part.Pages, Path.Combine(renderDir, "src" + part.Order.ToString(CultureInfo.InvariantCulture)));
        }

        // (b) first: what appending the page number adds, every page box read with qpdf in
        // decimal arithmetic and judged by the fixed table, and the pixels of every page
        // against its matching reference (the unedited part for the source box, the rounded
        // copy for the rounded box), with the comparison against the unedited part recorded.
        // The figures must equal the expected ones; any difference stops the probe.
        var sourceStreams = ContentStreams(part1, 1);
        var stampedStreams = ContentStreams(v2, 1);
        string firstData = stampedStreams.Count > 0 ? StreamData(v2, stampedStreams[0]).Trim() : string.Empty;
        string lastOriginal = stampedStreams.Count >= 2 ? StreamData(v2, stampedStreams[^2]).TrimEnd() : string.Empty;
        string roundedDir = Path.Combine(temp, "ref-rounded");
        var roundedCopies = manifest.Parts.Select(p => PageBoxes.BuildRounded(p.Order, SetManifest.PartPath(manifestPath, p), Path.Combine(roundedDir, p.File))).ToList();
        bool constructionOk = roundedCopies.All(c => c.Pass);
        var rounded = new Dictionary<int, Dictionary<int, string>>();
        if (constructionOk)
        {
            foreach (var part in manifest.Parts)
            {
                rounded[part.Order] = Verifier.Render(Path.Combine(roundedDir, part.File), 1, part.Pages, Path.Combine(renderDir, "rnd" + part.Order.ToString(CultureInfo.InvariantCulture)));
            }
        }

        var sourceBoxes = manifest.Parts.SelectMany(p => PageBoxes.Read(SetManifest.PartPath(manifestPath, p))).ToList();
        bool boxesOk = sourceBoxes.Count == total && sourceBoxes.All(b => b.Text == PageBoxes.SourceBoxText);
        var boxes = new Dictionary<string, object>
        {
            ["parts"] = new { values = PageBoxes.Distinct(sourceBoxes.Select(b => b.Text)), cropBoxes = sourceBoxes.Count(b => b.CropText is not null), expected = $"[{PageBoxes.SourceBoxText}] on {total} of {total} pages", ok = boxesOk },
        };
        var pixels = new List<string>();
        bool pixelsOk = constructionOk;
        foreach (var (name, pdf, numbered, rule, expectedBox) in new[]
        {
            ("V1", v1, false, PageBoxes.SourceOrRoundedRule, PageBoxes.RoundedBoxText),
            ("V2", v2, true, PageBoxes.SourceOrRoundedRule, PageBoxes.RoundedBoxText),
            ("V4", v4, false, PageBoxes.SourceOnlyRule, PageBoxes.SourceBoxText),
            ("V5", v5, true, PageBoxes.SourceOrRoundedRule, PageBoxes.RoundedBoxText),
        })
        {
            var read = PageBoxes.Read(pdf);
            var classes = read.Select(b => PageBoxes.Classify(b.Numbers, rule)).ToList();
            bool boxOk = read.Count == total && read.All(b => b.Text == expectedBox) && !classes.Contains(PageBoxes.Differs);
            boxesOk &= boxOk;
            boxes[name] = new
            {
                rule,
                values = PageBoxes.Distinct(read.Select(b => b.Text)),
                cropBoxes = read.Count(b => b.CropText is not null),
                classes = PageBoxes.Counts(classes),
                verdict = PageBoxes.Verdict(classes),
                expected = $"[{expectedBox}] on {total} of {total} pages",
                ok = boxOk,
            };

            var rendered = Verifier.Render(pdf, 1, total, Path.Combine(renderDir, name));
            bool ok = rendered.Count == total && constructionOk;
            var perPage = new List<string>();
            long maxOutside = 0;
            long minInside = long.MaxValue;
            long maxInside = 0;
            for (int i = 1; i <= total && constructionOk; i++)
            {
                var (part, k) = map[i - 1];
                if (!rendered.TryGetValue(i, out string? file) || i > classes.Count || classes[i - 1] == PageBoxes.Differs)
                {
                    ok = false;
                    perPage.Add(string.Create(CultureInfo.InvariantCulture, $"page {i}: not rendered or box outside the table"));
                    continue;
                }

                bool isRounded = classes[i - 1] == PageBoxes.Rounded;
                var output = PgmImage.Load(file);
                var matching = PgmImage.Compare(PgmImage.Load(isRounded ? rounded[part][k] : references[part][k]), output, PixelRect.NumberingArea);
                var strict = PgmImage.Compare(PgmImage.Load(references[part][k]), output, PixelRect.NumberingArea);
                bool pageOk = matching.SameSize && matching.ChangedOutside == 0
                    && (numbered ? matching.ChangedInside is >= ExpectedStampMin and <= ExpectedStampMax : matching.ChangedInside == 0);
                ok &= pageOk;
                maxOutside = Math.Max(maxOutside, matching.ChangedOutside);
                minInside = Math.Min(minInside, matching.ChangedInside);
                maxInside = Math.Max(maxInside, matching.ChangedInside);
                perPage.Add(string.Create(CultureInfo.InvariantCulture,
                    $"page {i}: reference {(isRounded ? "rounded" : "unedited")}, changed outside {matching.ChangedOutside}, inside {matching.ChangedInside}; against the unedited part (information): outside {strict.ChangedOutside}, inside {strict.ChangedInside}{(pageOk ? string.Empty : " — DIFFERS FROM THE EXPECTED FIGURES")}"));
            }

            pixelsOk &= ok;
            pixels.Add(string.Create(CultureInfo.InvariantCulture,
                $"{name}: {rendered.Count} pages rendered, against the matching reference changed outside max {maxOutside}, inside {(minInside == long.MaxValue ? 0 : minInside)}-{maxInside} — {(ok ? "as expected" : "DIFFERS")}"));
            pixels.AddRange(perPage.Select(l => "  " + name + " " + l));
        }

        bool bOk = constructionOk && boxesOk && pixelsOk;
        report["b_appendAndPageBox"] = new
        {
            pass = bOk,
            expected = string.Create(CultureInfo.InvariantCulture,
                $"boxes: parts and V4 [{PageBoxes.SourceBoxText}], V1, V2, V5 [{PageBoxes.RoundedBoxText}], on every page; pixels against the matching reference: V1 and V4 0 anywhere, V2 and V5 0 outside and {ExpectedStampMin}-{ExpectedStampMax} inside on every page"),
            roundedCopies,
            sourceContentStreams = sourceStreams.Count,
            sourceStreams = sourceStreams.Select(n => Summarize(part1, n)).ToList(),
            stampedContentStreams = stampedStreams.Count,
            stampedStreams = stampedStreams.Select(n => Summarize(v2, n)).ToList(),
            originalWrappedInSaveRestore = firstData == "q" && lastOriginal.EndsWith('Q'),
            pageBoxesOfPage1 = new Dictionary<string, string>
            {
                ["source"] = PageBox(part1, 1),
                ["V1"] = PageBox(v1, 1),
                ["V2"] = PageBox(v2, 1),
                ["V4"] = PageBox(v4, 1),
                ["V5"] = PageBox(v5, 1),
            },
            pageBoxes = boxes,
            pixels,
        };
        pass &= bOk;
        if (!bOk)
        {
            // Stop: the other checks wait for a decision on this one.
            Directory.Delete(temp, true);
            report["pass"] = false;
            report["stoppedAfter"] = "b";
            ResultsWriter.WriteJson(Path.Combine(outDir, "probe.json"), report);
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(report, SetManifest.Json));
            Console.WriteLine("probe: FAIL (stopped after check b)");
            return 1;
        }

        // (a) Words and positions: every page of V1 and V2 against its source page.
        var sourceWords = manifest.Parts.ToDictionary(p => p.Order, p => PageWords.Extract(SetManifest.PartPath(manifestPath, p), temp));
        var perPageWords = new List<string>();
        bool textOk = true;
        foreach (var (name, pdf, numbered) in new[] { ("V1", v1, false), ("V2", v2, true) })
        {
            var pages = PageWords.Extract(pdf, temp);
            textOk &= pages.Count == total;
            for (int i = 1; i <= Math.Min(total, pages.Count); i++)
            {
                var (part, k) = map[i - 1];
                var source = sourceWords[part][k - 1];
                var content = PageWords.ContentWords(pages[i - 1]);
                bool same = string.Join(' ', content) == PageWords.ContentText(source);
                var counters = PageWords.PageCounters(content);
                bool counterOk = counters.Count == 1 && counters[0] == (k, manifest.Part(part).Pages);
                string area = PageWords.AreaText(pages[i - 1]);
                bool numberAreaOk = numbered ? area == PageStamper.Text(i, total) : area.Length == 0;
                textOk &= same && counterOk && numberAreaOk;
                perPageWords.Add(string.Create(CultureInfo.InvariantCulture,
                    $"{name} page {i}: content words {(same ? "equal" : "DIFFER")} ({content.Count}), own counter {(counterOk ? "ok" : "WRONG")} ({string.Join(", ", counters.Select(c => $"Page {c.K} of {c.N}"))}), numbering area '{area}' {(numberAreaOk ? "ok" : "WRONG")}"));
            }
        }

        string qdf = Path.Combine(temp, "part1.qdf.pdf");
        ExternalTool.Run(Verifier.Qpdf, ["--qdf", "--object-streams=disable", part1, qdf]);
        string qdfText = Encoding.Latin1.GetString(File.ReadAllBytes(qdf));
        var stampWords = PageWords.Extract(v2, temp, 1, 1)[0].Words.Where(PageWords.CentreInArea).ToList();
        report["a_textAndPositions"] = new
        {
            pass = textOk,
            perPage = perPageWords,
            stampWordBoxes = stampWords.Select(w => string.Create(CultureInfo.InvariantCulture, $"'{w.Text}' x {w.XMin:F2}-{w.XMax:F2}, y {w.YMin:F2}-{w.YMax:F2}")).ToList(),
            sourceFirstPageWordsSample = sourceWords[1][0].Words.Take(12).Select(w => string.Create(CultureInfo.InvariantCulture, $"'{w.Text}' x {w.XMin:F2}-{w.XMax:F2}, y {w.YMin:F2}-{w.YMax:F2}")).ToList(),
            partFonts = new
            {
                toUnicodeEntries = Regex.Count(qdfText, "/ToUnicode"),
                type0Fonts = Regex.Count(qdfText, @"/Subtype\s*/Type0"),
                trueTypeFonts = Regex.Count(qdfText, @"/Subtype\s*/TrueType"),
                identityH = Regex.Count(qdfText, "/Identity-H"),
                winAnsi = Regex.Count(qdfText, "/WinAnsiEncoding"),
                fontNames = FontNamesPattern().Matches(qdfText).Select(m => m.Groups[1].Value).Distinct().ToList(),
            },
        };
        pass &= textOk;

        // (c) Closing each source right after its pages were added, and the structural check.
        var checkSource = QpdfCheck.Run(part1);
        var checkMerged = QpdfCheck.Run(v1);
        var checkStamped = QpdfCheck.Run(v2);
        var checkQpdfMerged = QpdfCheck.Run(v4);
        var checkStampedQpdf = QpdfCheck.Run(v5);
        bool collectable = SourceCollectableAfterDispose(part1);
        bool disposeOk = checkSource.Pass && checkMerged.Pass && checkStamped.Pass;
        report["c_sourceDisposal"] = new
        {
            pass = disposeOk,
            exemptMessage = QpdfCheck.ExemptTail,
            qpdfCheckSourcePart = checkSource,
            qpdfCheckV1 = checkMerged,
            qpdfCheckV2 = checkStamped,
            qpdfCheckV4 = checkQpdfMerged,
            qpdfCheckV5 = checkStampedQpdf,
            sourceCollectableWhileOutputOpen = collectable,
        };
        pass &= disposeOk;

        // (d) The numbering area is empty on every page of the parts: no word box touches it and every pixel is white.
        var areaPages = new List<string>();
        bool areaOk = true;
        foreach (var part in manifest.Parts)
        {
            foreach (var page in sourceWords[part.Order])
            {
                var touching = page.Words.Where(PageWords.IntersectsArea).Select(w => w.Text).ToList();
                long notWhite = PgmImage.Load(references[part.Order][page.Number]).NotWhite(PixelRect.NumberingArea)
                    + PgmImage.Load(rounded[part.Order][page.Number]).NotWhite(PixelRect.NumberingArea);
                var band = page.Words.Where(w => PageWords.InBottomBand(w, page.Height)).ToList();
                bool empty = touching.Count == 0 && notWhite == 0;
                areaOk &= empty;
                areaPages.Add(string.Create(CultureInfo.InvariantCulture,
                    $"part {part.Order} page {page.Number}: words in area {touching.Count}{(touching.Count > 0 ? " (" + string.Join(' ', touching) + ")" : string.Empty)}, non-white pixels on both references {notWhite}, bottom 15 mm band: {(band.Count == 0 ? "no word" : PageWords.BandPattern(band))}"));
            }
        }

        report["d_numberingAreaEmpty"] = new { pass = areaOk, pages = areaPages };
        pass &= areaOk;

        Directory.Delete(temp, true);
        report["pass"] = pass;
        ResultsWriter.WriteJson(Path.Combine(outDir, "probe.json"), report);
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(report, SetManifest.Json));
        Console.WriteLine($"probe: {(pass ? "PASS" : "FAIL")}");
        return pass ? 0 : 1;
    }

    /// <summary>The /MediaBox and /CropBox of a page as written in the file (from the parent when the page has none).</summary>
    private static string PageBox(string pdf, int page)
    {
        string listing = ExternalTool.Output(Verifier.Qpdf, "--show-pages", pdf);
        var match = PageObjectPattern().Match(listing.Split('\n').FirstOrDefault(l => l.StartsWith(string.Create(CultureInfo.InvariantCulture, $"page {page}:"), StringComparison.Ordinal)) ?? string.Empty);
        if (!match.Success)
        {
            return "page object not found";
        }

        string dictionary = ExternalTool.Output(Verifier.Qpdf, "--show-object=" + match.Groups[1].Value, pdf);
        string media = BoxPattern("MediaBox").Match(dictionary) is { Success: true } m ? m.Value : "no /MediaBox on the page";
        string crop = BoxPattern("CropBox").Match(dictionary) is { Success: true } c ? c.Value : "no /CropBox";
        if (!media.StartsWith('/'))
        {
            var parent = ParentPattern().Match(dictionary);
            if (parent.Success)
            {
                string parentDictionary = ExternalTool.Output(Verifier.Qpdf, "--show-object=" + parent.Groups[1].Value, pdf);
                media += BoxPattern("MediaBox").Match(parentDictionary) is { Success: true } pm ? "; parent " + pm.Value : "; none on the parent";
            }
        }

        return $"object {match.Groups[1].Value}: {media}; {crop}";
    }

    private static Regex BoxPattern(string name) => new(@"/" + name + @"\s*\[[^\]]*\]", RegexOptions.CultureInvariant);

    [GeneratedRegex(@"^page \d+: (\d+) 0 R")]
    private static partial Regex PageObjectPattern();

    [GeneratedRegex(@"/Parent (\d+) 0 R")]
    private static partial Regex ParentPattern();

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool SourceCollectableAfterDispose(string path)
    {
        using var output = new PdfDocument();
        var weak = ImportAndClose(output, path);
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        bool collectable = !weak.IsAlive;
        output.Save(new MemoryStream());
        return collectable;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference ImportAndClose(PdfDocument output, string path)
    {
        var source = PdfReader.Open(path, PdfDocumentOpenMode.Import);
        for (int k = 0; k < source.PageCount; k++)
        {
            output.AddPage(source.Pages[k]);
        }

        var weak = new WeakReference(source);
        source.Dispose();
        return weak;
    }

    /// <summary>Object numbers of the content streams of one page, from "qpdf --show-pages".</summary>
    private static List<int> ContentStreams(string pdf, int page)
    {
        string listing = ExternalTool.Output(Verifier.Qpdf, "--show-pages", pdf);
        var result = new List<int>();
        bool inPage = false;
        foreach (string raw in listing.Split('\n'))
        {
            string line = raw.TrimEnd();
            if (line.StartsWith("page ", StringComparison.Ordinal))
            {
                inPage = line.StartsWith(string.Create(CultureInfo.InvariantCulture, $"page {page}:"), StringComparison.Ordinal);
                continue;
            }

            var match = ObjectReferencePattern().Match(line);
            if (inPage && match.Success)
            {
                result.Add(int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture));
            }
        }

        return result;
    }

    /// <summary>Decoded data of one stream; qpdf warnings (exit 3) are tolerated here because this only describes the stream.</summary>
    private static string StreamData(string pdf, int objectNumber)
    {
        var result = ExternalTool.Run(Verifier.Qpdf, [string.Create(CultureInfo.InvariantCulture, $"--show-object={objectNumber}"), "--filtered-stream-data", pdf]);
        if (result.ExitCode is not (0 or 3))
        {
            throw new InvalidOperationException($"qpdf could not decode object {objectNumber}: {result.StandardError.Trim()}");
        }

        return result.StandardOutput;
    }

    private static string Summarize(string pdf, int objectNumber)
    {
        string data = StreamData(pdf, objectNumber).Replace('\n', ' ').Trim();
        string head = data.Length > 60 ? data[..60] + " ..." : data;
        string tail = data.Length > 60 ? " ... " + data[^40..] : string.Empty;
        return string.Create(CultureInfo.InvariantCulture, $"object {objectNumber}, {data.Length} chars: {head}{tail}");
    }

    [GeneratedRegex(@"^\s+(\d+) 0 R$")]
    private static partial Regex ObjectReferencePattern();

    [GeneratedRegex(@"/BaseFont\s*/([A-Za-z0-9+\-]+)")]
    private static partial Regex FontNamesPattern();
}
