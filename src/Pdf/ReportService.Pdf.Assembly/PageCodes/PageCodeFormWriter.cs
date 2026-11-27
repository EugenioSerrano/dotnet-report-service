using System.Globalization;
using System.IO.Compression;
using System.Text;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using ReportService.Pdf.Verification.PageCodes;

namespace ReportService.Pdf.Assembly.PageCodes;

/// <summary>
/// Writes one page's code as one form object whose bounding box is the declared
/// area and whose content is exactly the generator's bytes, compressed with Flate,
/// and draws it once, inside the area, in the content stream the numbering pass
/// just added to the page. Nothing the generator returns is rewritten: the form's
/// stream decodes to the generator's bytes, which is what the verification compares.
/// </summary>
public static class PageCodeFormWriter
{
    /// <summary>The resource name the code's form is registered under on a page.</summary>
    public const string ResourceName = "RsPageCode";

    /// <summary>The size of a form as stored: its compressed stream plus its dictionary.</summary>
    /// <param name="content">The generator's content.</param>
    /// <param name="area">The area's size.</param>
    /// <returns>The stored size in bytes.</returns>
    public static int StoredSize(byte[] content, PageCodeArea area)
    {
        ArgumentNullException.ThrowIfNull(content);
        var compressed = Compress(content);
        return compressed.Length + Encoding.ASCII.GetByteCount(DictionaryText(area, compressed.Length));
    }

    /// <summary>Adds the form to the page and draws it inside the area.</summary>
    /// <param name="document">The document the page belongs to.</param>
    /// <param name="page">The page, after the numbering pass drew on it.</param>
    /// <param name="content">The generator's content, already checked.</param>
    /// <param name="area">The area, in points from the page's top-left corner.</param>
    /// <exception cref="AssemblyInputException">The page cannot carry the code as the architecture requires.</exception>
    public static void Draw(PdfDocument document, PdfPage page, byte[] content, PageCodeText.Region area)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(area);

        var compressed = Compress(content);
        var form = new PdfDictionary(document);
        form.Elements.SetName("/Type", "/XObject");
        form.Elements.SetName("/Subtype", "/Form");
        form.Elements["/BBox"] = new PdfArray(
            document,
            new PdfReal(0),
            new PdfReal(0),
            new PdfReal(Round(area.WidthPt)),
            new PdfReal(Round(area.HeightPt)));
        form.CreateStream(compressed);
        form.Elements.SetName("/Filter", "/FlateDecode");
        document.Internals.AddObject(form);

        var resources = page.Elements.GetDictionary("/Resources")
            ?? throw new AssemblyInputException("a stamped page has no resources");
        var objects = resources.Elements.GetDictionary("/XObject");
        if (objects is null)
        {
            objects = new PdfDictionary(document);
            resources.Elements["/XObject"] = objects;
        }

        if (objects.Elements.ContainsKey("/" + ResourceName))
        {
            throw new AssemblyInputException("a page already carries an object under the page code's resource name");
        }

        objects.Elements["/" + ResourceName] = form.Reference;

        if (page.Contents.Elements.Count == 0
            || page.Contents.Elements.GetDictionary(page.Contents.Elements.Count - 1) is not { } last
            || last.Stream is null
            || last.Elements.ContainsKey("/Filter"))
        {
            throw new AssemblyInputException("the numbering pass left no plain content stream to draw the page code in");
        }

        var left = area.LeftPt;
        var bottom = page.Height.Point - area.TopPt - area.HeightPt;
        var drawing = Encoding.ASCII.GetBytes(string.Create(
            CultureInfo.InvariantCulture,
            $"\nq 1 0 0 1 {Round(left):0.####} {Round(bottom):0.####} cm /{ResourceName} Do Q\n"));
        last.Stream.Value = [.. last.Stream.Value, .. drawing];
    }

    private static double Round(double value) => Math.Round(value, 4, MidpointRounding.AwayFromZero);

    private static byte[] Compress(byte[] content)
    {
        using var buffer = new MemoryStream();
        using (var zlib = new ZLibStream(buffer, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(content);
        }

        return buffer.ToArray();
    }

    private static string DictionaryText(PageCodeArea area, int length) => string.Create(
        CultureInfo.InvariantCulture,
        $"<</Type/XObject/Subtype/Form/BBox[0 0 {Round(area.WidthPt):0.####} {Round(area.HeightPt):0.####}]/Filter/FlateDecode/Length {length}>>");
}
