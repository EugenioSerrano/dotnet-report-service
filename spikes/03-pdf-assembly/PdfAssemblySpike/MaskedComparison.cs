using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace PdfAssemblySpike;

/// <summary>One masked span of a file: the mask that found it, the key it sits under, where the whole match and the masked value are.</summary>
internal sealed record MaskSpan(string Mask, string Key, int MatchOffset, int MatchLength, int Offset, int Length, string Value);

/// <summary>A file read once, with the spans both masks found, the shape check and the masked hash (absent when the shape check fails).</summary>
internal sealed record MaskedFile(
    long Length,
    string Sha256,
    string? MaskedSha256,
    IReadOnlyList<MaskSpan> Spans,
    int TagSpans,
    int XmpSpans,
    int DistinctTags,
    string ShapeFailure,
    string ShapeDetail);

/// <summary>Result of comparing one part of the first generation with the same part of the second.</summary>
internal sealed record PairComparison(
    int Order,
    string File,
    string Sha256A,
    string Sha256B,
    string? MaskedSha256A,
    string? MaskedSha256B,
    long LengthA,
    long LengthB,
    long RawDifferingBytes,
    int TagSpansA,
    int TagSpansB,
    int XmpSpansA,
    int XmpSpansB,
    int DistinctTagsA,
    int DistinctTagsB,
    long DifferingBytesOutsideSpans,
    long? FirstOffsetOutsideSpans,
    string BytesAroundA,
    string BytesAroundB,
    string Verdict,
    string FailReason,
    string Detail);

/// <summary>One control of the masked comparison: what it changed, what it expected and what it got.</summary>
internal sealed record ReproControl(
    string Id,
    string Group,
    string Input,
    string ExpectedVerdict,
    string ExpectedFailReason,
    string Verdict,
    string FailReason,
    bool AsExpected,
    bool RawSha256Equal,
    long RawDifferingBytes,
    long DifferingBytesOutsideSpans,
    int TagSpansA,
    int TagSpansB,
    int XmpSpansA,
    int XmpSpansB,
    string Note);

/// <summary>Result of comparing two generations of one set, with the controls of the comparison.</summary>
internal sealed record ReproResult(
    int Size,
    string FirstManifestSha256,
    string RepeatManifestSha256,
    string? FirstMaskedSetDigest,
    string? RepeatMaskedSetDigest,
    bool StructureEqual,
    string StructureDetail,
    bool FilesMatchManifests,
    IReadOnlyList<PairComparison> Parts,
    IReadOnlyList<ReproControl> Controls,
    bool ControlsPass,
    bool ControlFilesUnchanged,
    string Verdict)
{
    public bool Pass => Verdict == MaskedComparison.MaskedIdentical && ControlsPass && ControlFilesUnchanged && FilesMatchManifests;
}

/// <summary>
/// Compares two generations of the same set of partial PDFs. The PDF library
/// writes two random values on every save — a six-letter font-subset tag in
/// each embedded font name and the metadata document and instance IDs — so the
/// raw bytes of two generations differ. Exactly those two fields are masked, in
/// memory only, with constants of the same length; every other byte must be
/// equal. Files on disk are never written.
/// </summary>
internal static partial class MaskedComparison
{
    public const string MaskedIdentical = "masked-identical";
    public const string Fail = "fail";
    public const string TagMask = "M-TAG";
    public const string XmpMask = "M-XMP";
    public const string TagConstant = "XXXXXX";
    public const string UuidConstant = "00000000-0000-0000-0000-000000000000";
    public const string TagPatternText = @"/(?:BaseFont|FontName)/(?<m>[A-Z]{6})\+DejaVu#20Sans(?:,Bold)?(?=[\x00\t\n\f\r /\[\]<>(){}%])";
    public const string XmpPatternText = @"<xmpMM:(?<k>DocumentID|InstanceID)>uuid:(?<m>[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})</xmpMM:\k<k>>";

    public const string SpanCount = "span-count";
    public const string TagPairing = "tag-pairing";
    public const string LengthDiffers = "length-differs";
    public const string SpanOffsets = "span-offsets";
    public const string OutsideSpan = "outside-span";
    public const string MaskedHashDiffers = "masked-hash-differs";
    public const string StructureDiffers = "structure-differs";

    /// <summary>Both masks, verbatim, for the reports.</summary>
    public static object Masks => new[]
    {
        new { id = TagMask, pattern = TagPatternText, maskedGroup = "m", constant = TagConstant, options = "CultureInvariant", view = "Latin-1, one character per byte" },
        new { id = XmpMask, pattern = XmpPatternText, maskedGroup = "m", constant = UuidConstant, options = "CultureInvariant", view = "Latin-1, one character per byte" },
    };

    /// <summary>Finds the spans, checks their shape and computes the masked hash of a copy of the bytes.</summary>
    public static MaskedFile Analyze(byte[] bytes)
    {
        string view = Encoding.Latin1.GetString(bytes);
        var spans = new List<MaskSpan>();
        foreach (Match match in TagPattern().Matches(view))
        {
            var m = match.Groups["m"];
            spans.Add(new MaskSpan(TagMask, match.Value.Substring(1, 8), match.Index, match.Length, m.Index, m.Length, m.Value));
        }

        foreach (Match match in XmpPattern().Matches(view))
        {
            var m = match.Groups["m"];
            spans.Add(new MaskSpan(XmpMask, match.Groups["k"].Value, match.Index, match.Length, m.Index, m.Length, m.Value));
        }

        spans.Sort((a, b) => a.Offset.CompareTo(b.Offset));
        var tags = spans.Where(s => s.Mask == TagMask).ToList();
        var xmp = spans.Where(s => s.Mask == XmpMask).ToList();
        int distinctTags = tags.Select(t => t.Value).Distinct(StringComparer.Ordinal).Count();
        string failure = string.Empty;
        string detail = string.Empty;
        if (xmp.Count != 2 || xmp.Count(s => s.Key == "DocumentID") != 1 || xmp.Count(s => s.Key == "InstanceID") != 1)
        {
            failure = SpanCount;
            detail = string.Create(CultureInfo.InvariantCulture, $"{xmp.Count} {XmpMask} spans, expected one DocumentID and one InstanceID");
        }
        else if (tags.Count < 2 || tags.Count % 2 != 0)
        {
            failure = SpanCount;
            detail = string.Create(CultureInfo.InvariantCulture, $"{tags.Count} {TagMask} spans, expected an even number of at least 2");
        }
        else
        {
            var unpaired = tags.GroupBy(t => t.Value, StringComparer.Ordinal).FirstOrDefault(g => g.Count() != 2);
            if (unpaired is not null)
            {
                failure = TagPairing;
                detail = string.Create(CultureInfo.InvariantCulture, $"tag {unpaired.Key} appears {unpaired.Count()} times, expected exactly 2");
            }
        }

        string? masked = null;
        if (failure.Length == 0)
        {
            byte[] copy = (byte[])bytes.Clone();
            foreach (var span in spans)
            {
                byte[] constant = Encoding.ASCII.GetBytes(span.Mask == TagMask ? TagConstant : UuidConstant);
                constant.CopyTo(copy, span.Offset);
            }

            masked = Convert.ToHexStringLower(SHA256.HashData(copy));
        }

        return new MaskedFile(bytes.Length, Convert.ToHexStringLower(SHA256.HashData(bytes)), masked, spans, tags.Count, xmp.Count, distinctTags, failure, detail);
    }

    /// <summary>The masked hash of a file, or null when its spans do not have the expected shape.</summary>
    public static string? MaskedSha256OfFile(string path) => Analyze(File.ReadAllBytes(path)).MaskedSha256;

    /// <summary>
    /// The masked set digest: SHA-256 of the lines order|file|pages|firstGlobalPage|maskedSha256
    /// of every part in manifest order, joined by LF. Null when a part has no masked hash.
    /// </summary>
    public static string? MaskedSetDigest(SetManifest manifest)
    {
        if (manifest.Parts.Any(p => string.IsNullOrEmpty(p.MaskedSha256)))
        {
            return null;
        }

        string lines = string.Join('\n', manifest.Parts.OrderBy(p => p.Order).Select(p =>
            string.Create(CultureInfo.InvariantCulture, $"{p.Order}|{p.File}|{p.Pages}|{p.FirstGlobalPage}|{p.MaskedSha256}")));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(lines)));
    }

    /// <summary>The two-sided comparison of one pair of files, its checks in a fixed order; the first failing check gives the reason.</summary>
    public static PairComparison Compare(int order, string file, byte[] a, byte[] b)
    {
        var fa = Analyze(a);
        var fb = Analyze(b);
        int common = Math.Min(a.Length, b.Length);
        var inSpan = new bool[common];
        foreach (var span in fa.Spans.Concat(fb.Spans))
        {
            for (int i = span.Offset; i < span.Offset + span.Length && i < common; i++)
            {
                inSpan[i] = true;
            }
        }

        long differing = Math.Abs((long)a.Length - b.Length);
        long outside = 0;
        long? firstOutside = null;
        for (int i = 0; i < common; i++)
        {
            if (a[i] != b[i])
            {
                differing++;
                if (!inSpan[i])
                {
                    outside++;
                    firstOutside ??= i;
                }
            }
        }

        string verdict = MaskedIdentical;
        string reason = string.Empty;
        string detail = string.Empty;
        if (fa.ShapeFailure.Length > 0 || fb.ShapeFailure.Length > 0)
        {
            reason = fa.ShapeFailure.Length > 0 ? fa.ShapeFailure : fb.ShapeFailure;
            detail = fa.ShapeFailure.Length > 0 ? "first file: " + fa.ShapeDetail : "second file: " + fb.ShapeDetail;
        }
        else if (a.Length != b.Length)
        {
            reason = LengthDiffers;
            detail = string.Create(CultureInfo.InvariantCulture, $"{a.Length} and {b.Length} bytes");
        }
        else if (!SameSpans(fa.Spans, fb.Spans))
        {
            reason = SpanOffsets;
            detail = "the spans are not at the same offsets in both files";
        }
        else if (outside > 0)
        {
            reason = OutsideSpan;
            detail = string.Create(CultureInfo.InvariantCulture, $"{outside} differing bytes outside the masked spans, the first at offset {firstOutside}");
        }
        else if (!string.Equals(fa.MaskedSha256, fb.MaskedSha256, StringComparison.Ordinal))
        {
            reason = MaskedHashDiffers;
        }

        if (reason.Length > 0)
        {
            verdict = Fail;
        }

        return new PairComparison(order, file, fa.Sha256, fb.Sha256, fa.MaskedSha256, fb.MaskedSha256, a.Length, b.Length, differing,
            fa.TagSpans, fb.TagSpans, fa.XmpSpans, fb.XmpSpans, fa.DistinctTags, fb.DistinctTags, outside, firstOutside,
            Around(a, firstOutside), Around(b, firstOutside), verdict, reason, detail);
    }

    /// <summary>
    /// Compares the second generation of a set with the first: the structure of
    /// the two manifests, then every part pair, then runs the controls on the
    /// first part. The raw hashes are recorded and never compared.
    /// </summary>
    public static ReproResult CompareSets(string firstManifestPath, string repeatManifestPath, string tempDir)
    {
        var first = SetManifest.Load(firstManifestPath);
        var repeat = SetManifest.Load(repeatManifestPath);
        string structure = StructureDifference(first, repeat);
        var parts = new List<PairComparison>();
        bool filesMatch = true;
        if (structure.Length == 0)
        {
            foreach (var part in first.Parts.OrderBy(p => p.Order))
            {
                var other = repeat.Part(part.Order);
                byte[] a = File.ReadAllBytes(SetManifest.PartPath(firstManifestPath, part));
                byte[] b = File.ReadAllBytes(SetManifest.PartPath(repeatManifestPath, other));
                var pair = Compare(part.Order, part.File, a, b);
                filesMatch &= pair.Sha256A == part.Sha256 && pair.Sha256B == other.Sha256
                    && pair.MaskedSha256A == part.MaskedSha256 && pair.MaskedSha256B == other.MaskedSha256;
                parts.Add(pair);
            }
        }

        string pathA = SetManifest.PartPath(firstManifestPath, first.Parts[0]);
        string pathB = SetManifest.PartPath(repeatManifestPath, repeat.Part(first.Parts[0].Order));
        var controls = RunControls(pathA, pathB, tempDir);
        bool unchanged = SetManifest.Sha256OfFile(pathA) == first.Parts[0].Sha256
            && SetManifest.Sha256OfFile(pathB) == repeat.Part(first.Parts[0].Order).Sha256;
        bool allParts = structure.Length == 0 && parts.Count == first.Parts.Count && parts.All(p => p.Verdict == MaskedIdentical);
        return new ReproResult(first.TargetPages, SetManifest.Sha256OfFile(firstManifestPath), SetManifest.Sha256OfFile(repeatManifestPath),
            MaskedSetDigest(first), MaskedSetDigest(repeat), structure.Length == 0, structure, filesMatch, parts, controls,
            controls.All(c => c.AsExpected), unchanged, allParts ? MaskedIdentical : Fail);
    }

    /// <summary>The summary block of a comparison of two generations.</summary>
    public static object Summary(ReproResult r) => new
    {
        size = r.Size,
        firstManifestSha256 = r.FirstManifestSha256,
        repeatManifestSha256 = r.RepeatManifestSha256,
        rawManifestHashesCompared = false,
        firstMaskedSetDigest = r.FirstMaskedSetDigest,
        repeatMaskedSetDigest = r.RepeatMaskedSetDigest,
        structureEqual = r.StructureEqual,
        parts = r.Parts.Count,
        maskedIdentical = r.Parts.Count(p => p.Verdict == MaskedIdentical),
        fail = r.Parts.Count(p => p.Verdict != MaskedIdentical),
        minRawDifferingBytes = r.Parts.Count == 0 ? 0 : r.Parts.Min(p => p.RawDifferingBytes),
        maxRawDifferingBytes = r.Parts.Count == 0 ? 0 : r.Parts.Max(p => p.RawDifferingBytes),
        sumRawDifferingBytes = r.Parts.Sum(p => p.RawDifferingBytes),
        rawDifferingBytesCompared = false,
        minTagSpans = r.Parts.Count == 0 ? 0 : r.Parts.Min(p => Math.Min(p.TagSpansA, p.TagSpansB)),
        maxTagSpans = r.Parts.Count == 0 ? 0 : r.Parts.Max(p => Math.Max(p.TagSpansA, p.TagSpansB)),
        xmpSpans = r.Parts.Select(p => p.XmpSpansA).Concat(r.Parts.Select(p => p.XmpSpansB)).Distinct().Order().ToList(),
        filesMatchManifests = r.FilesMatchManifests,
        controls = r.Controls.Count,
        controlsPass = r.ControlsPass,
        controlFilesUnchanged = r.ControlFilesUnchanged,
        verdict = r.Verdict,
    };

    /// <summary>The detail block of a comparison of two generations, for the verification report.</summary>
    public static object Report(ReproResult r) => new
    {
        masks = Masks,
        reproducibility = Summary(r),
        structureDetail = r.StructureDetail,
        reproducibilityParts = r.Parts,
        reproducibilityControls = new { allAsExpected = r.ControlsPass, controlFilesUnchanged = r.ControlFilesUnchanged, controls = r.Controls },
    };

    private static string StructureDifference(SetManifest first, SetManifest repeat)
    {
        if (first.Seed != repeat.Seed || first.TargetPages != repeat.TargetPages || first.Parts.Count != repeat.Parts.Count)
        {
            return "seed, target pages or part count differ";
        }

        var a = first.Parts.OrderBy(p => p.Order).ToList();
        var b = repeat.Parts.OrderBy(p => p.Order).ToList();
        for (int i = 0; i < a.Count; i++)
        {
            if (a[i].Order != b[i].Order || a[i].File != b[i].File || a[i].Pages != b[i].Pages || a[i].FirstGlobalPage != b[i].FirstGlobalPage)
            {
                return string.Create(CultureInfo.InvariantCulture, $"part {a[i].Order} differs in order, file, pages or first global page");
            }
        }

        return string.Empty;
    }

    private static bool SameSpans(IReadOnlyList<MaskSpan> a, IReadOnlyList<MaskSpan> b) =>
        a.Count == b.Count && a.Zip(b).All(p => p.First.Mask == p.Second.Mask && p.First.Key == p.Second.Key
            && p.First.Offset == p.Second.Offset && p.First.Length == p.Second.Length);

    private static string Around(byte[] bytes, long? offset)
    {
        if (offset is null)
        {
            return string.Empty;
        }

        int start = (int)Math.Max(0, offset.Value - 16);
        int end = (int)Math.Min(bytes.Length, offset.Value + 16);
        return start >= end ? string.Empty : Convert.ToHexStringLower(bytes.AsSpan(start, end - start)) + string.Create(CultureInfo.InvariantCulture, $" (from offset {start})");
    }

    // ---- Controls -------------------------------------------------------------------------------

    private static List<ReproControl> RunControls(string pathA, string pathB, string tempDir)
    {
        Directory.CreateDirectory(tempDir);
        byte[] a = File.ReadAllBytes(pathA);
        byte[] b = File.ReadAllBytes(pathB);
        var fb = Analyze(b);
        string latinB = Encoding.Latin1.GetString(b);
        var controls = new List<ReproControl>();
        const string Base = "base";
        const string Extended = "extended";

        // MC-1: the real second generation.
        controls.Add(Check("MC-1", Base, "first part of the first generation against the same part of the second generation",
            MaskedIdentical, string.Empty, a, b, tempDir, null,
            (p, shaEqual) => !shaEqual && p.RawDifferingBytes >= 1 && p.DifferingBytesOutsideSpans == 0
                ? null : "raw sha256 must differ, at least one byte must differ and every differing byte must be inside a span"));

        // MC-2: an identical copy.
        controls.Add(Check("MC-2", Base, "first part against a byte copy of itself", MaskedIdentical, string.Empty, a, (byte[])a.Clone(), tempDir, null,
            (p, shaEqual) => shaEqual && p.RawDifferingBytes == 0 ? null : "raw sha256 must be equal and no byte may differ"));

        // MC-3: one byte changed outside every span.
        int dataStart = FirstStreamDataStart(b);
        controls.Add(dataStart < 0
            ? ControlError("MC-3", Base, "one byte changed outside the spans", OutsideSpan, "no stream data start was found")
            : InSpan(fb, dataStart + 3)
                ? ControlError("MC-3", Base, "one byte changed outside the spans", OutsideSpan, "the chosen byte lies inside a masked span")
                : Check("MC-3", Base, string.Create(CultureInfo.InvariantCulture, $"byte {dataStart + 3} (3 bytes after the first stream data start {dataStart}) XOR 0x01"),
                    Fail, OutsideSpan, a, Xor(b, dataStart + 3), tempDir, null, null));

        var firstFontName = fb.Spans.FirstOrDefault(s => s.Mask == TagMask && s.Key == "FontName");
        var firstBaseFont = fb.Spans.FirstOrDefault(s => s.Mask == TagMask && s.Key == "BaseFont");
        var documentId = fb.Spans.FirstOrDefault(s => s.Mask == XmpMask && s.Key == "DocumentID");
        var instanceId = fb.Spans.FirstOrDefault(s => s.Mask == XmpMask && s.Key == "InstanceID");
        if (firstFontName is null || firstBaseFont is null || documentId is null || instanceId is null)
        {
            foreach (string id in new[] { "MC-4", "MC-5", "MC-6", "MC-10", "MC-11", "MC-12", "MC-13", "MC-14", "MC-15", "MC-16", "MC-17", "MC-18" })
            {
                controls.Add(ControlError(id, id is "MC-4" or "MC-5" or "MC-6" ? Base : Extended, "span-based change", Fail,
                    "the second generation's first part lacks a FontName tag, a BaseFont tag or one of the two metadata IDs"));
            }
        }
        else
        {
            // MC-4: a tag changed inside its mask, keeping the mask shape but breaking the pairing.
            var existing = fb.Spans.Where(s => s.Mask == TagMask).Select(s => s.Value).ToHashSet(StringComparer.Ordinal);
            string shifted = firstFontName.Value;
            do
            {
                shifted = string.Concat(shifted.Select(c => c == 'Z' ? 'A' : (char)(c + 1)));
            }
            while (existing.Contains(shifted));

            controls.Add(Check("MC-4", Base, $"first FontName tag {firstFontName.Value} shifted to {shifted}", Fail, TagPairing, a,
                Overwrite(b, firstFontName.Offset, shifted), tempDir, null, null));

            // MC-5: the last letter of the first BaseFont tag replaced by a digit.
            controls.Add(Check("MC-5", Base, $"last letter of the first BaseFont tag {firstBaseFont.Value} replaced by '1'", Fail, SpanCount, a,
                Overwrite(b, firstBaseFont.Offset + 5, "1"), tempDir, null, null));

            // MC-6: the first a-f letter of the document ID made uppercase (or its first digit replaced by G).
            int letter = documentId.Value.IndexOfAny(['a', 'b', 'c', 'd', 'e', 'f']);
            string mc6Input = letter >= 0
                ? string.Create(CultureInfo.InvariantCulture, $"document ID character {letter} '{documentId.Value[letter]}' made uppercase")
                : "first digit of the document ID replaced by 'G'";
            byte[] mc6 = letter >= 0
                ? Overwrite(b, documentId.Offset + letter, char.ToUpperInvariant(documentId.Value[letter]).ToString())
                : Overwrite(b, documentId.Offset + documentId.Value.IndexOfAny("0123456789".ToCharArray()), "G");
            controls.Add(Check("MC-6", Base, mc6Input, Fail, SpanCount, a, mc6, tempDir, null, null));

            // MC-7: one byte appended.
            controls.Add(Check("MC-7", Base, "one LF byte appended", Fail, LengthDiffers, a, [.. b, (byte)'\n'], tempDir, null, null));

            // MC-8: one byte of the trailer /ID changed.
            int trailer = latinB.LastIndexOf("trailer", StringComparison.Ordinal);
            int idStart = trailer < 0 ? -1 : latinB.IndexOf("/ID[<", trailer, StringComparison.Ordinal);
            controls.Add(idStart < 0
                ? ControlError("MC-8", Extended, "one trailer /ID byte changed", OutsideSpan, "no trailer /ID was found")
                : Check("MC-8", Extended, string.Create(CultureInfo.InvariantCulture, $"first byte of the trailer /ID at {idStart + 5} XOR 0x01"),
                    Fail, OutsideSpan, a, Xor(b, idStart + 5), tempDir, null, null));

            // MC-9: one byte of the document information creation date changed.
            const string DateKey = "/CreationDate(D:";
            int date = latinB.IndexOf(DateKey, StringComparison.Ordinal);
            controls.Add(date < 0
                ? ControlError("MC-9", Extended, "one /CreationDate byte changed", OutsideSpan, "no /CreationDate was found")
                : Check("MC-9", Extended, string.Create(CultureInfo.InvariantCulture, $"first digit of /CreationDate at {date + DateKey.Length} XOR 0x01"),
                    Fail, OutsideSpan, a, Xor(b, date + DateKey.Length), tempDir, null, null));

            // MC-10: a non-hex character in the document ID.
            controls.Add(Check("MC-10", Extended, $"first character of the document ID '{documentId.Value[0]}' replaced by 'g'", Fail, SpanCount, a,
                Overwrite(b, documentId.Offset, "g"), tempDir, null, null));

            // MC-11: the document ID one character longer.
            controls.Add(Check("MC-11", Extended, "one extra '0' inserted after the first character of the document ID (37 characters)", Fail, SpanCount, a,
                Splice(b, documentId.Offset + 1, 0, "0"), tempDir, null, null));

            // MC-12: the first FontName tag naming another family.
            int family = firstFontName.Offset + firstFontName.Length + "+DejaVu#20".Length;
            controls.Add(Check("MC-12", Extended, $"family of the first FontName tag {firstFontName.Value} changed from DejaVu#20Sans to DejaVu#20Serif", Fail, SpanCount, a,
                Splice(b, family, "Sans".Length, "Serif"), tempDir, null, null));

            // MC-13: a tag of seven letters.
            controls.Add(Check("MC-13", Extended, $"a seventh letter 'Q' inserted after the first BaseFont tag {firstBaseFont.Value}", Fail, SpanCount, a,
                Splice(b, firstBaseFont.Offset + firstBaseFont.Length, 0, "Q"), tempDir, null, null));

            // Insertions go just before the line break that precedes the last startxref, so a delimiter follows them.
            int insertAt = latinB.LastIndexOf("\nstartxref", StringComparison.Ordinal);
            string fontNameMatch = latinB.Substring(firstFontName.MatchOffset, firstFontName.MatchLength);

            // MC-14: one extra tag occurrence.
            controls.Add(insertAt < 0
                ? ControlError("MC-14", Extended, "one extra tag occurrence", SpanCount, "no startxref was found")
                : Check("MC-14", Extended, $"an extra '{fontNameMatch}' inserted at byte {insertAt}", Fail, SpanCount, a,
                    Splice(b, insertAt, 0, fontNameMatch), tempDir, null, null));

            // MC-15: one extra metadata ID occurrence.
            string documentIdMatch = latinB.Substring(documentId.MatchOffset, documentId.MatchLength);
            controls.Add(insertAt < 0
                ? ControlError("MC-15", Extended, "one extra metadata ID occurrence", SpanCount, "no startxref was found")
                : Check("MC-15", Extended, $"an extra '{documentIdMatch}' inserted at byte {insertAt}", Fail, SpanCount, a,
                    Splice(b, insertAt, 0, documentIdMatch), tempDir, null, null));

            // MC-16: one tag occurrence dropped.
            controls.Add(Check("MC-16", Extended, $"'{fontNameMatch}' removed at byte {firstFontName.MatchOffset}", Fail, SpanCount, a,
                Splice(b, firstFontName.MatchOffset, firstFontName.MatchLength, string.Empty), tempDir, null, null));

            // MC-17: one metadata ID occurrence dropped.
            controls.Add(Check("MC-17", Extended, $"the instance ID element removed at byte {instanceId.MatchOffset}", Fail, SpanCount, a,
                Splice(b, instanceId.MatchOffset, instanceId.MatchLength, string.Empty), tempDir, null, null));

            // MC-18: one tag occurrence dropped and the same bytes added elsewhere: same length, same counts, other offsets.
            byte[] moved = Splice(b, firstFontName.MatchOffset, firstFontName.MatchLength, string.Empty);
            int movedAt = Encoding.Latin1.GetString(moved).LastIndexOf("\nstartxref", StringComparison.Ordinal);
            controls.Add(movedAt < 0
                ? ControlError("MC-18", Extended, "one tag occurrence moved", SpanOffsets, "no startxref was found")
                : Check("MC-18", Extended, $"'{fontNameMatch}' moved from byte {firstFontName.MatchOffset} to just before the last startxref (length unchanged)", Fail, SpanOffsets, a,
                    Splice(moved, movedAt, 0, fontNameMatch), tempDir, null, null));
        }

        return controls;
    }

    private static ReproControl Check(string id, string group, string input, string expectedVerdict, string expectedReason, byte[] a, byte[] changed,
        string tempDir, string? note, Func<PairComparison, bool, string?>? extra)
    {
        // The changed copy is written under the temporary folder, read back, compared and deleted.
        string path = Path.Combine(tempDir, id.ToLowerInvariant() + ".pdf");
        File.WriteAllBytes(path, changed);
        byte[] b = File.ReadAllBytes(path);
        File.Delete(path);
        var pair = Compare(0, id, a, b);
        bool shaEqual = pair.Sha256A == pair.Sha256B;
        string? extraFailure = extra?.Invoke(pair, shaEqual);
        bool asExpected = pair.Verdict == expectedVerdict && pair.FailReason == expectedReason && extraFailure is null;
        return new ReproControl(id, group, input, expectedVerdict, expectedReason, pair.Verdict, pair.FailReason, asExpected, shaEqual,
            pair.RawDifferingBytes, pair.DifferingBytesOutsideSpans, pair.TagSpansA, pair.TagSpansB, pair.XmpSpansA, pair.XmpSpansB,
            extraFailure ?? note ?? pair.Detail);
    }

    private static ReproControl ControlError(string id, string group, string input, string expectedReason, string reason) =>
        new(id, group, input, Fail, expectedReason, "control-error", string.Empty, false, false, 0, 0, 0, 0, 0, 0, reason);

    private static bool InSpan(MaskedFile file, int offset) => file.Spans.Any(s => offset >= s.Offset && offset < s.Offset + s.Length);

    private static byte[] Xor(byte[] bytes, int offset)
    {
        byte[] copy = (byte[])bytes.Clone();
        copy[offset] ^= 0x01;
        return copy;
    }

    private static byte[] Overwrite(byte[] bytes, int offset, string text)
    {
        byte[] copy = (byte[])bytes.Clone();
        Encoding.Latin1.GetBytes(text).CopyTo(copy, offset);
        return copy;
    }

    private static byte[] Splice(byte[] bytes, int offset, int removeLength, string insert) =>
        [.. bytes.AsSpan(0, offset), .. Encoding.Latin1.GetBytes(insert), .. bytes.AsSpan(offset + removeLength)];

    /// <summary>
    /// Offset of the first stream's data: after the first "stream" keyword that
    /// is not part of "endstream" and is followed by LF or CR LF. -1 when absent.
    /// The same rule the structural-check controls use.
    /// </summary>
    private static int FirstStreamDataStart(byte[] bytes)
    {
        ReadOnlySpan<byte> keyword = "stream"u8;
        for (int i = 0; i + keyword.Length < bytes.Length; i++)
        {
            if (!bytes.AsSpan(i, keyword.Length).SequenceEqual(keyword) || (i >= 3 && bytes.AsSpan(i - 3, 3).SequenceEqual("end"u8)))
            {
                continue;
            }

            int after = i + keyword.Length;
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

    [GeneratedRegex(TagPatternText, RegexOptions.CultureInvariant)]
    private static partial Regex TagPattern();

    [GeneratedRegex(XmpPatternText, RegexOptions.CultureInvariant)]
    private static partial Regex XmpPattern();
}
