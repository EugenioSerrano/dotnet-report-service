using System.Globalization;
using PdfSharp.Drawing;
using PdfSharp.Fonts;
using PdfSharp.Pdf;
using ReportService.Pdf.Assembly;
using ReportService.Pdf.Assembly.PageCodes;
using ReportService.Pdf.Verification;
using ReportService.Pdf.Verification.PageCodes;

namespace ReportService.PerformanceTests;

/// <summary>
/// The synthetic parts the assembly measurements are taken on: A4 pages written by the
/// PDF library itself, one short line of text per page. They are lighter than parts a
/// real template prints - fewer words, one font, no rows that span pages - so every
/// figure measured on them is a lower bound for a real document of the same page count.
/// The numbering contract and the per-page code are the ones a template could declare;
/// the code is drawn by the product's library-free reference generator.
/// </summary>
internal static class SyntheticAssemblyParts
{
    /// <summary>The name of this part builder, recorded with every figure taken on it.</summary>
    public const string BuilderName = "synthetic-one-line-a4";

    /// <summary>The page frame the parts are printed on: A4 portrait with the fixture's margins.</summary>
    public static PageFrame Frame { get; } = PageFrame.FromMillimetres(210, 297, 12, 10, 12, 10);

    /// <summary>The code's rectangle, in points, clear of the numbering area.</summary>
    public static PageCodeRectangle CodeArea { get; } = new(160, 100, 20, 20);

    /// <summary>Builds <paramref name="parts"/> parts holding <paramref name="pages"/> pages in all.</summary>
    /// <param name="directory">Where the parts are written.</param>
    /// <param name="pages">The total page count.</param>
    /// <param name="parts">How many parts the pages are spread over.</param>
    public static List<AssemblyPart> Build(string directory, int pages, int parts)
    {
        var font = new XFont(Face(), 9, XFontStyleEx.Regular, new XPdfFontOptions(PdfFontEncoding.Unicode));
        var built = new List<AssemblyPart>(parts);
        var remaining = pages;
        for (var index = 0; index < parts; index++)
        {
            var pageCount = index == parts - 1 ? remaining : pages / parts;
            remaining -= pageCount;
            var path = Path.Combine(directory, $"part-{index.ToString("0000", CultureInfo.InvariantCulture)}.pdf");
            using (var document = new PdfDocument())
            {
                for (var page = 1; page <= pageCount; page++)
                {
                    var sheet = document.AddPage();
                    sheet.Width = XUnit.FromPoint(595.276);
                    sheet.Height = XUnit.FromPoint(841.89);
                    using var graphics = XGraphics.FromPdfPage(sheet);
                    graphics.DrawString(
                        $"part{index}page{page}",
                        font,
                        XBrushes.Black,
                        new XRect(40, 40, 400, 20),
                        XStringFormats.TopLeft);
                }

                document.Save(path);
            }

            built.Add(new AssemblyPart(index, path, pageCount, new FileInfo(path).Length));
        }

        return built;
    }

    /// <summary>The numbering contract the parts are stamped with.</summary>
    public static PageNumbering Contract() => new(
        new NumberingArea(120, 268, 80, 8),
        StampFont(),
        "Page {X} of {Y}",
        "en-US");

    /// <summary>
    /// The per-page code every page carries: the reference generator's grid, its value the
    /// page's number, the document's total and the page's own printed line.
    /// </summary>
    public static AssemblyPageCode Code()
    {
        var generator = new ReferenceGridPageCodeGenerator();
        var problems = new List<PageCodeProblem>();
        var source = PageCodeSource.Parse("{page.number}/{page.total}|{match}", "part[0-9]+page[0-9]+", 20, problems)
            ?? throw new InvalidOperationException(string.Join("; ", problems.Select(problem => problem.Problem)));
        var declaration = new PageCodeDeclaration(generator.Name, new Dictionary<string, string>(), CodeArea, source);
        return new AssemblyPageCode(
            declaration,
            new RegisteredPageCodeGenerator(generator, PageCodeGeneratorDescriptor.Of(generator, PageCodeGeneratorOrigins.Reference)),
            "capacity-measurement",
            "1.0.0",
            Frame.Margins,
            Contract().Area);
    }

    /// <summary>The expectations of a part set, as their commits would state them.</summary>
    /// <param name="parts">The parts.</param>
    public static List<AssemblyPartExpectation> Expectations(IEnumerable<AssemblyPart> parts) =>
        [.. parts.Select(part => new AssemblyPartExpectation(
            part.PartIndex,
            part.Path,
            part.PageCount,
            part.SizeBytes,
            Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(part.Path)))))];

    private static NumberingFont StampFont() => new(
        "Liberation Sans",
        "fonts/LiberationSans-Regular.ttf",
        9,
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fonts", "LiberationSans-Regular.ttf")));

    private static string Face()
    {
        var resolver = PackagedFontResolver.Instance;
        try
        {
            GlobalFontSettings.FontResolver = resolver;
        }
        catch (InvalidOperationException)
        {
            // The resolver is process-wide and may already be installed by another case.
        }

        return resolver.Register(StampFont());
    }
}
