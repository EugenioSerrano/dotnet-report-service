using System.Globalization;
using System.Text;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Content;
using PdfSharp.Pdf.Content.Objects;
using PdfSharp.Pdf.IO;
using TextIntegrityCheck;
using UglyToad.PdfPig;

namespace ReaderAgreement;

/// <summary>The reference reader: the earlier checker's poppler reading, called unchanged.</summary>
internal static class OracleExtractor
{
    public static async Task<PdfText> ExtractAsync(string pdf, TimeSpan timeout, CancellationToken cancellation)
    {
        if (!OnPath("pdftotext") || !OnPath("pdfinfo"))
        {
            throw new InvalidOperationException("The reference reader runs only where poppler-utils is installed (the checker image).");
        }

        return await Poppler.ExtractAsync(pdf, timeout, cancellation);
    }

    private static bool OnPath(string tool) =>
        (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Any(d => File.Exists(Path.Combine(d, tool)));
}

/// <summary>
/// The PdfPig candidate, both configurations from one reading: the default word
/// extractor of the library as shipped, and the same words with a vertical extent
/// built from the baseline and the point size. Coordinates are converted from PDF
/// user space (origin bottom left) to the checker's frame (origin top left).
/// </summary>
internal static class PdfPigExtractor
{
    public static (PdfText AsShipped, PdfText Baseline, Dictionary<string, string> Notes) Extract(string pdf)
    {
        using var document = UglyToad.PdfPig.PdfDocument.Open(pdf);
        var sizes = new List<(double Width, double Height)>();
        var shipped = new List<PdfWord>();
        var baseline = new List<PdfWord>();
        var rotations = new SortedSet<int>();
        var origins = new SortedSet<string>(StringComparer.Ordinal);
        for (var n = 1; n <= document.NumberOfPages; n++)
        {
            var page = document.GetPage(n);
            var height = page.Height;
            sizes.Add((page.Width, height));
            rotations.Add(page.Rotation.Value);
            origins.Add(string.Create(CultureInfo.InvariantCulture, $"{page.MediaBox.Bounds.Left:0.##},{page.MediaBox.Bounds.Bottom:0.##}"));
            foreach (var word in page.GetWords())
            {
                if (string.IsNullOrWhiteSpace(word.Text))
                {
                    continue;
                }

                var box = word.BoundingBox;
                shipped.Add(new PdfWord(n, box.Left, height - box.Top, box.Right, height - box.Bottom, word.Text));
                var first = word.Letters[0];
                var pointSize = word.Letters.Max(l => l.PointSize);
                var yMax = height - first.StartBaseLine.Y;
                baseline.Add(new PdfWord(n, box.Left, yMax - pointSize, box.Right, yMax, word.Text));
            }
        }

        var notes = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["rotations"] = string.Join(',', rotations),
            ["mediaBoxOrigins"] = string.Join(';', origins),
        };
        return (
            new PdfText { Pages = document.NumberOfPages, PageSizes = sizes, Words = shipped },
            new PdfText { Pages = document.NumberOfPages, PageSizes = [.. sizes], Words = baseline },
            notes);
    }
}

/// <summary>What PDFsharp 6.2.4 can read from a PDF through its public API alone.</summary>
internal sealed class CapabilityRecord
{
    public string PdfSha256 { get; set; } = string.Empty;

    public int Pages { get; set; }

    public List<double[]> PageSizes { get; set; } = [];

    public int TextShowingOperators { get; set; }

    public int StringOperands { get; set; }

    /// <summary>The first string operand of page 1, as the bytes the content stream holds (hex).</summary>
    public string? FirstOperandHex { get; set; }

    /// <summary>The same operand as PDFsharp exposes it (CString.Value), escaped.</summary>
    public string? FirstOperandValue { get; set; }

    /// <summary>The font resource the first operand is drawn with, and the type PDFsharp gives its object.</summary>
    public string? FirstFont { get; set; }

    public string? FirstFontObjectType { get; set; }

    public bool FirstFontHasToUnicode { get; set; }

    /// <summary>Whether any public member returned Unicode text for a text operand.</summary>
    public bool UnicodeText { get; set; }

    /// <summary>Whether any public member returned a position for a text operand.</summary>
    public bool Positions { get; set; }

    public string Finding { get; set; } = string.Empty;
}

/// <summary>
/// The PDFsharp capability probe. Allowed: public members only. Not allowed:
/// writing a character-map parser, a font-program reader or a text-matrix
/// engine — that would be building a reader, not measuring PDFsharp.
/// </summary>
internal static class PdfSharpProbe
{
    private static readonly HashSet<string> TextShowing = new(StringComparer.Ordinal) { "Tj", "TJ", "'", "\"" };

    public static CapabilityRecord Probe(string pdf, string pdfSha256)
    {
        using var document = PdfReader.Open(pdf, PdfDocumentOpenMode.Import);
        var record = new CapabilityRecord { PdfSha256 = pdfSha256, Pages = document.PageCount };
        string? currentFont = null;
        for (var i = 0; i < document.PageCount; i++)
        {
            var page = document.Pages[i];
            record.PageSizes.Add([page.MediaBox.Width, page.MediaBox.Height]);
            var content = ContentReader.ReadContent(page);
            Walk(content, page, i == 0, record, ref currentFont);
        }

        record.UnicodeText = false;
        record.Positions = false;
        record.Finding = record.TextShowingOperators == 0
            ? "No text-showing operator was found through the content reader."
            : "PDFsharp exposes the content stream's operators and their raw string operands (font-encoded bytes, here glyph codes), "
              + "and the font dictionaries as plain dictionaries. No public member maps an operand to Unicode text or gives its position "
              + "on the page; doing so needs a character-map parser and a text-matrix engine, which is building a reader.";
        return record;
    }

    private static void Walk(CSequence sequence, PdfPage page, bool firstPage, CapabilityRecord record, ref string? currentFont)
    {
        foreach (var item in sequence)
        {
            switch (item)
            {
                case COperator op when op.OpCode.Name == "Tf" && op.Operands.Count > 0 && op.Operands[0] is CName name:
                    currentFont = name.Name;
                    break;
                case COperator op when TextShowing.Contains(op.OpCode.Name):
                    record.TextShowingOperators++;
                    foreach (var operand in Flatten(op.Operands))
                    {
                        if (operand is not CString s)
                        {
                            continue;
                        }

                        record.StringOperands++;
                        if (firstPage && record.FirstOperandHex is null)
                        {
                            record.FirstOperandValue = Escape(s.Value);
                            record.FirstOperandHex = Convert.ToHexString(Encoding.Latin1.GetBytes(s.Value));
                            record.FirstFont = currentFont;
                            DescribeFont(page, currentFont, record);
                        }
                    }

                    break;
                case CSequence inner:
                    Walk(inner, page, firstPage, record, ref currentFont);
                    break;
            }
        }
    }

    private static IEnumerable<CObject> Flatten(CSequence operands)
    {
        foreach (var o in operands)
        {
            if (o is CArray array)
            {
                foreach (var inner in Flatten(array))
                {
                    yield return inner;
                }
            }
            else
            {
                yield return o;
            }
        }
    }

    private static void DescribeFont(PdfPage page, string? font, CapabilityRecord record)
    {
        if (font is null)
        {
            return;
        }

        var fonts = page.Resources.Elements.GetDictionary("/Font");
        var item = fonts?.Elements.GetObject(font);
        record.FirstFontObjectType = item?.GetType().FullName;
        record.FirstFontHasToUnicode = item is PdfDictionary dictionary && dictionary.Elements.ContainsKey("/ToUnicode");
    }

    private static string Escape(string value)
    {
        var builder = new StringBuilder();
        foreach (var c in value)
        {
            builder.Append(c is >= ' ' and <= '~' ? c.ToString() : $"\\u{(int)c:x4}");
        }

        return builder.ToString();
    }
}
