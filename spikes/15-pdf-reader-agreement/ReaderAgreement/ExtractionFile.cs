using System.Security.Cryptography;
using System.Text.Json;
using TextIntegrityCheck;

namespace ReaderAgreement;

/// <summary>
/// What one reader read from one PDF, in the earlier checker's own terms: page
/// count, page sizes and every word with its page and box (points, origin top
/// left, y downwards). No timing is stored here, so the files of repeated runs
/// can be compared byte for byte.
/// </summary>
internal sealed class ExtractionFile
{
    public string Reader { get; set; } = string.Empty;

    public string Configuration { get; set; } = string.Empty;

    public string PdfSha256 { get; set; } = string.Empty;

    public int Pages { get; set; }

    public List<double[]> PageSizes { get; set; } = [];

    /// <summary>Each word as [page, xMin, yMin, xMax, yMax].</summary>
    public List<double[]> Boxes { get; set; } = [];

    /// <summary>Each word's text, in the same order as <see cref="Boxes"/>.</summary>
    public List<string> Texts { get; set; } = [];

    /// <summary>Reader-specific facts (rotation, page box origin, warnings); never used by the rules.</summary>
    public Dictionary<string, string> Notes { get; set; } = [];

    public static ExtractionFile From(string reader, string configuration, string pdfSha256, PdfText text) => new()
    {
        Reader = reader,
        Configuration = configuration,
        PdfSha256 = pdfSha256,
        Pages = text.Pages,
        PageSizes = [.. text.PageSizes.Select(s => new[] { s.Width, s.Height })],
        Boxes = [.. text.Words.Select(w => new[] { w.Page, w.XMin, w.YMin, w.XMax, w.YMax })],
        Texts = [.. text.Words.Select(w => w.Text)],
    };

    public PdfText ToPdfText()
    {
        if (Boxes.Count != Texts.Count)
        {
            throw new InvalidDataException($"{Boxes.Count} boxes and {Texts.Count} texts.");
        }

        var words = new List<PdfWord>(Boxes.Count);
        for (var i = 0; i < Boxes.Count; i++)
        {
            var b = Boxes[i];
            words.Add(new PdfWord((int)b[0], b[1], b[2], b[3], b[4], Texts[i]));
        }

        return new PdfText
        {
            Pages = Pages,
            PageSizes = [.. PageSizes.Select(s => (s[0], s[1]))],
            Words = words,
        };
    }

    public void Write(string path)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(this, Settings.JsonLine));
    }

    public static ExtractionFile Read(string path) =>
        JsonSerializer.Deserialize<ExtractionFile>(File.ReadAllBytes(path), Settings.Json) ?? throw new InvalidDataException($"Empty extraction file {path}.");

    public static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }
}
