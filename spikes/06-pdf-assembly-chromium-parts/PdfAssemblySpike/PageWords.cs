using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace PdfAssemblySpike;

/// <summary>One word as the text extractor reports it, with its box in points from the top-left corner.</summary>
internal sealed record PageWord(double XMin, double YMin, double XMax, double YMax, string Text)
{
    public double CentreX => (XMin + XMax) / 2;

    public double CentreY => (YMin + YMax) / 2;
}

/// <summary>The words of one page.</summary>
internal sealed record WordPage(int Number, double Width, double Height, IReadOnlyList<PageWord> Words);

/// <summary>
/// Reads the words of PDF pages with their positions ("pdftotext -bbox"), splits
/// each page into the words inside the numbering area and the content words, and
/// puts the content words in a canonical order (by top, then left, then text).
/// </summary>
internal static partial class PageWords
{
    /// <summary>Pages per extractor run, so a large output never becomes one huge text file.</summary>
    public const int ExtractChunk = 1000;

    /// <summary>Height of the earlier experiment's numbering band at the bottom of the page.</summary>
    public static readonly double BottomBandHeight = PageLayout.FooterBandHeight;

    /// <summary>Every page of a file, in order.</summary>
    public static List<WordPage> Extract(string pdf, string tempDir) => Extract(pdf, tempDir, null, null);

    /// <summary>Pages first..last of a file (all pages when both are null).</summary>
    public static List<WordPage> Extract(string pdf, string tempDir, int? first, int? last)
    {
        Directory.CreateDirectory(tempDir);
        string html = Path.Combine(tempDir, "words-" + Guid.NewGuid().ToString("N") + ".html");
        var arguments = new List<string> { "-enc", "UTF-8", "-bbox" };
        if (first is not null && last is not null)
        {
            arguments.AddRange(["-f", first.Value.ToString(CultureInfo.InvariantCulture), "-l", last.Value.ToString(CultureInfo.InvariantCulture)]);
        }

        arguments.Add(pdf);
        arguments.Add(html);
        try
        {
            var result = ExternalTool.Run(Verifier.PdfToText, arguments);
            if (result.ExitCode != 0)
            {
                throw new InvalidOperationException($"pdftotext -bbox {pdf} exited {result.ExitCode}: {result.StandardError.Trim()}");
            }

            return Parse(File.ReadLines(html, Encoding.UTF8), first ?? 1);
        }
        finally
        {
            if (File.Exists(html))
            {
                File.Delete(html);
            }
        }
    }

    /// <summary>Parses the extractor's page and word elements; entities in the word text are decoded.</summary>
    public static List<WordPage> Parse(IEnumerable<string> lines, int firstNumber)
    {
        var pages = new List<WordPage>();
        List<PageWord>? words = null;
        double width = 0;
        double height = 0;
        foreach (string line in lines)
        {
            var page = PagePattern().Match(line);
            if (page.Success)
            {
                width = Number(page.Groups["w"].Value);
                height = Number(page.Groups["h"].Value);
                words = [];
                continue;
            }

            var word = WordPattern().Match(line);
            if (word.Success && words is not null)
            {
                words.Add(new PageWord(
                    Number(word.Groups["x0"].Value),
                    Number(word.Groups["y0"].Value),
                    Number(word.Groups["x1"].Value),
                    Number(word.Groups["y1"].Value),
                    WebUtility.HtmlDecode(word.Groups["t"].Value)));
                continue;
            }

            if (line.Contains("</page>", StringComparison.Ordinal) && words is not null)
            {
                pages.Add(new WordPage(firstNumber + pages.Count, width, height, words));
                words = null;
            }
        }

        return pages;
    }

    /// <summary>A word belongs to the numbering area when the centre of its box lies inside it (output pages).</summary>
    public static bool CentreInArea(PageWord w) =>
        w.CentreX >= PageLayout.NumberingAreaLeft && w.CentreX <= PageLayout.NumberingAreaRight
        && w.CentreY >= PageLayout.NumberingAreaTop && w.CentreY <= PageLayout.NumberingAreaBottom;

    /// <summary>Stricter test for source pages: any overlap of the word's box with the numbering area.</summary>
    public static bool IntersectsArea(PageWord w) =>
        w.XMax >= PageLayout.NumberingAreaLeft && w.XMin <= PageLayout.NumberingAreaRight
        && w.YMax >= PageLayout.NumberingAreaTop && w.YMin <= PageLayout.NumberingAreaBottom;

    /// <summary>A word whose centre lies in the bottom 15 mm of the page (the earlier experiment's band).</summary>
    public static bool InBottomBand(PageWord w, double pageHeight) => w.CentreY >= pageHeight - BottomBandHeight;

    /// <summary>The content words of a page in canonical order: every word outside the numbering area.</summary>
    public static List<string> ContentWords(WordPage page) =>
        [.. page.Words.Where(w => !CentreInArea(w))
            .OrderBy(w => w.YMin)
            .ThenBy(w => w.XMin)
            .ThenBy(w => w.Text, StringComparer.Ordinal)
            .Select(w => w.Text)];

    /// <summary>The words inside the numbering area, left to right, joined by one space.</summary>
    public static string AreaText(WordPage page) =>
        string.Join(' ', page.Words.Where(CentreInArea).OrderBy(w => w.YMin).ThenBy(w => w.XMin).Select(w => w.Text));

    /// <summary>The content words joined by one space (words never contain a space).</summary>
    public static string ContentText(WordPage page) => string.Join(' ', ContentWords(page));

    /// <summary>Every "Page k of n" in a word sequence, as four consecutive words.</summary>
    public static List<(int K, int N)> PageCounters(IReadOnlyList<string> words)
    {
        var found = new List<(int, int)>();
        for (int i = 0; i + 3 < words.Count; i++)
        {
            if (words[i] == "Page" && words[i + 2] == "of"
                && int.TryParse(words[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out int k)
                && int.TryParse(words[i + 3], NumberStyles.None, CultureInfo.InvariantCulture, out int n))
            {
                found.Add((k, n));
            }
        }

        return found;
    }

    /// <summary>SHA-256 (UTF-8) of a part's content words: words joined by one space, pages joined by U+000C.</summary>
    public static string ContentSha256(IEnumerable<string> pageContentTexts) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\f', pageContentTexts))));

    /// <summary>A footer pattern: the band's words with every run of digits, signs and separators replaced by '#'.</summary>
    public static string BandPattern(IEnumerable<PageWord> words) =>
        AmountPattern().Replace(string.Join(' ', words.OrderBy(w => w.YMin).ThenBy(w => w.XMin).Select(w => w.Text)), "#");

    private static double Number(string text) => double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);

    [GeneratedRegex("<page width=\"(?<w>[0-9.]+)\" height=\"(?<h>[0-9.]+)\">", RegexOptions.CultureInvariant)]
    private static partial Regex PagePattern();

    [GeneratedRegex("<word xMin=\"(?<x0>-?[0-9.]+)\" yMin=\"(?<y0>-?[0-9.]+)\" xMax=\"(?<x1>-?[0-9.]+)\" yMax=\"(?<y1>-?[0-9.]+)\">(?<t>.*)</word>", RegexOptions.CultureInvariant)]
    private static partial Regex WordPattern();

    [GeneratedRegex(@"[-+]?[0-9][0-9.,]*", RegexOptions.CultureInvariant)]
    private static partial Regex AmountPattern();
}
