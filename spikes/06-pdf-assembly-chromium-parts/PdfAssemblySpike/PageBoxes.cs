using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PdfAssemblySpike;

/// <summary>The /MediaBox of one page as qpdf wrote it, its four numbers in decimal, and the /CropBox if any.</summary>
internal sealed record PageBoxValue(int Page, string Text, decimal[] Numbers, string? CropText);

/// <summary>A distinct page box value and the number of pages that carry it.</summary>
internal sealed record BoxValueCount(string Box, int Pages);

/// <summary>
/// The rounded copy of one source part: the page box numbers substituted in place,
/// byte for byte, with every check of the construction and its outcome.
/// </summary>
internal sealed record RoundedCopy(
    int Order,
    string File,
    int Pages,
    int MediaBoxCount,
    IReadOnlyList<string> SourceBoxTexts,
    IReadOnlyList<BoxValueCount> SourceBoxesAsRead,
    long Bytes,
    long CopyBytes,
    int DifferingBytes,
    bool DifferencesInsideBoxNumbers,
    string SourceCheck,
    string CopyCheck,
    IReadOnlyList<BoxValueCount> CopyBoxesAsRead,
    string? CopySha256,
    bool Pass,
    string? Failure);

/// <summary>Changed pixels of one page against one reference, outside and inside the numbering area.</summary>
internal sealed record PagePixels(int Page, long Outside, long Inside);

/// <summary>
/// The page box rule. The source parts carry the page box
/// [0 0 594.95996 841.91998]; the PDF library rewrites it on import as
/// [0 0 594.96 841.92]. The reference for such a page is fixed by rule from the
/// source bytes: a copy of the part with the box numbers substituted in place,
/// same length, by no PDF writer. Boxes are read with qpdf and compared in decimal
/// arithmetic. qpdf-only outputs must keep the source box; outputs whose pages the
/// PDF library wrote may carry the source box or the rounded box; anything else
/// differs.
/// </summary>
internal static partial class PageBoxes
{
    public const string SourceBoxText = "0 0 594.95996 841.91998";
    public const string RoundedBoxText = "0 0 594.96 841.92";
    public const string Source = "source";
    public const string Rounded = "rounded";
    public const string Differs = "box-differs";

    /// <summary>Rule for qpdf-only outputs: the source box exactly.</summary>
    public const string SourceOnlyRule = "source-only";

    /// <summary>Rule for outputs whose pages the PDF library wrote: the source box or the rounded box.</summary>
    public const string SourceOrRoundedRule = "source-or-rounded";

    public static readonly decimal[] SourceBox = [0m, 0m, 594.95996m, 841.91998m];

    /// <summary>The source box with each number rounded to two decimals.</summary>
    public static readonly decimal[] RoundedBox = [.. SourceBox.Select(v => Math.Round(v, 2, MidpointRounding.AwayFromZero))];

    private const string SourceWidth = "594.95996";
    private const string SourceHeight = "841.91998";
    private const string RoundedWidth = "594.96000";
    private const string RoundedHeight = "841.92000";

    /// <summary>The rule an output variant is judged by.</summary>
    public static string RuleFor(string variant) => variant == "V4" ? SourceOnlyRule : SourceOrRoundedRule;

    /// <summary>Classifies one page box under a rule: source, rounded or box-differs.</summary>
    public static string Classify(decimal[] box, string rule)
    {
        if (Equal(box, SourceBox))
        {
            return Source;
        }

        return rule == SourceOrRoundedRule && Equal(box, RoundedBox) ? Rounded : Differs;
    }

    /// <summary>The verdict of a whole output from its page classifications.</summary>
    public static string Verdict(IReadOnlyCollection<string> classes) =>
        classes.Contains(Differs) ? "fail"
        : classes.Contains(Source) && classes.Contains(Rounded) ? "box-mixed"
        : classes.Contains(Rounded) ? "box-rounded"
        : "box-source";

    private static bool Equal(decimal[] a, decimal[] b) => a.Length == b.Length && a.Zip(b).All(p => p.First == p.Second);

    /// <summary>
    /// Every page's /MediaBox as qpdf writes it (inherited from the page tree when the
    /// page has none), parsed in decimal arithmetic.
    /// </summary>
    public static List<PageBoxValue> Read(string pdf)
    {
        var result = ExternalTool.Run(Verifier.Qpdf, ["--json=2", "--json-key=pages", "--json-key=qpdf", pdf]);
        if (result.ExitCode is not (0 or 3))
        {
            throw new InvalidDataException($"qpdf --json could not read {pdf}: exit {result.ExitCode} {result.StandardError.Trim()}");
        }

        using var document = JsonDocument.Parse(result.StandardOutput);
        var root = document.RootElement;
        var objects = root.GetProperty("qpdf")[1];
        var pages = new List<PageBoxValue>();
        int number = 0;
        foreach (var page in root.GetProperty("pages").EnumerateArray())
        {
            number++;
            string reference = page.GetProperty("object").GetString() ?? string.Empty;
            var media = Inherited(objects, reference, "/MediaBox")
                ?? throw new InvalidDataException($"{pdf}: page {number} has no /MediaBox, not even inherited.");
            var crop = Inherited(objects, reference, "/CropBox");
            var (text, numbers) = Box(objects, media);
            pages.Add(new PageBoxValue(number, text, numbers, crop is null ? null : Box(objects, crop.Value).Text));
        }

        return pages;
    }

    /// <summary>The distinct box values of a list of pages with their page counts.</summary>
    public static List<BoxValueCount> Distinct(IEnumerable<string> texts) =>
        [.. texts.GroupBy(t => t, StringComparer.Ordinal).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new BoxValueCount("[" + g.Key + "]", g.Count()))];

    private static JsonElement? Inherited(JsonElement objects, string reference, string key)
    {
        string? current = reference;
        for (int depth = 0; current is not null && depth < 64; depth++)
        {
            var value = Resolve(objects, current);
            if (value.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (value.TryGetProperty(key, out var box))
            {
                return box;
            }

            current = value.TryGetProperty("/Parent", out var parent) && parent.ValueKind == JsonValueKind.String ? parent.GetString() : null;
        }

        return null;
    }

    private static JsonElement Resolve(JsonElement objects, string reference) =>
        objects.TryGetProperty("obj:" + reference, out var entry) && entry.TryGetProperty("value", out var value) ? value : default;

    private static (string Text, decimal[] Numbers) Box(JsonElement objects, JsonElement box)
    {
        if (box.ValueKind == JsonValueKind.String && IndirectPattern().IsMatch(box.GetString() ?? string.Empty))
        {
            box = Resolve(objects, box.GetString()!);
        }

        if (box.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException($"a page box is not an array: {box.GetRawText()}");
        }

        var raw = new List<string>();
        foreach (var element in box.EnumerateArray())
        {
            var item = element;
            if (item.ValueKind == JsonValueKind.String && IndirectPattern().IsMatch(item.GetString() ?? string.Empty))
            {
                item = Resolve(objects, item.GetString()!);
            }

            if (item.ValueKind != JsonValueKind.Number)
            {
                throw new InvalidDataException($"a page box holds a value that is not a number: {item.GetRawText()}");
            }

            raw.Add(item.GetRawText());
        }

        return (string.Join(' ', raw), [.. raw.Select(r => decimal.Parse(r, NumberStyles.Float, CultureInfo.InvariantCulture))]);
    }

    [GeneratedRegex(@"^\d+ \d+ R$")]
    private static partial Regex IndirectPattern();

    [GeneratedRegex(@"/MediaBox(?![A-Za-z0-9])")]
    private static partial Regex MediaBoxKeyPattern();

    [GeneratedRegex(@"/MediaBox\s*\[([^\]]*)\]")]
    private static partial Regex MediaBoxArrayPattern();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    /// <summary>
    /// Builds the rounded copy of a source part by an in-place substitution of the box
    /// numbers only (594.95996 to 594.96000, 841.91998 to 841.92000) and checks the
    /// construction: one /MediaBox per page, every source box exactly the expected
    /// text, exactly eight differing bytes per page, all inside the box numbers, the
    /// same size, and the same structural-check verdict, exit code and warning count.
    /// The copy is kept only when every check holds.
    /// </summary>
    public static RoundedCopy BuildRounded(int order, string part, string copyPath)
    {
        byte[] bytes = File.ReadAllBytes(part);
        string latin = Encoding.Latin1.GetString(bytes);
        string fileName = Path.GetFileName(part);
        int pages = Verifier.PageCount(part);
        int keyCount = MediaBoxKeyPattern().Count(latin);
        var arrays = MediaBoxArrayPattern().Matches(latin);
        var texts = arrays.Select(m => Spaces().Replace(m.Groups[1].Value.Trim(), " ")).ToList();
        var sourceRead = Read(part);
        var sourceDistinct = Distinct(sourceRead.Select(p => p.Text));
        var sourceCheck = QpdfCheck.Run(part);
        string sourceCheckText = CheckText(sourceCheck);

        RoundedCopy Failed(string reason, long copyBytes = 0, int differing = 0, bool inside = false, string copyCheck = "", IReadOnlyList<BoxValueCount>? copyBoxes = null)
        {
            if (File.Exists(copyPath))
            {
                File.Delete(copyPath);
            }

            return new RoundedCopy(order, fileName, pages, keyCount, [.. texts.Distinct(StringComparer.Ordinal)], sourceDistinct, bytes.Length, copyBytes,
                differing, inside, sourceCheckText, copyCheck, copyBoxes ?? [], null, false, reason);
        }

        if (pages <= 0 || keyCount != pages || arrays.Count != pages)
        {
            return Failed(string.Create(CultureInfo.InvariantCulture, $"{keyCount} /MediaBox keys and {arrays.Count} box arrays for {pages} pages"));
        }

        if (texts.Any(t => t != SourceBoxText))
        {
            return Failed($"a source box text is '[{texts.First(t => t != SourceBoxText)}]', expected '[{SourceBoxText}]'");
        }

        if (sourceRead.Count != pages || sourceRead.Any(p => p.Text != SourceBoxText || !Equal(p.Numbers, SourceBox)))
        {
            return Failed($"qpdf reads the source boxes as {string.Join(", ", sourceDistinct.Select(d => d.Box + " x" + d.Pages))}");
        }

        // The substitution: inside each box array, the two numbers, in place.
        byte[] copy = (byte[])bytes.Clone();
        var spans = new List<(int Start, int Length)>();
        foreach (Match match in arrays)
        {
            var inner = match.Groups[1];
            foreach (var (from, to) in new[] { (SourceWidth, RoundedWidth), (SourceHeight, RoundedHeight) })
            {
                int at = latin.IndexOf(from, inner.Index, inner.Length, StringComparison.Ordinal);
                if (at < 0)
                {
                    return Failed($"box number {from} not found inside a box array");
                }

                Encoding.Latin1.GetBytes(to).CopyTo(copy, at);
                spans.Add((at, from.Length));
            }
        }

        int differing = 0;
        bool inside = true;
        for (int i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] != copy[i])
            {
                differing++;
                inside &= spans.Any(s => i >= s.Start && i < s.Start + s.Length);
            }
        }

        Directory.CreateDirectory(Path.GetDirectoryName(copyPath)!);
        File.WriteAllBytes(copyPath, copy);
        long copyBytes = new FileInfo(copyPath).Length;
        var copyCheck = QpdfCheck.Run(copyPath);
        string copyCheckText = CheckText(copyCheck);
        var copyRead = Read(copyPath);
        var copyDistinct = Distinct(copyRead.Select(p => p.Text));
        if (differing != 8 * pages || !inside)
        {
            return Failed(string.Create(CultureInfo.InvariantCulture, $"{differing} differing bytes for {pages} pages (expected {8 * pages}), all inside the box numbers: {inside}"),
                copyBytes, differing, inside, copyCheckText, copyDistinct);
        }

        if (copyBytes != bytes.Length)
        {
            return Failed(string.Create(CultureInfo.InvariantCulture, $"size changed from {bytes.Length} to {copyBytes}"), copyBytes, differing, inside, copyCheckText, copyDistinct);
        }

        if (copyCheck.Verdict != sourceCheck.Verdict || copyCheck.ExitCode != sourceCheck.ExitCode || copyCheck.WarningLines != sourceCheck.WarningLines)
        {
            return Failed($"structural check changed from {sourceCheckText} to {copyCheckText}", copyBytes, differing, inside, copyCheckText, copyDistinct);
        }

        if (copyRead.Count != pages || copyRead.Any(p => !Equal(p.Numbers, RoundedBox)))
        {
            return Failed($"qpdf reads the copy's boxes as {string.Join(", ", copyDistinct.Select(d => d.Box + " x" + d.Pages))}", copyBytes, differing, inside, copyCheckText, copyDistinct);
        }

        return new RoundedCopy(order, fileName, pages, keyCount, [.. texts.Distinct(StringComparer.Ordinal)], sourceDistinct, bytes.Length, copyBytes,
            differing, inside, sourceCheckText, copyCheckText, copyDistinct, SetManifest.Sha256OfFile(copyPath), true, null);
    }

    private static string CheckText(QpdfCheckRecord check) =>
        string.Create(CultureInfo.InvariantCulture, $"{check.Verdict} (exit {check.ExitCode}, {check.WarningLines} warnings)");

    /// <summary>Marks a page that was not rendered, or rendered at another size: never counted as a pixel difference.</summary>
    public const long NotRendered = -1;

    /// <summary>The number of pages per classification.</summary>
    public static Dictionary<string, int> Counts(IEnumerable<string> classes) =>
        classes.GroupBy(c => c, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

    private static bool AllRendered(IEnumerable<PagePixels> pixels) => pixels.All(p => p.Outside != NotRendered);

    private static long OutsideTotal(IEnumerable<PagePixels> pixels) => pixels.Where(p => p.Outside != NotRendered).Sum(p => p.Outside);

    /// <summary>Changed pixels of every page of a candidate against a reference file of the same page count.</summary>
    public static List<PagePixels> ComparePages(string reference, string candidate, int pages, string dir)
    {
        Directory.CreateDirectory(dir);
        string tag = Guid.NewGuid().ToString("N")[..8];
        var a = Verifier.Render(reference, 1, pages, Path.Combine(dir, "a" + tag));
        var b = Verifier.Render(candidate, 1, pages, Path.Combine(dir, "b" + tag));
        var result = new List<PagePixels>();
        try
        {
            for (int k = 1; k <= pages; k++)
            {
                if (!a.TryGetValue(k, out string? fa) || !b.TryGetValue(k, out string? fb))
                {
                    result.Add(new PagePixels(k, NotRendered, NotRendered));
                    continue;
                }

                var diff = PgmImage.Compare(PgmImage.Load(fa), PgmImage.Load(fb), PixelRect.NumberingArea);
                result.Add(diff.SameSize ? new PagePixels(k, diff.ChangedOutside, diff.ChangedInside) : new PagePixels(k, NotRendered, NotRendered));
            }
        }
        finally
        {
            foreach (string file in a.Values.Concat(b.Values))
            {
                File.Delete(file);
            }
        }

        return result;
    }

    public const string PageBoxGroup = "page-box";

    /// <summary>
    /// The page-box controls on one Chromium part P and its rounded copy R: the
    /// construction of R (BC-1); P under the source-only rule (BC-2); R under the
    /// source-or-rounded rule (BC-3); a box outside the table (BC-4); R under the
    /// source-only rule (BC-5); R with its first page's content shifted by 0.5 pt,
    /// which must still fail on pixels (BC-6); the same at 0.1 pt, recorded only (BC-7).
    /// </summary>
    public static List<ControlResult> RunControls(string part, string fileName, string tempDir)
    {
        Directory.CreateDirectory(tempDir);
        var controls = new List<ControlResult>();
        string render = Path.Combine(tempDir, "render");
        string r = Path.Combine(tempDir, "ref-rounded-" + fileName);
        var built = BuildRounded(1, part, r);
        int pages = built.Pages;

        // BC-1: the construction of R.
        controls.Add(Box("BC-1", $"rounded copy of {fileName} built by in-place substitution", "pass", built.Pass ? "pass" : "fail",
            new { construction = built }));
        if (!built.Pass)
        {
            foreach (string id in new[] { "BC-2", "BC-3", "BC-4", "BC-5", "BC-6" })
            {
                controls.Add(Box(id, "needs the rounded copy", "-", "control-error", new { reason = "BC-1 failed: " + built.Failure }));
            }

            return controls;
        }

        // BC-2: P under the source-only rule against its own render.
        {
            var classes = Read(part).Select(p => Classify(p.Numbers, SourceOnlyRule)).ToList();
            var pixels = ComparePages(part, part, pages, render);
            bool pass = classes.All(c => c == Source) && pixels.All(p => p.Outside == 0 && p.Inside == 0);
            controls.Add(Box("BC-2", $"{fileName} under the source-only rule, unedited reference", "pass", pass ? "pass" : "fail",
                new { boxes = Counts(classes), changedPixelsPerPage = pixels }));
        }

        // BC-3: R under the source-or-rounded rule against R's render; its strict count against P recorded.
        {
            var classes = Read(r).Select(p => Classify(p.Numbers, SourceOrRoundedRule)).ToList();
            var pixels = ComparePages(r, r, pages, render);
            var strict = ComparePages(part, r, pages, render);
            bool pass = classes.All(c => c == Rounded) && pixels.All(p => p.Outside == 0 && p.Inside == 0);
            controls.Add(Box("BC-3", "rounded copy under the source-or-rounded rule, rounded reference", "pass", pass ? "pass" : "fail",
                new { boxes = Counts(classes), changedPixelsPerPage = pixels, strictAgainstUnedited = strict, strictOutsideTotal = OutsideTotal(strict), strictAllRendered = AllRendered(strict) }));
        }

        // BC-4: a box outside the table, same length.
        {
            string bc4 = Path.Combine(tempDir, "bc-4.pdf");
            byte[] bytes = File.ReadAllBytes(part);
            string latin = Encoding.Latin1.GetString(bytes);
            int changed = 0;
            foreach (Match match in MediaBoxArrayPattern().Matches(latin))
            {
                foreach (var (from, to) in new[] { (SourceWidth, "594.97000"), (SourceHeight, RoundedHeight) })
                {
                    int at = latin.IndexOf(from, match.Groups[1].Index, match.Groups[1].Length, StringComparison.Ordinal);
                    if (at >= 0)
                    {
                        Encoding.Latin1.GetBytes(to).CopyTo(bytes, at);
                        changed++;
                    }
                }
            }

            File.WriteAllBytes(bc4, bytes);
            var read = Read(bc4);
            var underSource = read.Select(p => Classify(p.Numbers, SourceOnlyRule)).ToList();
            var underRounded = read.Select(p => Classify(p.Numbers, SourceOrRoundedRule)).ToList();
            var againstUnedited = ComparePages(part, bc4, pages, render);
            var againstRounded = ComparePages(r, bc4, pages, render);
            bool differs = changed == 2 * pages && underSource.All(c => c == Differs) && underRounded.All(c => c == Differs);
            controls.Add(Box("BC-4", "box substituted to [0 0 594.97000 841.92000]", Differs + " under both rules", differs ? Differs + " under both rules" : "accepted",
                new
                {
                    boxesAsRead = Distinct(read.Select(p => p.Text)),
                    underSourceOnly = Counts(underSource),
                    underSourceOrRounded = Counts(underRounded),
                    outsidePixelsAgainstUnedited = OutsideTotal(againstUnedited),
                    outsidePixelsAgainstRounded = OutsideTotal(againstRounded),
                    allRendered = AllRendered(againstUnedited) && AllRendered(againstRounded),
                }));
            File.Delete(bc4);
        }

        // BC-5: R under the source-only rule.
        {
            var classes = Read(r).Select(p => Classify(p.Numbers, SourceOnlyRule)).ToList();
            controls.Add(Box("BC-5", "rounded copy under the source-only rule", Differs, classes.All(c => c == Differs) ? Differs : "accepted",
                new { boxes = Counts(classes) }));
        }

        // BC-6 (counted) and BC-7 (recorded): a real content shift on page 1 of R must be seen.
        foreach (var (id, shift, counted) in new[] { ("BC-6", "0.5", true), ("BC-7", "0.1", false) })
        {
            var shifted = Shift(r, shift, Path.Combine(tempDir, id.ToLowerInvariant()));
            if (shifted.Path is null)
            {
                controls.Add(Box(id, $"rounded copy, page 1 shifted by {shift} pt", counted ? "fail (pixels)" : "recorded", "control-error",
                    new { rewrite = shifted }, counted));
                continue;
            }

            var classes = Read(shifted.Path).Select(p => Classify(p.Numbers, SourceOrRoundedRule)).ToList();
            var pixels = ComparePages(r, shifted.Path, pages, render);
            bool boxPass = classes.All(c => c == Rounded);
            long outside = OutsideTotal(pixels);
            string actual = !AllRendered(pixels) ? "control-error (does not render)" : !boxPass ? Differs : outside >= 1 ? "fail (pixels)" : "pass";
            controls.Add(Box(id, $"rounded copy, page 1 content prefixed with '1 0 0 1 {shift} 0 cm', rewritten by qpdf", counted ? "fail (pixels)" : "recorded", actual,
                new { rewrite = shifted, boxes = Counts(classes), changedPixelsPerPage = pixels, outsideTotal = outside }, counted));
            File.Delete(shifted.Path);
        }

        if (File.Exists(r))
        {
            File.Delete(r);
        }

        Directory.Delete(render, recursive: true);
        return controls;
    }

    private static ControlResult Box(string id, string input, string expected, string actual, object detail, bool counted = true) =>
        new(id, input, expected, actual, !counted || expected == actual, -1, 0, 0, string.Empty, string.Empty, string.Empty, PageBoxGroup, counted, detail);

    /// <summary>What the shifted copy's construction did: each command's exit code and warnings, and the result's structural check.</summary>
    internal sealed record ShiftRecord(
        string? Path,
        int QdfExit,
        int RewriteExit,
        IReadOnlyList<string> RewriteWarnings,
        string? Check,
        int? RenderedPages,
        string? Problem);

    /// <summary>
    /// Writes R in qpdf's editable form, prefixes page 1's content stream with a
    /// translation, adjusts that stream's /Length object, and rewrites the edited file
    /// with plain qpdf. The result is a control input only, never a reference.
    /// </summary>
    private static ShiftRecord Shift(string r, string shift, string prefix)
    {
        string qdf = prefix + ".qdf.pdf";
        string output = prefix + ".pdf";
        var toQdf = ExternalTool.Run(Verifier.Qpdf, ["--qdf", "--object-streams=disable", r, qdf]);
        if (toQdf.ExitCode is not (0 or 3) || !File.Exists(qdf))
        {
            return new ShiftRecord(null, toQdf.ExitCode, -1, [], null, null, "qpdf --qdf failed: " + toQdf.StandardError.Trim());
        }

        string text = Encoding.Latin1.GetString(File.ReadAllBytes(qdf));
        File.Delete(qdf);
        int marker = text.IndexOf("%% Contents for page 1\n", StringComparison.Ordinal);
        int stream = marker < 0 ? -1 : text.IndexOf("stream\n", marker, StringComparison.Ordinal);
        var length = marker < 0 ? Match.Empty : LengthReferencePattern().Match(text, marker);
        if (marker < 0 || stream < 0 || !length.Success || length.Index > stream)
        {
            return new ShiftRecord(null, toQdf.ExitCode, -1, [], null, null, "page 1's content stream or its /Length reference was not found in the editable form");
        }

        string insert = $"1 0 0 1 {shift} 0 cm\n";
        string lengthObject = length.Groups[1].Value;
        var lengthValue = new Regex(@"\n" + lengthObject + @" 0 obj\n(\d+)\nendobj", RegexOptions.CultureInvariant).Match(text);
        if (!lengthValue.Success)
        {
            return new ShiftRecord(null, toQdf.ExitCode, -1, [], null, null, $"length object {lengthObject} not found");
        }

        int oldLength = int.Parse(lengthValue.Groups[1].Value, CultureInfo.InvariantCulture);
        int dataStart = stream + "stream\n".Length;
        string edited = string.Concat(text.AsSpan(0, dataStart), insert, text.AsSpan(dataStart));
        int valueIndex = lengthValue.Groups[1].Index + (lengthValue.Groups[1].Index > dataStart ? insert.Length : 0);
        edited = string.Concat(edited.AsSpan(0, valueIndex), (oldLength + insert.Length).ToString(CultureInfo.InvariantCulture),
            edited.AsSpan(valueIndex + lengthValue.Groups[1].Length));
        string editedPath = prefix + ".edited.qdf.pdf";
        File.WriteAllBytes(editedPath, Encoding.Latin1.GetBytes(edited));
        var rewrite = ExternalTool.Run(Verifier.Qpdf, [editedPath, output]);
        File.Delete(editedPath);
        var warnings = QpdfCheck.Lines(rewrite.StandardError).Concat(QpdfCheck.Lines(rewrite.StandardOutput)).ToList();
        if (rewrite.ExitCode is not (0 or 3) || !File.Exists(output))
        {
            return new ShiftRecord(null, toQdf.ExitCode, rewrite.ExitCode, warnings, null, null, "the qpdf rewrite produced no file");
        }

        var check = QpdfCheck.Run(output);
        int rendered = Verifier.PageCount(output);
        string checkText = CheckText(check);
        return rendered > 0
            ? new ShiftRecord(output, toQdf.ExitCode, rewrite.ExitCode, warnings, checkText, rendered, null)
            : new ShiftRecord(null, toQdf.ExitCode, rewrite.ExitCode, warnings, checkText, rendered, "the rewritten file has no readable page");
    }

    [GeneratedRegex(@"/Length (\d+) 0 R")]
    private static partial Regex LengthReferencePattern();
}
