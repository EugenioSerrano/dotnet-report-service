using System.Security.Cryptography;
using PdfSharp.Drawing;
using PdfSharp.Fonts;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using ReportService.Pdf.Assembly;
using ReportService.Pdf.Verification;

namespace ReportService.Pdf.Assembly.Tests;

/// <summary>
/// The documents and contracts the assembly tests work with: A4 parts generated in
/// the test's own temporary directory with the vendored Liberation Sans font, the
/// page frame the template declares, the numbering contract and the expectations
/// the verification compares against. Every value is derived from the contract the
/// tests declare, never from the implementation's output.
/// </summary>
internal static class AssemblyFixture
{
    /// <summary>The page frame of an A4 template with 12/10/12/10 mm margins.</summary>
    public static PageFrame Frame { get; } = PageFrame.FromMillimetres(210, 297, 12, 10, 12, 10);

    /// <summary>The reserved numbering area at the bottom of the page, in millimetres.</summary>
    public static NumberingArea Area { get; } = new(120, 268, 80, 8);

    /// <summary>The width Chromium writes for an A4 page, in points (ADR-007 §1.3).</summary>
    public const double ChromiumA4WidthPt = 594.95996d;

    /// <summary>The height Chromium writes for an A4 page, in points (ADR-007 §1.3).</summary>
    public const double ChromiumA4HeightPt = 841.91998d;

    /// <summary>The packaged font, read from the test project's own output.</summary>
    public static NumberingFont Font()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fonts", "LiberationSans-Regular.ttf");
        return new NumberingFont(
            "Liberation Sans",
            "fonts/LiberationSans-Regular.ttf",
            9,
            File.ReadAllBytes(path));
    }

    /// <summary>The numbering contract the tests assemble with.</summary>
    public static PageNumbering Contract(NumberingArea? area = null, string format = "Page {X} of {Y}", string culture = "en-US") =>
        new(area ?? Area, Font(), format, culture);

    /// <summary>A temporary directory unique to one test.</summary>
    public static string NewDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "report-assembly-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>Creates the face name the writing library resolves for the packaged font.</summary>
    public static string Face()
    {
        var resolver = PackagedFontResolver.Instance;
        try
        {
            GlobalFontSettings.FontResolver = resolver;
        }
        catch (InvalidOperationException)
        {
            // A resolver installed earlier in the process is the same packaged one.
        }

        return resolver.Register(Font());
    }

    /// <summary>Creates one part: <paramref name="pages"/> pages, each carrying its own two words.</summary>
    /// <param name="directory">The part's directory.</param>
    /// <param name="index">The part's manifest position, which also names its file.</param>
    /// <param name="pages">How many pages the part holds.</param>
    /// <param name="fileName">A suffix that distinguishes parts built in one test.</param>
    /// <param name="widthPt">The page width to print, in points; the frame's width when null.</param>
    /// <param name="heightPt">The page height to print, in points; the frame's height when null.</param>
    public static AssemblyPart CreatePart(
        string directory,
        int index,
        int pages,
        string fileName = "",
        double? widthPt = null,
        double? heightPt = null)
    {
        var path = Path.Combine(directory, $"part-{index}{(fileName.Length == 0 ? string.Empty : "-" + fileName)}.pdf");
        using (var document = new PdfDocument())
        {
            var font = new XFont(Face(), 10, XFontStyleEx.Regular, new XPdfFontOptions(PdfFontEncoding.Unicode));
            for (var page = 1; page <= pages; page++)
            {
                var sheet = document.AddPage();
                sheet.Width = XUnit.FromPoint(widthPt ?? Frame.WidthPt);
                sheet.Height = XUnit.FromPoint(heightPt ?? Frame.HeightPt);
                using var graphics = XGraphics.FromPdfPage(sheet);
                graphics.DrawString(
                    $"part{index}page{page}start part{index}page{page}end",
                    font,
                    XBrushes.Black,
                    new XRect(40, 40, 400, 30),
                    XStringFormats.TopLeft);
            }

            document.Save(path);
        }

        return new AssemblyPart(index, path, pages, new FileInfo(path).Length);
    }

    /// <summary>The expectations of a part set, as their commits would state them.</summary>
    public static IReadOnlyList<AssemblyPartExpectation> Expectations(IEnumerable<AssemblyPart> parts) =>
        [.. parts.Select(part => new AssemblyPartExpectation(
            part.PartIndex,
            part.Path,
            part.PageCount,
            part.SizeBytes,
            Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(part.Path)))))];

    /// <summary>Assembles a part set into <paramref name="output"/>.</summary>
    public static async Task<AssemblyResult> AssembleAsync(
        IReadOnlyList<AssemblyPart> parts,
        string temporaryRoot,
        string output,
        PageNumbering? numbering = null,
        int batchSize = AssemblyLimits.BatchSize)
    {
        var assembler = new PdfSharpAssembler();
        return await assembler.AssembleAsync(
            new AssemblyRequest(parts, numbering ?? Contract(), output, temporaryRoot, batchSize),
            CancellationToken.None);
    }

    /// <summary>Rebuilds a document, keeping the pages the predicate selects, into <paramref name="target"/>.</summary>
    public static string Rebuild(string source, string target, Func<int, bool> keep, Action<PdfDocument>? mutate = null)
    {
        using var input = PdfReader.Open(source, PdfDocumentOpenMode.Import);
        using var output = new PdfDocument();
        var number = 0;
        foreach (var page in input.Pages)
        {
            number++;
            if (keep(number))
            {
                output.AddPage(page);
            }
        }

        mutate?.Invoke(output);
        output.Save(target);
        return target;
    }

    /// <summary>The text of every page of a document, one line per page.</summary>
    public static IReadOnlyList<string> PageTexts(string path) =>
        [.. PdfWordReader.Read(path, Frame.Margins).Pages.Select(page => string.Join(' ', page.Words.Select(word => word.Text)))];
}
