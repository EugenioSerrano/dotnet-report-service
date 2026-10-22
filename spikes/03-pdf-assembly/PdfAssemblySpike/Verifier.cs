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
    IReadOnlyList<int> PixelPages)
{
    public bool Pass => StructurePass && OrderPass && NumberingPass && PixelPass;
}

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
    string Note);

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

    public static VerificationResult Verify(string manifestPath, string pdf, bool numbered, string mode, string tempDir, string keepDir)
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

        if (mode == "structure")
        {
            return new VerificationResult(pdf, mode, numbered, check, pageCount, expected, structurePass,
                0, true, true, 0, true, 0, 0, 0, null, firstFailure, []);
        }

        // 2. Order and numbering, every page.
        var map = manifest.PageMap();
        string text = ExternalTool.Output(PdfToText, "-enc", "UTF-8", "-layout", pdf, "-");
        string[] pages = text.Split('\f');
        bool orderPass = pages.Length >= pageCount && pageCount == expected;
        bool numberingPass = orderPass;
        int textChecked = 0;
        for (int i = 1; i <= Math.Min(expected, pages.Length); i++)
        {
            textChecked++;
            string pageText = pages[i - 1];
            var markers = MarkerPattern().Matches(pageText);
            var (part, k) = map[i - 1];
            bool markerOk = markers.Count == 1
                && int.Parse(markers[0].Groups[1].Value, CultureInfo.InvariantCulture) == part
                && int.Parse(markers[0].Groups[2].Value, CultureInfo.InvariantCulture) == k;
            if (!markerOk)
            {
                orderPass = false;
                Fail(ref firstFailingPage, ref firstFailure, i, $"page {i}: {markers.Count} markers, expected one {PartGenerator.Marker(part, k)}");
            }

            var numbers = NumberPattern().Matches(pageText);
            bool numberOk = numbered
                ? numbers.Count == 1
                    && int.Parse(numbers[0].Groups[1].Value, CultureInfo.InvariantCulture) == i
                    && int.Parse(numbers[0].Groups[2].Value, CultureInfo.InvariantCulture) == expected
                : numbers.Count == 0;
            if (!numberOk)
            {
                numberingPass = false;
                Fail(ref firstFailingPage, ref firstFailure, i, $"page {i}: {numbers.Count} page numbers ({(numbers.Count > 0 ? numbers[0].Value : "none")}), expected {(numbered ? PageStamper.Text(i, expected) : "none")}");
            }
        }

        // 3. Pixel comparison against the source pages.
        List<int> pixelPages = mode == "all" ? [.. Enumerable.Range(1, expected)] : SamplePages(manifest);
        bool pixelPass = true;
        long maxOutside = 0;
        long minInside = long.MaxValue;
        long maxInside = 0;
        int pixelChecked = 0;
        string renderDir = Path.Combine(tempDir, "render");
        Directory.CreateDirectory(renderDir);
        var references = new ReferenceCache(manifestPath, manifest, renderDir);
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
                    string referencePath = references.Get(part, k, single: mode != "all");
                    if (!rendered.TryGetValue(i, out string? outputPath))
                    {
                        pixelPass = false;
                        Fail(ref firstFailingPage, ref firstFailure, i, $"page {i}: not rendered");
                        continue;
                    }

                    var diff = PgmImage.Compare(PgmImage.Load(referencePath), PgmImage.Load(outputPath), PageLayout.PageHeight, PageLayout.FooterBandHeight);
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
                        Fail(ref firstFailingPage, ref firstFailure, i, $"page {i}: same size {diff.SameSize}, {diff.ChangedOutside} changed outside, {diff.ChangedInside} inside the band");
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
            Directory.Delete(renderDir, recursive: true);
        }

        return new VerificationResult(pdf, mode, numbered, check, pageCount, expected, structurePass,
            textChecked, orderPass, numberingPass, pixelChecked, pixelPass, maxOutside, minInside == long.MaxValue ? 0 : minInside, maxInside,
            firstFailingPage, firstFailure, mode == "all" ? [] : pixelPages);
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

    /// <summary>
    /// Positive and negative controls of the structural check, built from the
    /// first part of a set: the check must still accept what it claims to accept
    /// and still reject damaged files. Returns true only when every control has
    /// its expected verdict.
    /// </summary>
    public static (bool Pass, List<ControlResult> Controls) RunControls(string manifestPath, string tempDir)
    {
        var manifest = SetManifest.Load(manifestPath);
        string part = SetManifest.PartPath(manifestPath, manifest.Parts[0]);
        Directory.CreateDirectory(tempDir);
        var controls = new List<ControlResult>();
        string fileName = manifest.Parts[0].File;

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

        // NC-1: eight bytes of 0xFF written into the first stream's compressed data.
        byte[] bytes = File.ReadAllBytes(part);
        int dataStart = FirstStreamDataStart(bytes);
        if (dataStart >= 0 && dataStart + 11 <= bytes.Length)
        {
            byte[] corrupted = (byte[])bytes.Clone();
            Array.Fill(corrupted, (byte)0xFF, dataStart + 3, 8);
            string nc1 = Path.Combine(tempDir, "nc-1.pdf");
            File.WriteAllBytes(nc1, corrupted);
            controls.Add(Control("NC-1", string.Create(CultureInfo.InvariantCulture, $"8 bytes 0xFF at byte {dataStart + 3} (stream data starts at {dataStart})"),
                QpdfCheck.Fail, QpdfCheck.Run(nc1), null));
        }
        else
        {
            controls.Add(ControlError("NC-1", "corrupted stream", QpdfCheck.Fail, "no stream keyword followed by an end of line was found"));
        }

        // NC-2: the first half of the file.
        string nc2 = Path.Combine(tempDir, "nc-2.pdf");
        File.WriteAllBytes(nc2, bytes[..(bytes.Length / 2)]);
        controls.Add(Control("NC-2", string.Create(CultureInfo.InvariantCulture, $"first {bytes.Length / 2} of {bytes.Length} bytes"),
            QpdfCheck.Fail, QpdfCheck.Run(nc2), null));

        // NC-3: the first stream length falsified.
        string latin = System.Text.Encoding.Latin1.GetString(bytes);
        var length = LengthPattern().Match(latin);
        if (length.Success)
        {
            string nc3 = Path.Combine(tempDir, "nc-3.pdf");
            string falsified = string.Concat(latin.AsSpan(0, length.Index), "/Length 5", latin.AsSpan(length.Index + length.Length));
            File.WriteAllBytes(nc3, System.Text.Encoding.Latin1.GetBytes(falsified));
            controls.Add(Control("NC-3", $"'{length.Value}' replaced by '/Length 5'", QpdfCheck.Fail, QpdfCheck.Run(nc3), null));
        }
        else
        {
            controls.Add(ControlError("NC-3", "falsified length", QpdfCheck.Fail, "no /Length entry was found"));
        }

        // NC-4: edge cases of the classifier, from PC-1's real transcript changed one way at a time.
        controls.AddRange(TranscriptControls(part, raw));

        foreach (string file in new[] { pc2, nc2, Path.Combine(tempDir, "nc-1.pdf"), Path.Combine(tempDir, "nc-3.pdf") })
        {
            if (File.Exists(file))
            {
                File.Delete(file);
            }
        }

        return (controls.All(c => c.AsExpected), controls);
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

    [GeneratedRegex(@"\[\[P(\d{4})\.(\d{3})\]\]")]
    private static partial Regex MarkerPattern();

    [GeneratedRegex(@"Page (\d+) of (\d+)")]
    private static partial Regex NumberPattern();

    /// <summary>
    /// Renders source pages on demand. In "all" mode a whole part is rendered when
    /// its first page is needed and dropped when the next part starts.
    /// </summary>
    private sealed class ReferenceCache(string manifestPath, SetManifest manifest, string renderDir) : IDisposable
    {
        private readonly Dictionary<int, string> _pages = [];
        private int _part;

        public string Get(int part, int page, bool single)
        {
            if (single)
            {
                Clear();
                _part = 0;
                var rendered = Render(SetManifest.PartPath(manifestPath, manifest.Part(part)), page, page, Path.Combine(renderDir, "ref"));
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
                foreach (var pair in Render(SetManifest.PartPath(manifestPath, entry), 1, entry.Pages, Path.Combine(renderDir, "ref")))
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
