using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PdfAssemblySpike;

/// <summary>A part as the producer's manifest describes it, with the record range it was printed from.</summary>
internal sealed record ProducerPart(int Order, string File, int Pages, int FirstGlobalPage, string Sha256, int FirstSequence, int LastSequence, int Records);

/// <summary>The comparison of one part with its second generation.</summary>
internal sealed record ReproPair(
    int Order,
    string File,
    string Verdict,
    string FailReason,
    int? FirstPage,
    string Detail,
    string ContentSha256A,
    string ContentSha256B,
    string Sha256A,
    string Sha256B,
    long LengthA,
    long LengthB,
    long RawDifferingBytes,
    string Classification,
    long? FirstOtherOffset,
    string BytesAroundA,
    string BytesAroundB);

/// <summary>One control of the comparison: the pair it fed in, what it expected and what it got.</summary>
internal sealed record ReproControl(
    string Id,
    string Input,
    string ExpectedVerdict,
    string ExpectedClassification,
    string Verdict,
    string FailReason,
    string Classification,
    long RawDifferingBytes,
    bool AsExpected);

/// <summary>The comparison of two generations of a set, with its controls.</summary>
internal sealed record ChromiumRepro(
    int Size,
    IReadOnlyList<ReproPair> Parts,
    string FirstContentSetDigest,
    string RepeatContentSetDigest,
    IReadOnlyList<ReproControl> Controls,
    bool ControlsPass,
    string Verdict)
{
    public bool Pass => Verdict == ChromiumInputs.ContentIdentical && ControlsPass;
}

/// <summary>
/// Reproducibility of Chromium-printed parts. Two generations of a set are compared
/// part by part: structure, then the content words of every page, then the pixels of
/// every page. The raw bytes are recorded and classified, never used as a pass
/// condition, and nothing is masked.
/// </summary>
internal static partial class ChromiumInputs
{
    public const string ContentIdentical = "content-identical";
    public const string StructureDiffers = "structure-differs";
    public const string ContentDiffers = "content-differs";
    public const string PixelsDiffer = "pixels-differ";

    public const string Identical = "identical";
    public const string InfoDatesOnly = "info-dates-only";
    public const string Other = "other";

    /// <summary>The producer's part list, read from its manifest.</summary>
    public static List<ProducerPart> LoadParts(string manifestPath)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(manifestPath));
        var parts = new List<ProducerPart>();
        foreach (var e in document.RootElement.GetProperty("parts").EnumerateArray())
        {
            parts.Add(new ProducerPart(
                e.GetProperty("order").GetInt32(),
                e.GetProperty("file").GetString() ?? string.Empty,
                e.GetProperty("pages").GetInt32(),
                e.GetProperty("firstGlobalPage").GetInt32(),
                e.GetProperty("sha256").GetString() ?? string.Empty,
                e.GetProperty("firstSequence").GetInt32(),
                e.GetProperty("lastSequence").GetInt32(),
                e.GetProperty("records").GetInt32()));
        }

        return parts;
    }

    /// <summary>Compares two generations of a set and runs the controls RC-1 to RC-4.</summary>
    public static ChromiumRepro CompareSets(string manifestPath, string repeatManifestPath, string tempDir)
    {
        Directory.CreateDirectory(tempDir);
        var first = LoadParts(manifestPath);
        var repeat = LoadParts(repeatManifestPath);
        int size = SetManifest.Load(manifestPath).TargetPages;
        var pairs = new List<ReproPair>();
        var digestLinesA = new List<string>();
        var digestLinesB = new List<string>();
        for (int i = 0; i < Math.Max(first.Count, repeat.Count); i++)
        {
            if (i >= first.Count || i >= repeat.Count)
            {
                var only = i < first.Count ? first[i] : repeat[i];
                pairs.Add(new ReproPair(only.Order, only.File, StructureDiffers, "part-count", null, $"{first.Count} vs {repeat.Count} parts",
                    string.Empty, string.Empty, string.Empty, string.Empty, 0, 0, 0, Other, null, string.Empty, string.Empty));
                continue;
            }

            var a = first[i];
            var b = repeat[i];
            var pair = ComparePair(PathOf(manifestPath, a), a, PathOf(repeatManifestPath, b), b, tempDir);
            pairs.Add(pair);
            digestLinesA.Add(DigestLine(a, pair.ContentSha256A));
            digestLinesB.Add(DigestLine(b, pair.ContentSha256B));
            if (pair.Verdict != ContentIdentical)
            {
                Console.WriteLine($"repro part {a.Order} {a.File}: {pair.Verdict} {pair.Detail}");
            }
        }

        string digestA = SetDigest(digestLinesA);
        string digestB = SetDigest(digestLinesB);
        var controls = RunControls(manifestPath, first, tempDir);
        bool controlsPass = controls.All(c => c.AsExpected);
        string verdict = pairs.Count > 0 && pairs.All(p => p.Verdict == ContentIdentical) && digestA == digestB
            ? ContentIdentical
            : pairs.FirstOrDefault(p => p.Verdict != ContentIdentical)?.Verdict ?? "content-set-digest-differs";
        return new ChromiumRepro(size, pairs, digestA, digestB, controls, controlsPass, verdict);
    }

    /// <summary>The line of a part in the content set digest.</summary>
    public static string DigestLine(ProducerPart part, string contentSha256) =>
        string.Create(CultureInfo.InvariantCulture, $"{part.Order}|{part.File}|{part.Pages}|{part.FirstGlobalPage}|{contentSha256}");

    /// <summary>SHA-256 (UTF-8) of the LF-joined part lines.</summary>
    public static string SetDigest(IEnumerable<string> lines) =>
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', lines))));

    /// <summary>
    /// Compares one part pair: structure, then words of every page, then pixels of
    /// every page (the first failing check gives the reason); the bytes are classified.
    /// </summary>
    public static ReproPair ComparePair(string pathA, ProducerPart a, string pathB, ProducerPart b, string tempDir)
    {
        byte[] bytesA = File.ReadAllBytes(pathA);
        byte[] bytesB = File.ReadAllBytes(pathB);
        var (classification, differing, firstOther, aroundA, aroundB) = Classify(bytesA, bytesB);
        string shaA = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytesA));
        string shaB = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytesB));

        var wordsA = PageWords.Extract(pathA, tempDir).Select(PageWords.ContentText).ToList();
        var wordsB = PageWords.Extract(pathB, tempDir).Select(PageWords.ContentText).ToList();
        string contentA = PageWords.ContentSha256(wordsA);
        string contentB = PageWords.ContentSha256(wordsB);

        ReproPair Result(string verdict, string reason, int? page, string detail) =>
            new(a.Order, a.File, verdict, reason, page, detail, contentA, contentB, shaA, shaB, bytesA.LongLength, bytesB.LongLength,
                differing, classification, firstOther, aroundA, aroundB);

        bool sameStructure = a.Order == b.Order && a.File == b.File && a.Pages == b.Pages && a.FirstGlobalPage == b.FirstGlobalPage
            && a.FirstSequence == b.FirstSequence && a.LastSequence == b.LastSequence && a.Records == b.Records;
        if (!sameStructure)
        {
            return Result(StructureDiffers, StructureDiffers, null,
                $"({a.Order}, {a.File}, {a.Pages}, {a.FirstGlobalPage}, {a.FirstSequence}-{a.LastSequence}, {a.Records}) vs ({b.Order}, {b.File}, {b.Pages}, {b.FirstGlobalPage}, {b.FirstSequence}-{b.LastSequence}, {b.Records})");
        }

        for (int k = 0; k < Math.Max(wordsA.Count, wordsB.Count); k++)
        {
            if (k >= wordsA.Count || k >= wordsB.Count || !string.Equals(wordsA[k], wordsB[k], StringComparison.Ordinal))
            {
                return Result(ContentDiffers, ContentDiffers, k + 1, $"page {k + 1}: words differ ({wordsA.Count} vs {wordsB.Count} pages extracted)");
            }
        }

        string renderDir = Path.Combine(tempDir, "render-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(renderDir);
        try
        {
            var renderedA = Verifier.Render(pathA, 1, a.Pages, Path.Combine(renderDir, "a"));
            var renderedB = Verifier.Render(pathB, 1, b.Pages, Path.Combine(renderDir, "b"));
            for (int k = 1; k <= a.Pages; k++)
            {
                if (!renderedA.TryGetValue(k, out string? fileA) || !renderedB.TryGetValue(k, out string? fileB)
                    || !PgmImage.Identical(PgmImage.Load(fileA), PgmImage.Load(fileB)))
                {
                    return Result(PixelsDiffer, PixelsDiffer, k, $"page {k}: rendered pages differ");
                }
            }
        }
        finally
        {
            Directory.Delete(renderDir, recursive: true);
        }

        return Result(ContentIdentical, string.Empty, null, string.Empty);
    }

    /// <summary>
    /// Classifies the raw byte differences of two files: identical; info-dates-only
    /// (same length, every differing byte inside the value of /CreationDate or
    /// /ModDate); or other, with the first differing offset outside those values and
    /// the 32 bytes around it in each file (Latin-1).
    /// </summary>
    public static (string Classification, long Differing, long? FirstOther, string AroundA, string AroundB) Classify(byte[] a, byte[] b)
    {
        if (a.AsSpan().SequenceEqual(b))
        {
            return (Identical, 0, null, string.Empty, string.Empty);
        }

        var spans = DateValueSpans(a);
        long differing = 0;
        long? firstOther = null;
        int common = Math.Min(a.Length, b.Length);
        for (int i = 0; i < common; i++)
        {
            if (a[i] != b[i])
            {
                differing++;
                if (firstOther is null && !spans.Any(s => i >= s.Start && i < s.End))
                {
                    firstOther = i;
                }
            }
        }

        differing += Math.Abs(a.Length - b.Length);
        if (a.Length != b.Length)
        {
            firstOther ??= common;
        }

        if (firstOther is null)
        {
            return (InfoDatesOnly, differing, null, string.Empty, string.Empty);
        }

        return (Other, differing, firstOther, Around(a, firstOther.Value), Around(b, firstOther.Value));
    }

    /// <summary>The byte ranges of the values between the parentheses of "/CreationDate (" and "/ModDate (".</summary>
    public static List<(int Start, int End)> DateValueSpans(byte[] bytes)
    {
        string latin = Encoding.Latin1.GetString(bytes);
        var spans = new List<(int, int)>();
        foreach (Match m in DateValuePattern().Matches(latin))
        {
            var value = m.Groups["v"];
            spans.Add((value.Index, value.Index + value.Length));
        }

        return spans;
    }

    private static string Around(byte[] bytes, long offset)
    {
        int start = (int)Math.Max(0, offset - 16);
        int end = (int)Math.Min(bytes.Length, offset + 16);
        return start >= end ? string.Empty : Encoding.Latin1.GetString(bytes, start, end - start);
    }

    private static string PathOf(string manifestPath, ProducerPart part) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(manifestPath))!, part.File);

    /// <summary>
    /// RC-1 a part against its byte copy; RC-2 against the second part of the set;
    /// RC-3 against a copy with the last digit of its creation date changed; RC-4
    /// against a copy with the first letter of its document title changed.
    /// </summary>
    private static List<ReproControl> RunControls(string manifestPath, List<ProducerPart> first, string tempDir)
    {
        var controls = new List<ReproControl>();
        string controlDir = Path.Combine(tempDir, "controls");
        Directory.CreateDirectory(controlDir);
        var a = first[0];
        string pathA = PathOf(manifestPath, a);
        byte[] bytesA = File.ReadAllBytes(pathA);
        string latin = Encoding.Latin1.GetString(bytesA);

        ReproControl Run(string id, string input, string expectedVerdict, string expectedClassification, string pathB, ProducerPart b)
        {
            var pair = ComparePair(pathA, a, pathB, b, controlDir);
            bool ok = pair.Verdict == expectedVerdict && (expectedClassification.Length == 0 || pair.Classification == expectedClassification);
            return new ReproControl(id, input, expectedVerdict, expectedClassification, pair.Verdict, pair.FailReason, pair.Classification, pair.RawDifferingBytes, ok);
        }

        // RC-1: a byte copy.
        string copy = Path.Combine(controlDir, "rc-1-" + a.File);
        File.WriteAllBytes(copy, bytesA);
        controls.Add(Run("RC-1", $"{a.File} vs its byte copy", ContentIdentical, Identical, copy, a));

        // RC-2: the second part of the same set.
        if (first.Count >= 2)
        {
            controls.Add(Run("RC-2", $"{a.File} vs {first[1].File} (second part)", StructureDiffers, string.Empty, PathOf(manifestPath, first[1]), first[1]));
        }
        else
        {
            controls.Add(new ReproControl("RC-2", "second part", StructureDiffers, string.Empty, "control-error", "the set has one part", string.Empty, 0, false));
        }

        // RC-3: the last digit of the creation date changed.
        var date = CreationDatePattern().Match(latin);
        int lastDigit = -1;
        if (date.Success)
        {
            var value = date.Groups["v"];
            for (int i = value.Index + value.Length - 1; i >= value.Index; i--)
            {
                if (char.IsAsciiDigit(latin[i]))
                {
                    lastDigit = i;
                    break;
                }
            }
        }

        if (lastDigit >= 0)
        {
            byte[] changed = (byte[])bytesA.Clone();
            changed[lastDigit] = changed[lastDigit] == (byte)'0' ? (byte)'1' : (byte)'0';
            string path = Path.Combine(controlDir, "rc-3-" + a.File);
            File.WriteAllBytes(path, changed);
            controls.Add(Run("RC-3", string.Create(CultureInfo.InvariantCulture, $"{a.File} vs a copy with the creation date digit at byte {lastDigit} changed"), ContentIdentical, InfoDatesOnly, path, a));
        }
        else
        {
            controls.Add(new ReproControl("RC-3", "creation date digit", ContentIdentical, InfoDatesOnly, "control-error", "no /CreationDate value with a digit", string.Empty, 0, false));
        }

        // RC-4: the first letter of the document title changed to the next letter.
        var title = TitlePattern().Match(latin);
        int letter = -1;
        if (title.Success)
        {
            var value = title.Groups["v"];
            for (int i = value.Index; i < value.Index + value.Length; i++)
            {
                if (char.IsAsciiLetter(latin[i]))
                {
                    letter = i;
                    break;
                }
            }
        }

        if (letter >= 0)
        {
            byte[] changed = (byte[])bytesA.Clone();
            changed[letter] = changed[letter] switch
            {
                (byte)'z' => (byte)'a',
                (byte)'Z' => (byte)'A',
                var c => (byte)(c + 1),
            };
            string path = Path.Combine(controlDir, "rc-4-" + a.File);
            File.WriteAllBytes(path, changed);
            controls.Add(Run("RC-4", string.Create(CultureInfo.InvariantCulture, $"{a.File} vs a copy with the title letter at byte {letter} changed"), ContentIdentical, Other, path, a));
        }
        else
        {
            controls.Add(new ReproControl("RC-4", "title letter", ContentIdentical, Other, "control-error", "no /Title value with a letter", string.Empty, 0, false));
        }

        Directory.Delete(controlDir, recursive: true);
        return controls;
    }

    [GeneratedRegex(@"/(?:CreationDate|ModDate) \((?<v>[^)]*)\)", RegexOptions.CultureInvariant)]
    private static partial Regex DateValuePattern();

    [GeneratedRegex(@"/CreationDate \((?<v>[^)]*)\)", RegexOptions.CultureInvariant)]
    private static partial Regex CreationDatePattern();

    [GeneratedRegex(@"/Title \((?<v>[^)]*)\)", RegexOptions.CultureInvariant)]
    private static partial Regex TitlePattern();
}
