using System.Globalization;
using System.Text.RegularExpressions;

namespace PdfAssemblySpike;

/// <summary>Outcome of the checks on one output file.</summary>
internal sealed record VerificationResult(
    string Pdf,
    string Mode,
    bool Numbered,
    QpdfCheckRecord QpdfCheck,
    int PageCount,
    int ExpectedPages,
    bool StructurePass,
    int TextPagesChecked,
    bool OrderPass,
    bool NumberingPass,
    int PixelPagesChecked,
    bool PixelPass,
    long MaxChangedOutside,
    long MinChangedInside,
    long MaxChangedInside,
    int? FirstFailingPage,
    string? FirstFailure,
    IReadOnlyList<int> PixelPages,
    OutputBoxes Boxes,
    StrictCounts Strict,
    [property: System.Text.Json.Serialization.JsonIgnore] IReadOnlyList<PixelPageRecord> PixelPageRecords)
{
    public bool Pass => StructurePass && OrderPass && NumberingPass && PixelPass && Boxes.Pass;
}

/// <summary>
/// The page boxes of one output under its rule: the distinct values as qpdf wrote
/// them, the pages per classification, the verdict and the first page outside the table.
/// </summary>
internal sealed record OutputBoxes(
    string Rule,
    int Pages,
    IReadOnlyList<BoxValueCount> Values,
    IReadOnlyList<BoxValueCount> CropBoxValues,
    IReadOnlyDictionary<string, int> Classes,
    string Verdict,
    int? FirstBoxDiffersPage)
{
    public bool Pass => Verdict != "fail";
}

/// <summary>The comparison of the checked pages with the unedited source render: information only, never a verdict.</summary>
internal sealed record StrictCounts(int Pages, long MinOutside, long MaxOutside, long SumOutside, long MinInside, long MaxInside);

/// <summary>One checked page: the reference it was compared with and both comparisons.</summary>
internal sealed record PixelPageRecord(int Page, string Reference, long MatchingOutside, long MatchingInside, long StrictOutside, long StrictInside);

/// <summary>Structural check and page count of one source part.</summary>
internal sealed record SourcePartCheck(int Order, string File, int ManifestPages, int PageCount, QpdfCheckRecord QpdfCheck, bool Pass);

/// <summary>Structural check totals over the source parts of one set.</summary>
internal sealed record SourcePartTotals(
    int TargetPages,
    int Parts,
    int PassClean,
    int PassExempt,
    int Fail,
    int MinWarningLines,
    int MaxWarningLines,
    long SumWarningLines,
    long SumExemptWarningLines);

/// <summary>One control of the structural check: what it fed in, what it expected and what it got.</summary>
internal sealed record ControlResult(
    string Id,
    string Input,
    string Expected,
    string Actual,
    bool AsExpected,
    int ExitCode,
    int WarningLines,
    int ExemptWarningLines,
    string FailReason,
    string FirstOffendingLine,
    string Note,
    string Group = "",
    bool Counted = true,
    object? Detail = null);

/// <summary>The content digest of one part: SHA-256 of its pages' content words.</summary>
internal sealed record PartContent(int Order, string File, int Pages, int FirstGlobalPage, string ContentSha256);

/// <summary>A footer pattern found in the earlier experiment's bottom band, with the pages that carry it.</summary>
internal sealed record BandPatternCount(string Pattern, int Pages);

/// <summary>The checks of one set of Chromium parts made before anything is measured.</summary>
internal sealed record FixtureCheck(
    string Set,
    int Parts,
    int Pages,
    string Area,
    string PixelArea,
    bool AreaEmpty,
    int PagesWithWordInArea,
    string? FirstWordInArea,
    int PixelPagesChecked,
    int PixelPagesNotWhite,
    long MaxNotWhitePixels,
    string? FirstNotWhitePage,
    bool PagesUnique,
    int DuplicatePages,
    string? FirstDuplicate,
    bool OwnCounter,
    int OwnCounterFailures,
    string? FirstOwnCounterFailure,
    int BottomBandPages,
    int BottomBandDistinctPatterns,
    IReadOnlyList<BandPatternCount> BottomBandPatterns,
    IReadOnlyList<PartContent> PartContents,
    string ContentSetDigest,
    bool BoxesPass,
    string? FirstBoxFailure,
    IReadOnlyList<BoxValueCount> SourceBoxValues,
    int RoundedPixelPagesNotWhite,
    IReadOnlyList<RoundedCopy> RoundedCopies)
{
    public bool Pass => AreaEmpty && PagesUnique && OwnCounter && BoxesPass;
}

/// <summary>
/// Checks an output against its set manifest only, never against the state of
/// the code that produced it: structure (qpdf), page order and page numbers
/// (text extracted by poppler) and a strict pixel comparison of rendered pages
/// against the source pages (poppler renderer).
/// </summary>
internal static partial class Verifier
{
    public const string Qpdf = "qpdf";
    public const string PdfToText = "pdftotext";
    public const string PdfToPpm = "pdftoppm";
    public const int RenderChunk = 50;
    public const int SampleCount = 30;
    public const ulong SampleSeed = 30;

    public static VerificationResult Verify(string manifestPath, string pdf, bool numbered, string mode, string tempDir, string keepDir, string boxRule, string? roundedDir)
    {
        var manifest = SetManifest.Load(manifestPath);
        Directory.CreateDirectory(tempDir);
        int expected = manifest.TargetPages;

        // 1. Structure.
        var check = QpdfCheck.Run(pdf);
        int pageCount = PageCount(pdf);
        bool structurePass = check.Pass && pageCount == expected;
        int? firstFailingPage = null;
        string? firstFailure = structurePass ? null
            : $"qpdf --check exit {check.ExitCode} {check.Verdict} {check.FailReason} '{check.FirstOffendingLine}', {pageCount} pages (expected {expected})";

        // 1b. Page boxes of every page, read with qpdf in decimal arithmetic, judged by the fixed table.
        var boxes = PageBoxes.Read(pdf);
        var classes = boxes.Select(b => PageBoxes.Classify(b.Numbers, boxRule)).ToList();
        int differsIndex = classes.IndexOf(PageBoxes.Differs);
        int? firstDiffers = differsIndex >= 0 ? differsIndex + 1 : null;
        var outputBoxes = new OutputBoxes(boxRule, boxes.Count, PageBoxes.Distinct(boxes.Select(b => b.Text)),
            PageBoxes.Distinct(boxes.Where(b => b.CropText is not null).Select(b => b.CropText!)), PageBoxes.Counts(classes),
            PageBoxes.Verdict(classes), firstDiffers);
        if (firstDiffers is int dp)
        {
            Fail(ref firstFailingPage, ref firstFailure, dp, $"page {dp}: page box [{boxes[dp - 1].Text}] is outside the table ({boxRule})");
        }

        if (mode == "structure")
        {
            return new VerificationResult(pdf, mode, numbered, check, pageCount, expected, structurePass,
                0, true, true, 0, true, 0, 0, 0, firstFailingPage, firstFailure, [], outputBoxes, new StrictCounts(0, 0, 0, 0, 0, 0), []);
        }

        // 2. Order and numbering, every page: the content words of each output page
        // against the source page the manifest names; the numbering area on its own.
        var map = manifest.PageMap();
        bool orderPass = pageCount == expected;
        bool numberingPass = orderPass;
        int textChecked = 0;
        var sources = new SourceWordCache(manifestPath, manifest, Path.Combine(tempDir, "words"));
        for (int chunkStart = 1; chunkStart <= expected; chunkStart += PageWords.ExtractChunk)
        {
            int chunkEnd = Math.Min(expected, chunkStart + PageWords.ExtractChunk - 1);
            var extracted = PageWords.Extract(pdf, Path.Combine(tempDir, "words"), chunkStart, chunkEnd);
            for (int i = chunkStart; i <= chunkEnd; i++)
            {
                int index = i - chunkStart;
                if (index >= extracted.Count)
                {
                    orderPass = false;
                    numberingPass = false;
                    Fail(ref firstFailingPage, ref firstFailure, i, $"page {i}: no words extracted");
                    continue;
                }

                textChecked++;
                var page = extracted[index];
                var (part, k) = map[i - 1];
                var source = sources.Get(part)[k - 1];
                var content = PageWords.ContentWords(page);
                string contentText = string.Join(' ', content);
                if (!string.Equals(contentText, source.ContentText, StringComparison.Ordinal))
                {
                    orderPass = false;
                    Fail(ref firstFailingPage, ref firstFailure, i, $"page {i}: content words differ from part {part} page {k} ({content.Count} words, source {source.WordCount}): {FirstDifference(contentText, source.ContentText)}");
                }

                var counters = PageWords.PageCounters(content);
                int partPages = manifest.Part(part).Pages;
                if (counters.Count != 1 || counters[0] != (k, partPages))
                {
                    orderPass = false;
                    Fail(ref firstFailingPage, ref firstFailure, i, $"page {i}: {counters.Count} own page counters ({string.Join(", ", counters.Select(c => $"Page {c.K} of {c.N}"))}), expected one 'Page {k} of {partPages}'");
                }

                string area = PageWords.AreaText(page);
                bool numberOk = numbered ? area == PageStamper.Text(i, expected) : area.Length == 0;
                if (!numberOk)
                {
                    numberingPass = false;
                    Fail(ref firstFailingPage, ref firstFailure, i, $"page {i}: numbering area holds '{area}', expected {(numbered ? "'" + PageStamper.Text(i, expected) + "'" : "no word")}");
                }
            }
        }

        // 3. Pixel comparison against the matching reference of each page: the unedited
        // source page for the source box, the rounded copy's page for the rounded box. The
        // comparison with the unedited source page is also recorded, as information.
        List<int> pixelPages = mode == "all" ? [.. Enumerable.Range(1, expected)] : SamplePages(manifest);
        bool pixelPass = true;
        long maxOutside = 0;
        long minInside = long.MaxValue;
        long maxInside = 0;
        int pixelChecked = 0;
        var records = new List<PixelPageRecord>();
        string renderDir = Path.Combine(tempDir, "render");
        Directory.CreateDirectory(renderDir);
        string roundedFolder = roundedDir ?? Path.Combine(tempDir, "ref-rounded");
        var references = new ReferenceCache(part => SetManifest.PartPath(manifestPath, manifest.Part(part)), manifest, renderDir, "ref");
        var roundedReferences = new ReferenceCache(part => RoundedPath(manifestPath, manifest.Part(part), roundedFolder), manifest, renderDir, "rnd");
        try
        {
            var groups = mode == "all"
                ? pixelPages.Chunk(RenderChunk).Select(c => (c[0], c[^1])).ToList()
                : pixelPages.Select(p => (p, p)).ToList();
            foreach (var (first, last) in groups)
            {
                var rendered = Render(pdf, first, last, Path.Combine(renderDir, "out"));
                for (int i = first; i <= last; i++)
                {
                    var (part, k) = map[i - 1];
                    string pageClass = i <= classes.Count ? classes[i - 1] : PageBoxes.Differs;
                    if (pageClass == PageBoxes.Differs)
                    {
                        // A page outside the box table gets no reference; it already failed the box rule.
                        pixelPass = false;
                        records.Add(new PixelPageRecord(i, "none (box-differs)", -1, -1, -1, -1));
                        continue;
                    }

                    string uneditedPath = references.Get(part, k, single: mode != "all");
                    string referencePath = pageClass == PageBoxes.Rounded ? roundedReferences.Get(part, k, single: mode != "all") : uneditedPath;
                    if (!rendered.TryGetValue(i, out string? outputPath))
                    {
                        pixelPass = false;
                        Fail(ref firstFailingPage, ref firstFailure, i, $"page {i}: not rendered");
                        continue;
                    }

                    var output = PgmImage.Load(outputPath);
                    var diff = PgmImage.Compare(PgmImage.Load(referencePath), output, PixelRect.NumberingArea);
                    var strict = pageClass == PageBoxes.Rounded ? PgmImage.Compare(PgmImage.Load(uneditedPath), output, PixelRect.NumberingArea) : diff;
                    records.Add(new PixelPageRecord(i, pageClass == PageBoxes.Rounded ? "rounded" : "unedited",
                        diff.SameSize ? diff.ChangedOutside : -1, diff.SameSize ? diff.ChangedInside : -1,
                        strict.SameSize ? strict.ChangedOutside : -1, strict.SameSize ? strict.ChangedInside : -1));
                    pixelChecked++;
                    maxOutside = Math.Max(maxOutside, diff.ChangedOutside);
                    minInside = Math.Min(minInside, diff.ChangedInside);
                    maxInside = Math.Max(maxInside, diff.ChangedInside);
                    bool ok = diff.SameSize && diff.ChangedOutside == 0 && (numbered ? diff.ChangedInside >= 1 : diff.ChangedInside == 0);
                    if (!ok)
                    {
                        if (pixelPass)
                        {
                            Directory.CreateDirectory(keepDir);
                            string name = Path.GetFileNameWithoutExtension(pdf);
                            File.Copy(referencePath, Path.Combine(keepDir, string.Create(CultureInfo.InvariantCulture, $"{name}-page{i}-reference.pgm")), true);
                            File.Copy(outputPath, Path.Combine(keepDir, string.Create(CultureInfo.InvariantCulture, $"{name}-page{i}-output.pgm")), true);
                        }

                        pixelPass = false;
                        Fail(ref firstFailingPage, ref firstFailure, i, $"page {i}: same size {diff.SameSize}, {diff.ChangedOutside} changed outside, {diff.ChangedInside} inside the numbering area");
                    }
                }

                foreach (string file in rendered.Values)
                {
                    File.Delete(file);
                }
            }
        }
        finally
        {
            references.Dispose();
            roundedReferences.Dispose();
            Directory.Delete(renderDir, recursive: true);
            if (roundedDir is null && Directory.Exists(roundedFolder))
            {
                Directory.Delete(roundedFolder, recursive: true);
            }
        }

        var strictValid = records.Where(r => r.StrictOutside >= 0).ToList();
        var strictCounts = strictValid.Count == 0 ? new StrictCounts(0, 0, 0, 0, 0, 0)
            : new StrictCounts(strictValid.Count, strictValid.Min(r => r.StrictOutside), strictValid.Max(r => r.StrictOutside), strictValid.Sum(r => r.StrictOutside),
                strictValid.Min(r => r.StrictInside), strictValid.Max(r => r.StrictInside));
        return new VerificationResult(pdf, mode, numbered, check, pageCount, expected, structurePass,
            textChecked, orderPass, numberingPass, pixelChecked, pixelPass, maxOutside, minInside == long.MaxValue ? 0 : minInside, maxInside,
            firstFailingPage, firstFailure, mode == "all" ? [] : pixelPages, outputBoxes, strictCounts, records);
    }

    /// <summary>
    /// The rounded copy of a part in the given folder; built there under the
    /// construction checks when it does not exist yet. A failed construction stops.
    /// </summary>
    public static string RoundedPath(string manifestPath, PartEntry part, string folder)
    {
        string path = Path.Combine(folder, part.File);
        if (!File.Exists(path))
        {
            var built = PageBoxes.BuildRounded(part.Order, SetManifest.PartPath(manifestPath, part), path);
            if (!built.Pass)
            {
                throw new InvalidDataException($"the rounded copy of part {part.Order} ({part.File}) cannot be built: {built.Failure}");
            }
        }

        return path;
    }

    /// <summary>Page 1, the last page, the first page of the middle part and 27 more distinct seeded pages, sorted.</summary>
    public static List<int> SamplePages(SetManifest manifest)
    {
        int total = manifest.TargetPages;
        var chosen = new SortedSet<int> { 1, total, manifest.Parts[manifest.Parts.Count / 2].FirstGlobalPage };
        var random = new SplitMix64(SampleSeed);
        while (chosen.Count < Math.Min(SampleCount, total))
        {
            chosen.Add(random.NextInt(1, total + 1));
        }

        return [.. chosen];
    }

    /// <summary>Renders pages first..last to greyscale PGM at 72 dpi; returns page number → file.</summary>
    public static Dictionary<int, string> Render(string pdf, int first, int last, string prefix)
    {
        ExternalTool.Output(PdfToPpm, "-gray", "-r", "72",
            "-f", first.ToString(CultureInfo.InvariantCulture), "-l", last.ToString(CultureInfo.InvariantCulture), pdf, prefix);
        string dir = Path.GetDirectoryName(prefix)!;
        string stem = Path.GetFileName(prefix) + "-";
        var result = new Dictionary<int, string>();
        foreach (string file in Directory.GetFiles(dir, stem + "*.pgm"))
        {
            string number = Path.GetFileNameWithoutExtension(file)[stem.Length..];
            if (int.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out int page))
            {
                result[page] = file;
            }
        }

        return result;
    }

    private static void Fail(ref int? firstPage, ref string? firstMessage, int page, string message)
    {
        if (firstPage is null || page < firstPage)
        {
            firstPage = page;
            firstMessage = message;
        }
    }

    /// <summary>Page count reported by qpdf; -1 when qpdf cannot report one.</summary>
    public static int PageCount(string pdf)
    {
        var result = ExternalTool.Run(Qpdf, ["--show-npages", pdf]);
        return result.ExitCode == 0 && int.TryParse(result.StandardOutput.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int pages)
            ? pages
            : -1;
    }

    /// <summary>
    /// Structural check of every source part of a set, with its page count, and
    /// the totals per set. A part that fails makes every output built from it fail.
    /// </summary>
    public static (bool Pass, List<SourcePartCheck> Parts, SourcePartTotals Totals) CheckSourceParts(string manifestPath)
    {
        var manifest = SetManifest.Load(manifestPath);
        var parts = new List<SourcePartCheck>();
        foreach (var part in manifest.Parts)
        {
            string path = SetManifest.PartPath(manifestPath, part);
            var check = QpdfCheck.Run(path);
            int pages = PageCount(path);
            parts.Add(new SourcePartCheck(part.Order, part.File, part.Pages, pages, check, check.Pass && pages == part.Pages));
        }

        var totals = new SourcePartTotals(
            manifest.TargetPages,
            parts.Count,
            parts.Count(p => p.QpdfCheck.Verdict == QpdfCheck.PassClean),
            parts.Count(p => p.QpdfCheck.Verdict == QpdfCheck.PassExempt),
            parts.Count(p => !p.Pass),
            parts.Count == 0 ? 0 : parts.Min(p => p.QpdfCheck.WarningLines),
            parts.Count == 0 ? 0 : parts.Max(p => p.QpdfCheck.WarningLines),
            parts.Sum(p => (long)p.QpdfCheck.WarningLines),
            parts.Sum(p => (long)p.QpdfCheck.ExemptWarningLines));
        return (parts.All(p => p.Pass), parts, totals);
    }

    public const string PdfSharpGroup = "pdfsharp";
    public const string ChromiumGroup = "chromium";

    /// <summary>
    /// The 29 counted controls: the earlier experiment's eighteen on its PDF-library
    /// sample part, five on the first part of the Chromium set, and six of the page-box
    /// rule on that part and its rounded copy (plus one recorded, not counted).
    /// Returns true only when every counted control has its expected result.
    /// </summary>
    public static (bool Pass, List<ControlResult> Controls) RunControls(string manifestPath, string fixturePath, string tempDir)
    {
        var manifest = SetManifest.Load(manifestPath);
        string chromiumPart = SetManifest.PartPath(manifestPath, manifest.Parts[0]);
        var controls = new List<ControlResult>();
        controls.AddRange(RunPdfSharpControls(fixturePath, Path.Combine(tempDir, PdfSharpGroup)).Select(c => c with { Group = PdfSharpGroup }));
        controls.AddRange(RunChromiumControls(chromiumPart, manifest.Parts[0].File, Path.Combine(tempDir, ChromiumGroup)).Select(c => c with { Group = ChromiumGroup }));
        controls.AddRange(PageBoxes.RunControls(chromiumPart, manifest.Parts[0].File, Path.Combine(tempDir, PageBoxes.PageBoxGroup)));
        return (controls.Where(c => c.Counted).All(c => c.AsExpected), controls);
    }

    /// <summary>
    /// Five controls on a Chromium part: the untouched part passes (clean or exempt,
    /// recorded), its uncompressed rewrite is clean, and three damaged copies fail.
    /// </summary>
    public static List<ControlResult> RunChromiumControls(string part, string fileName, string tempDir)
    {
        Directory.CreateDirectory(tempDir);
        var controls = new List<ControlResult>();

        // PC-1c: the untouched part; which pass it gets is the finding, not a threshold.
        var pc1 = QpdfCheck.Run(part);
        controls.Add(new ControlResult("PC-1c", $"untouched part {fileName}", QpdfCheck.PassClean + " or " + QpdfCheck.PassExempt, pc1.Verdict, pc1.Pass,
            pc1.ExitCode, pc1.WarningLines, pc1.ExemptWarningLines, pc1.FailReason, pc1.FirstOffendingLine, string.Empty));

        // PC-2c: every stream written uncompressed by qpdf.
        string pc2 = Path.Combine(tempDir, "pc-2c.pdf");
        var rewrite = ExternalTool.Run(Qpdf, ["--stream-data=uncompress", part, pc2]);
        controls.Add(rewrite.ExitCode is 0 or 3 && File.Exists(pc2)
            ? Control("PC-2c", $"qpdf --stream-data=uncompress of {fileName} (rewrite exit {rewrite.ExitCode})", QpdfCheck.PassClean, QpdfCheck.Run(pc2), null)
            : ControlError("PC-2c", "qpdf --stream-data=uncompress rewrite", QpdfCheck.PassClean, $"rewrite exited {rewrite.ExitCode}, expected 0 or 3"));
        if (File.Exists(pc2))
        {
            File.Delete(pc2);
        }

        controls.AddRange(DamagedCopyControls(File.ReadAllBytes(part), tempDir, "c"));
        return controls;
    }

    /// <summary>
    /// The earlier experiment's eighteen controls, built from its PDF-library sample
    /// part: the check must still accept what it claims to accept and still reject
    /// damaged files.
    /// </summary>
    public static List<ControlResult> RunPdfSharpControls(string part, string tempDir)
    {
        Directory.CreateDirectory(tempDir);
        var controls = new List<ControlResult>();
        string fileName = Path.GetFileName(part);

        // PC-1: the untouched part. Its raw transcript also feeds the in-memory cases.
        var raw = ExternalTool.Run(Qpdf, ["--check", part], QpdfCheck.Timeout, null, TimeSpan.FromMilliseconds(200));
        var pc1 = QpdfCheck.Classify(part, raw.ExitCode, raw.StandardOutput, raw.StandardError);
        bool pc1Counts = pc1.WarningLines >= 1 && pc1.ExemptWarningLines == pc1.WarningLines;
        controls.Add(Control("PC-1", $"untouched part {fileName}", QpdfCheck.PassExempt, pc1,
            pc1Counts ? null : "warningLines must be at least 1 and all exempt"));

        // PC-2: every stream written uncompressed by qpdf.
        string pc2 = Path.Combine(tempDir, "pc-2.pdf");
        var rewrite = ExternalTool.Run(Qpdf, ["--stream-data=uncompress", part, pc2]);
        controls.Add(rewrite.ExitCode == 3 && File.Exists(pc2)
            ? Control("PC-2", $"qpdf --stream-data=uncompress of {fileName} (rewrite exit {rewrite.ExitCode})", QpdfCheck.PassClean, QpdfCheck.Run(pc2), null)
            : ControlError("PC-2", "qpdf --stream-data=uncompress rewrite", QpdfCheck.PassClean, $"rewrite exited {rewrite.ExitCode}, expected 3"));

        controls.AddRange(DamagedCopyControls(File.ReadAllBytes(part), tempDir, string.Empty));

        // NC-4: edge cases of the classifier, from PC-1's real transcript changed one way at a time.
        controls.AddRange(TranscriptControls(part, raw));

        if (File.Exists(pc2))
        {
            File.Delete(pc2);
        }

        return controls;
    }

    /// <summary>
    /// NC-1 to NC-3 on copies of a file: eight bytes 0xFF written into the first
    /// stream's data, the first half of the file, the first stream length falsified.
    /// The suffix names the group ("c" for the Chromium part). Copies are deleted.
    /// </summary>
    private static List<ControlResult> DamagedCopyControls(byte[] bytes, string tempDir, string suffix)
    {
        var controls = new List<ControlResult>();
        string nc1 = Path.Combine(tempDir, $"nc-1{suffix}.pdf");
        string nc2 = Path.Combine(tempDir, $"nc-2{suffix}.pdf");
        string nc3 = Path.Combine(tempDir, $"nc-3{suffix}.pdf");

        // NC-1: eight bytes of 0xFF written into the first stream's compressed data.
        int dataStart = FirstStreamDataStart(bytes);
        if (dataStart >= 0 && dataStart + 11 <= bytes.Length)
        {
            byte[] corrupted = (byte[])bytes.Clone();
            Array.Fill(corrupted, (byte)0xFF, dataStart + 3, 8);
            File.WriteAllBytes(nc1, corrupted);
            controls.Add(Control("NC-1" + suffix, string.Create(CultureInfo.InvariantCulture, $"8 bytes 0xFF at byte {dataStart + 3} (stream data starts at {dataStart})"),
                QpdfCheck.Fail, QpdfCheck.Run(nc1), null));
        }
        else
        {
            controls.Add(ControlError("NC-1" + suffix, "corrupted stream", QpdfCheck.Fail, "no stream keyword followed by an end of line was found"));
        }

        // NC-2: the first half of the file.
        File.WriteAllBytes(nc2, bytes[..(bytes.Length / 2)]);
        controls.Add(Control("NC-2" + suffix, string.Create(CultureInfo.InvariantCulture, $"first {bytes.Length / 2} of {bytes.Length} bytes"),
            QpdfCheck.Fail, QpdfCheck.Run(nc2), null));

        // NC-3: the first stream length falsified.
        string latin = System.Text.Encoding.Latin1.GetString(bytes);
        var length = LengthPattern().Match(latin);
        if (length.Success)
        {
            string falsified = string.Concat(latin.AsSpan(0, length.Index), "/Length 5", latin.AsSpan(length.Index + length.Length));
            File.WriteAllBytes(nc3, System.Text.Encoding.Latin1.GetBytes(falsified));
            controls.Add(Control("NC-3" + suffix, $"'{length.Value}' replaced by '/Length 5'", QpdfCheck.Fail, QpdfCheck.Run(nc3), null));
        }
        else
        {
            controls.Add(ControlError("NC-3" + suffix, "falsified length", QpdfCheck.Fail, "no /Length entry was found"));
        }

        foreach (string file in new[] { nc1, nc2, nc3 })
        {
            if (File.Exists(file))
            {
                File.Delete(file);
            }
        }

        return controls;
    }

    private static List<ControlResult> TranscriptControls(string path, ToolResult raw)
    {
        var output = QpdfCheck.Lines(raw.StandardOutput);
        var error = QpdfCheck.Lines(raw.StandardError);
        int firstWarning = error.FindIndex(l => l.StartsWith("WARNING:", StringComparison.Ordinal));
        int closing = error.FindIndex(l => string.Equals(l, QpdfCheck.ClosingLine, StringComparison.Ordinal));
        int lastWarning = error.FindLastIndex(l => l.StartsWith("WARNING:", StringComparison.Ordinal));
        string exemptSuffix = ": " + QpdfCheck.ExemptTail;
        const string OtherPath = "/work/other/part.pdf";
        var results = new List<ControlResult>();
        if (raw.ExitCode != 3 || firstWarning < 0 || closing < 0 || lastWarning == firstWarning || output.Count == 0)
        {
            results.Add(ControlError("NC-4", "classifier edge cases", QpdfCheck.Fail,
                "PC-1's transcript is not an exit-3 transcript with at least two warnings, a closing line and standard output"));
            return results;
        }

        List<string> Edit(List<string> lines, Action<List<string>> change)
        {
            var copy = new List<string>(lines);
            change(copy);
            return copy;
        }

        string firstLine = error[firstWarning];
        int tailStart = firstLine.Length - QpdfCheck.ExemptTail.Length;
        var cases = new List<(string Id, string Input, int Exit, List<string> Output, List<string> Error)>
        {
            ("NC-4.i", "exempt text followed by extra text", 3, output,
                Edit(error, e => e[firstWarning] = firstLine + "; more")),
            ("NC-4.ii", "one warning replaced by '(offset 1): expected endstream'", 3, output,
                Edit(error, e => e[firstWarning] = $"WARNING: {path} (offset 1): expected endstream")),
            ("NC-4.iii", "every warning line removed", 3, output,
                Edit(error, e => e.RemoveAll(l => l.StartsWith("WARNING:", StringComparison.Ordinal)))),
            ("NC-4.iv", "an ERROR line added before the closing line", 3, output,
                Edit(error, e => e.Insert(closing, "ERROR: page 1: content stream (content stream object 5 0): errors while decoding content stream"))),
            ("NC-4.v", "closing line removed", 3, output,
                Edit(error, e => e.RemoveAt(closing))),
            ("NC-4.vi", "one warning naming another file", 3, output,
                Edit(error, e => e[firstWarning] = $"WARNING: {OtherPath}{firstLine[("WARNING: ".Length + path.Length)..]}")),
            ("NC-4.vii", "exempt text differing in case only", 3, output,
                Edit(error, e => e[firstWarning] = string.Concat(firstLine.AsSpan(0, tailStart), "I", firstLine.AsSpan(tailStart + 1)))),
            ("NC-4.viii", "exit 0 with the warning lines kept", 0, output, error),
            ("NC-4.ix", "exit 0 with an ERROR line on standard output", 0,
                Edit(output, o => o.Add("ERROR: page 1: content stream (content stream object 5 0): errors while decoding content stream")), []),
            ("NC-4.x", "exit 3 with the checking line naming another file", 3,
                Edit(output, o => o[0] = "checking " + OtherPath), error),
            ("NC-4.xi", "exit 3 without the checking line", 3,
                Edit(output, o => o.RemoveAt(0)), error),
            ("NC-4.xii", "exit 3 with the closing line present but not last", 3, output,
                Edit(error, e =>
                {
                    e.RemoveAt(closing);
                    e.Insert(e.FindLastIndex(l => l.StartsWith("WARNING:", StringComparison.Ordinal)), QpdfCheck.ClosingLine);
                })),
            ("NC-4.xiii", "exit 3 with an exempt-text warning line on standard output", 3,
                Edit(output, o => o.Add(firstLine)), error),
        };

        bool firstLineExempt = tailStart > 2 && string.Equals(firstLine[(tailStart - 2)..], exemptSuffix, StringComparison.Ordinal);
        foreach (var (id, input, exit, o, e) in cases)
        {
            if (!firstLineExempt)
            {
                results.Add(ControlError(id, input, QpdfCheck.Fail, "PC-1's first warning line does not carry the exempt text"));
                continue;
            }

            var record = QpdfCheck.Classify(path, exit, string.Join('\n', o) + "\n", string.Join('\n', e) + "\n");
            results.Add(Control(id, input, QpdfCheck.Fail, record, null));
        }

        return results;
    }

    private static ControlResult Control(string id, string input, string expected, QpdfCheckRecord actual, string? extraCondition) =>
        new(id, input, expected, actual.Verdict, actual.Verdict == expected && extraCondition is null,
            actual.ExitCode, actual.WarningLines, actual.ExemptWarningLines, actual.FailReason, actual.FirstOffendingLine, extraCondition ?? string.Empty);

    private static ControlResult ControlError(string id, string input, string expected, string reason) =>
        new(id, input, expected, "control-error", false, -1, 0, 0, string.Empty, string.Empty, reason);

    /// <summary>
    /// Offset of the first stream's data: after the first "stream" keyword that
    /// is not part of "endstream" and is followed by LF or CR LF. -1 when absent.
    /// </summary>
    private static int FirstStreamDataStart(byte[] bytes)
    {
        ReadOnlySpan<byte> keyword = "stream"u8;
        for (int i = 0; i + keyword.Length < bytes.Length; i++)
        {
            if (!bytes.AsSpan(i, keyword.Length).SequenceEqual(keyword))
            {
                continue;
            }

            bool endstream = i >= 3 && bytes.AsSpan(i - 3, 3).SequenceEqual("end"u8);
            int after = i + keyword.Length;
            if (endstream)
            {
                continue;
            }

            if (bytes[after] == (byte)'\n')
            {
                return after + 1;
            }

            if (bytes[after] == (byte)'\r' && after + 1 < bytes.Length && bytes[after + 1] == (byte)'\n')
            {
                return after + 2;
            }
        }

        return -1;
    }

    [GeneratedRegex("/Length [0-9]+", RegexOptions.CultureInvariant)]
    private static partial Regex LengthPattern();


    /// <summary>
    /// The fixture checks of one set, before anything is measured: no word box of any
    /// page touches the numbering area and every pixel of it is white on every page the
    /// pixel check renders as a reference (all pages of a set of 1 000 pages or fewer,
    /// the sampled pages otherwise); no two pages have the same content words; every
    /// page carries exactly its own "Page k of n"; the bottom band's words are counted;
    /// the content digests are computed.
    /// </summary>
    public static FixtureCheck CheckFixture(string setName, string manifestPath, string tempDir, string roundedDir)
    {
        var manifest = SetManifest.Load(manifestPath);
        Directory.CreateDirectory(tempDir);

        // Page boxes: every source page carries the expected box, one /MediaBox per page,
        // and the rounded copy of every part passes every construction check.
        var roundedCopies = new List<RoundedCopy>();
        var sourceBoxTexts = new List<string>();
        string? firstBoxFailure = null;
        foreach (var part in manifest.Parts.OrderBy(p => p.Order))
        {
            var copy = PageBoxes.BuildRounded(part.Order, SetManifest.PartPath(manifestPath, part), Path.Combine(roundedDir, part.File));
            roundedCopies.Add(copy);
            sourceBoxTexts.AddRange(copy.SourceBoxesAsRead.SelectMany(b => Enumerable.Repeat(b.Box, b.Pages)));
            if (!copy.Pass)
            {
                firstBoxFailure ??= $"part {part.Order} ({part.File}): {copy.Failure}";
            }
        }

        var sourceBoxValues = sourceBoxTexts.GroupBy(t => t, StringComparer.Ordinal)
            .Select(g => new BoxValueCount(g.Key, g.Count())).ToList();
        int pagesWithAreaWords = 0;
        string? firstAreaWord = null;
        int duplicates = 0;
        string? firstDuplicate = null;
        int counterFailures = 0;
        string? firstCounterFailure = null;
        int bandPages = 0;
        var bandPatterns = new Dictionary<string, int>(StringComparer.Ordinal);
        var seen = new Dictionary<string, string>(StringComparer.Ordinal);
        var contents = new List<PartContent>();
        int totalPages = 0;
        foreach (var part in manifest.Parts.OrderBy(p => p.Order))
        {
            string path = SetManifest.PartPath(manifestPath, part);
            var pages = PageWords.Extract(path, Path.Combine(tempDir, "words"));
            var texts = new List<string>();
            foreach (var page in pages)
            {
                totalPages++;
                string where = string.Create(CultureInfo.InvariantCulture, $"part {part.Order} ({part.File}) page {page.Number}");
                var inArea = page.Words.FirstOrDefault(PageWords.IntersectsArea);
                if (inArea is not null)
                {
                    pagesWithAreaWords++;
                    firstAreaWord ??= string.Create(CultureInfo.InvariantCulture, $"{where}: '{inArea.Text}' at x {inArea.XMin:F2}-{inArea.XMax:F2}, y {inArea.YMin:F2}-{inArea.YMax:F2}");
                }

                var content = PageWords.ContentWords(page);
                string text = string.Join(' ', content);
                texts.Add(text);
                if (!seen.TryAdd(text, where))
                {
                    duplicates++;
                    firstDuplicate ??= $"{where} has the content words of {seen[text]}";
                }

                var counters = PageWords.PageCounters(content);
                if (counters.Count != 1 || counters[0] != (page.Number, part.Pages))
                {
                    counterFailures++;
                    firstCounterFailure ??= $"{where}: {counters.Count} counters ({string.Join(", ", counters.Select(c => $"Page {c.K} of {c.N}"))}), expected 'Page {page.Number} of {part.Pages}'";
                }

                var band = page.Words.Where(w => PageWords.InBottomBand(w, page.Height)).ToList();
                if (band.Count > 0)
                {
                    bandPages++;
                    string pattern = PageWords.BandPattern(band);
                    bandPatterns[pattern] = bandPatterns.GetValueOrDefault(pattern) + 1;
                }
            }

            if (pages.Count != part.Pages)
            {
                counterFailures++;
                firstCounterFailure ??= $"part {part.Order} ({part.File}): {pages.Count} pages extracted, manifest {part.Pages}";
            }

            contents.Add(new PartContent(part.Order, part.File, part.Pages, part.FirstGlobalPage, PageWords.ContentSha256(texts)));
        }

        // Pixels of the numbering area on the pages the pixel check renders as references.
        var area = PixelRect.NumberingArea;
        int pixelPages = 0;
        int notWhitePages = 0;
        int roundedNotWhitePages = 0;
        long maxNotWhite = 0;
        string? firstNotWhite = null;
        string renderDir = Path.Combine(tempDir, "render-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(renderDir);
        try
        {
            var map = manifest.PageMap();
            IEnumerable<(int Part, int First, int Last)> renders = manifest.TargetPages <= 1000
                ? manifest.Parts.Select(p => (p.Order, 1, p.Pages))
                : SamplePages(manifest).Select(g => (map[g - 1].Part, map[g - 1].Page, map[g - 1].Page));
            foreach (var (order, first, last) in renders)
            {
                var part = manifest.Part(order);
                string roundedCopy = Path.Combine(roundedDir, part.File);

                // Both references: the unedited part and, when its construction passed, its rounded copy.
                foreach (var (pdf, rounded) in File.Exists(roundedCopy)
                    ? new[] { (SetManifest.PartPath(manifestPath, part), false), (roundedCopy, true) }
                    : [(SetManifest.PartPath(manifestPath, part), false)])
                {
                    var rendered = Render(pdf, first, last, Path.Combine(renderDir, rounded ? "rnd" : "ref"));
                    for (int k = first; k <= last; k++)
                    {
                        if (!rounded)
                        {
                            pixelPages++;
                        }

                        long notWhite = rendered.TryGetValue(k, out string? file) ? PgmImage.Load(file).NotWhite(area) : long.MaxValue;
                        if (notWhite != 0)
                        {
                            notWhitePages++;
                            roundedNotWhitePages += rounded ? 1 : 0;
                            maxNotWhite = Math.Max(maxNotWhite, notWhite);
                            firstNotWhite ??= string.Create(CultureInfo.InvariantCulture,
                                $"part {order} ({part.File}) page {k}{(rounded ? " (rounded copy)" : string.Empty)}: {(notWhite == long.MaxValue ? "not rendered" : notWhite + " pixels not white")}");
                        }
                    }

                    foreach (string file in rendered.Values)
                    {
                        File.Delete(file);
                    }
                }
            }
        }
        finally
        {
            Directory.Delete(renderDir, recursive: true);
        }

        string setDigest = ChromiumInputs.SetDigest(contents.Select(c =>
            string.Create(CultureInfo.InvariantCulture, $"{c.Order}|{c.File}|{c.Pages}|{c.FirstGlobalPage}|{c.ContentSha256}")));
        var patterns = bandPatterns.OrderByDescending(p => p.Value).ThenBy(p => p.Key, StringComparer.Ordinal)
            .Take(20).Select(p => new BandPatternCount(p.Key, p.Value)).ToList();
        string areaText = string.Create(CultureInfo.InvariantCulture,
            $"x {PageLayout.NumberingAreaLeft:F2}-{PageLayout.NumberingAreaRight:F2} pt, y {PageLayout.NumberingAreaTop:F2}-{PageLayout.NumberingAreaBottom:F2} pt from the top-left corner");
        return new FixtureCheck(setName, manifest.Parts.Count, totalPages, areaText, area.ToString(),
            pagesWithAreaWords == 0 && notWhitePages == 0, pagesWithAreaWords, firstAreaWord, pixelPages, notWhitePages, maxNotWhite, firstNotWhite,
            duplicates == 0, duplicates, firstDuplicate, counterFailures == 0, counterFailures, firstCounterFailure,
            bandPages, bandPatterns.Count, patterns, contents, setDigest,
            firstBoxFailure is null, firstBoxFailure, sourceBoxValues, roundedNotWhitePages, roundedCopies);
    }

    /// <summary>The first differing word position of two word sequences, with a few words of context.</summary>
    private static string FirstDifference(string actual, string expected)
    {
        string[] a = actual.Split(' ');
        string[] e = expected.Split(' ');
        int i = 0;
        while (i < a.Length && i < e.Length && string.Equals(a[i], e[i], StringComparison.Ordinal))
        {
            i++;
        }

        string Around(string[] words) => string.Join(' ', words.Skip(Math.Max(0, i - 2)).Take(5));
        return string.Create(CultureInfo.InvariantCulture, $"word {i + 1}: '{Around(a)}' vs source '{Around(e)}'");
    }

    /// <summary>One source page's content words (joined by one space) and their count.</summary>
    internal sealed record SourceWordPage(string ContentText, int WordCount);

    /// <summary>Extracts the words of each source part once and keeps its pages' content text.</summary>
    private sealed class SourceWordCache(string manifestPath, SetManifest manifest, string tempDir)
    {
        private readonly Dictionary<int, List<SourceWordPage>> _parts = [];

        public List<SourceWordPage> Get(int part)
        {
            if (!_parts.TryGetValue(part, out var pages))
            {
                pages = [.. PageWords.Extract(SetManifest.PartPath(manifestPath, manifest.Part(part)), tempDir)
                    .Select(p =>
                    {
                        var words = PageWords.ContentWords(p);
                        return new SourceWordPage(string.Join(' ', words), words.Count);
                    })];
                _parts[part] = pages;
            }

            return pages;
        }
    }

    /// <summary>
    /// Renders source pages on demand. In "all" mode a whole part is rendered when
    /// its first page is needed and dropped when the next part starts.
    /// </summary>
    private sealed class ReferenceCache(Func<int, string> partPath, SetManifest manifest, string renderDir, string prefix) : IDisposable
    {
        private readonly Dictionary<int, string> _pages = [];
        private int _part;

        public string Get(int part, int page, bool single)
        {
            if (single)
            {
                Clear();
                _part = 0;
                var rendered = Render(partPath(part), page, page, Path.Combine(renderDir, prefix));
                foreach (var pair in rendered)
                {
                    _pages[pair.Key] = pair.Value;
                }

                return _pages[page];
            }

            if (_part != part)
            {
                Clear();
                var entry = manifest.Part(part);
                foreach (var pair in Render(partPath(part), 1, entry.Pages, Path.Combine(renderDir, prefix)))
                {
                    _pages[pair.Key] = pair.Value;
                }

                _part = part;
            }

            return _pages[page];
        }

        public void Dispose() => Clear();

        private void Clear()
        {
            foreach (string file in _pages.Values)
            {
                File.Delete(file);
            }

            _pages.Clear();
        }
    }
}

/// <summary>The dataset as the input-row checks read it: every record's sequence and description length, in file order.</summary>
internal sealed record DatasetRows(string File, string Sha256, int[] Sequences, int[] DescriptionLengths)
{
    /// <summary>
    /// Reads the dataset's items. The description length is the length of the decoded
    /// string in UTF-16 code units, as the producer measures it.
    /// </summary>
    public static DatasetRows Load(string path)
    {
        string sha256 = SetManifest.Sha256OfFile(path);
        using var document = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllBytes(path));
        var items = document.RootElement.GetProperty("items");
        var sequences = new int[items.GetArrayLength()];
        var lengths = new int[sequences.Length];
        int i = 0;
        foreach (var item in items.EnumerateArray())
        {
            sequences[i] = item.GetProperty("sequence").GetInt32();
            lengths[i] = (item.GetProperty("description").GetString() ?? string.Empty).Length;
            i++;
        }

        return new DatasetRows(Path.GetFileName(path), sha256, sequences, lengths);
    }
}

/// <summary>
/// Outcome of the input-row checks on one set, read from the dataset and the set
/// manifest: only ordinary records used, contiguous slices of the ordinary records,
/// and the exclusion recorded as the dataset shows it.
/// </summary>
internal sealed record InputRowCheck(
    string Set,
    string DatasetFile,
    string DatasetSha256,
    string? ManifestDatasetSha256,
    int DatasetRecords,
    int Threshold,
    int? ManifestThreshold,
    int ExcludedInDataset,
    int ExpectedExcluded,
    int? ManifestExcludedCount,
    int LargestBelow,
    int SmallestAtOrAbove,
    int? ManifestLargestBelow,
    int? ManifestSmallestAtOrAbove,
    int FirstOrdinarySequence,
    int Parts,
    int RecordsUsed,
    int UsedFirstSequence,
    int UsedLastSequence,
    int ExcludedInUsedRange,
    int? ManifestExcludedInUsedRange,
    int LongestUsedDescription,
    bool DatasetMatches,
    bool ExclusionRecorded,
    bool ExcludedCountMatches,
    bool FirstPartAtFirstOrdinary,
    bool RecordsEqualOrdinaryInSpan,
    bool DescriptionsBelowThreshold,
    bool StrictlyIncreasing,
    bool GapsExactlyExcluded,
    bool Pass,
    string? FirstFailure);

internal static partial class Verifier
{
    /// <summary>A record is ordinary exactly when its description is shorter than this (UTF-16 code units).</summary>
    public const int OrdinaryDescriptionLimit = 1500;

    /// <summary>The dataset generator's own count of descriptions of 1 500 characters or more in this dataset.</summary>
    public const int ExpectedExcludedRecords = 1990;

    /// <summary>
    /// The input-row checks of one set, before measuring: every used record's
    /// description is below the limit; the used sequences strictly increase across
    /// the set; between two consecutive used sequences there are exactly the excluded
    /// records and nothing else; the first part starts at the first ordinary record;
    /// each part's record count equals the ordinary records between its first and
    /// last sequence (never assumed to be last - first + 1); the excluded count
    /// equals the generator's count; the manifest's exclusion record matches the
    /// dataset. The used records of a part are the dataset records in its span
    /// minus the excluded sequences the manifest lists for it.
    /// </summary>
    public static InputRowCheck CheckInputRows(string setName, string manifestPath, DatasetRows dataset)
    {
        var failures = new List<string>();
        var index = new Dictionary<int, int>(dataset.Sequences.Length);
        for (int i = 0; i < dataset.Sequences.Length; i++)
        {
            if (!index.TryAdd(dataset.Sequences[i], i))
            {
                failures.Add($"dataset sequence {dataset.Sequences[i]} appears twice");
            }
        }

        bool IsOrdinary(int sequence) => dataset.DescriptionLengths[index[sequence]] < OrdinaryDescriptionLimit;
        var ordinarySequences = dataset.Sequences.Where(IsOrdinary).ToList();
        var excludedSequences = dataset.Sequences.Where(s => !IsOrdinary(s)).ToList();
        int largestBelow = dataset.DescriptionLengths.Where(l => l < OrdinaryDescriptionLimit).DefaultIfEmpty(0).Max();
        int smallestAtOrAbove = dataset.DescriptionLengths.Where(l => l >= OrdinaryDescriptionLimit).DefaultIfEmpty(0).Min();
        int firstOrdinary = ordinarySequences.Count == 0 ? -1 : ordinarySequences[0];

        using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(manifestPath));
        var root = document.RootElement;
        string? manifestSha = root.TryGetProperty("dataset", out var ds) && ds.TryGetProperty("sha256", out var sh) ? sh.GetString() : null;
        int? manifestRecords = IntAt(root, "dataset", "records");
        int? manifestThreshold = IntAt(root, "dataset", "exclusion", "threshold");
        int? manifestExcluded = IntAt(root, "dataset", "exclusion", "excludedCount");
        int? manifestLargest = IntAt(root, "dataset", "exclusion", "largestBelow");
        int? manifestSmallest = IntAt(root, "dataset", "exclusion", "smallestAtOrAbove");
        int? manifestInRange = IntAt(root, "excludedInUsedRange", "count");

        bool datasetMatches = string.Equals(manifestSha, dataset.Sha256, StringComparison.Ordinal) && manifestRecords == dataset.Sequences.Length;
        if (!datasetMatches)
        {
            failures.Add($"dataset {dataset.Sha256} ({dataset.Sequences.Length} records) differs from the manifest's {manifestSha} ({manifestRecords})");
        }

        bool excludedCountMatches = excludedSequences.Count == ExpectedExcludedRecords && manifestExcluded == excludedSequences.Count;
        if (!excludedCountMatches)
        {
            failures.Add($"excluded count: dataset {excludedSequences.Count}, manifest {manifestExcluded}, generator {ExpectedExcludedRecords}");
        }

        bool exclusionRecorded = manifestThreshold == OrdinaryDescriptionLimit && manifestLargest == largestBelow && manifestSmallest == smallestAtOrAbove;
        if (!exclusionRecorded)
        {
            failures.Add($"exclusion record: threshold {manifestThreshold}, largest below {manifestLargest}, smallest at or above {manifestSmallest}; the dataset gives {OrdinaryDescriptionLimit}, {largestBelow}, {smallestAtOrAbove}");
        }

        var parts = root.GetProperty("parts").EnumerateArray()
            .Select(e => (
                Order: e.GetProperty("order").GetInt32(),
                First: e.GetProperty("firstSequence").GetInt32(),
                Last: e.GetProperty("lastSequence").GetInt32(),
                Records: e.GetProperty("records").GetInt32(),
                Excluded: e.TryGetProperty("excludedSequences", out var x) ? x.EnumerateArray().Select(v => v.GetInt32()).ToHashSet() : []))
            .OrderBy(p => p.Order)
            .ToList();

        bool firstPartAtFirstOrdinary = parts.Count > 0 && parts[0].First == firstOrdinary;
        if (!firstPartAtFirstOrdinary)
        {
            failures.Add($"the first part starts at sequence {(parts.Count > 0 ? parts[0].First : -1)}, the first ordinary record is {firstOrdinary}");
        }

        var sorted = dataset.Sequences.Order().ToArray();
        bool recordsEqual = true;
        bool descriptionsBelow = true;
        bool gapsExact = true;
        var used = new List<int>();
        int longestUsed = 0;
        foreach (var part in parts)
        {
            if (part.First > part.Last)
            {
                recordsEqual = false;
                failures.Add($"part {part.Order}: first sequence {part.First} after last {part.Last}");
                continue;
            }

            int lo = Array.BinarySearch(sorted, part.First);
            int hi = Array.BinarySearch(sorted, part.Last);
            if (lo < 0 || hi < 0)
            {
                recordsEqual = false;
                failures.Add($"part {part.Order}: sequence {(lo < 0 ? part.First : part.Last)} is not in the dataset");
                continue;
            }

            var span = sorted[lo..(hi + 1)];
            int ordinaryInSpan = span.Count(IsOrdinary);
            var partUsed = span.Where(s => !part.Excluded.Contains(s)).ToList();
            if (ordinaryInSpan != part.Records || partUsed.Count != part.Records)
            {
                recordsEqual = false;
                failures.Add($"part {part.Order}: {part.Records} records in the manifest, {ordinaryInSpan} ordinary records in {part.First}-{part.Last}, {partUsed.Count} used after the listed exclusions");
            }

            foreach (int s in part.Excluded.Order())
            {
                if (!index.ContainsKey(s) || s < part.First || s > part.Last || IsOrdinary(s))
                {
                    gapsExact = false;
                    failures.Add($"part {part.Order}: listed excluded sequence {s} is not an excluded record inside {part.First}-{part.Last}");
                }
            }

            foreach (int s in partUsed)
            {
                int length = dataset.DescriptionLengths[index[s]];
                longestUsed = Math.Max(longestUsed, length);
                if (length >= OrdinaryDescriptionLimit)
                {
                    descriptionsBelow = false;
                    failures.Add($"part {part.Order}: used sequence {s} has a description of {length} characters");
                }
            }

            used.AddRange(partUsed);
        }

        bool strictlyIncreasing = true;
        for (int i = 1; i < used.Count; i++)
        {
            if (used[i] <= used[i - 1])
            {
                strictlyIncreasing = false;
                failures.Add($"used sequences not strictly increasing: {used[i - 1]} then {used[i]}");
                break;
            }

            for (int s = used[i - 1] + 1; s < used[i]; s++)
            {
                if (!index.ContainsKey(s) || IsOrdinary(s))
                {
                    gapsExact = false;
                    failures.Add($"gap between used sequences {used[i - 1]} and {used[i]} holds {s}, which is {(index.ContainsKey(s) ? "an ordinary record" : "not in the dataset")}");
                    break;
                }
            }
        }

        int usedFirst = used.Count == 0 ? -1 : used[0];
        int usedLast = used.Count == 0 ? -1 : used[^1];
        int excludedInRange = excludedSequences.Count(s => s >= usedFirst && s <= usedLast);
        if (manifestInRange != excludedInRange)
        {
            gapsExact = false;
            failures.Add($"excluded records in the used range {usedFirst}-{usedLast}: dataset {excludedInRange}, manifest {manifestInRange}");
        }

        return new InputRowCheck(setName, dataset.File, dataset.Sha256, manifestSha, dataset.Sequences.Length, OrdinaryDescriptionLimit, manifestThreshold,
            excludedSequences.Count, ExpectedExcludedRecords, manifestExcluded, largestBelow, smallestAtOrAbove, manifestLargest, manifestSmallest,
            firstOrdinary, parts.Count, used.Count, usedFirst, usedLast, excludedInRange, manifestInRange, longestUsed,
            datasetMatches, exclusionRecorded, excludedCountMatches, firstPartAtFirstOrdinary, recordsEqual, descriptionsBelow, strictlyIncreasing, gapsExact,
            failures.Count == 0, failures.FirstOrDefault());
    }

    /// <summary>The integer at a path of object members, or null when a member is missing or not a number.</summary>
    private static int? IntAt(System.Text.Json.JsonElement element, params string[] path)
    {
        foreach (string name in path)
        {
            if (element.ValueKind != System.Text.Json.JsonValueKind.Object || !element.TryGetProperty(name, out element))
            {
                return null;
            }
        }

        return element.ValueKind == System.Text.Json.JsonValueKind.Number ? element.GetInt32() : null;
    }
}
