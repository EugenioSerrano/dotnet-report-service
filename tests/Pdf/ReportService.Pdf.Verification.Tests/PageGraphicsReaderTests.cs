using System.Globalization;
using System.IO.Compression;
using System.Text;
using ReportService.Pdf.Verification.PageCodes;
using ReportService.Templates;

namespace ReportService.Pdf.Verification.Tests;

/// <summary>
/// The graphics reading the code row needs: a page's resource names, its form
/// objects with their bounding boxes and decoded content, and each named-object
/// drawing with the matrix in force. The document is written by hand here, so the
/// reading is proven against bytes no PDF library chose.
/// </summary>
public sealed class PageGraphicsReaderTests
{
    private static readonly byte[] FormContent = Encoding.ASCII.GetBytes("q\n0 g\n1 2 3 4 re\nf\nQ\n");

    private static PageMargins Margins => PlacementConvention.FromMillimetres(new PageMarginsMm(10, 10, 10, 10));

    [Fact]
    public void AFormIsReadWithItsBoxItsDecodedContentAndItsDrawingMatrix()
    {
        var path = WriteDocument(compress: true);

        var page = Assert.Single(PdfDocumentReader.Read(path, Margins).Pages);
        var graphics = page.Graphics!;

        var form = Assert.Single(graphics.Forms);
        Assert.Equal("Code", form.Name);
        Assert.Equal([0d, 0d, 50d, 60d], form.BoundingBox);
        Assert.Equal(FormContent, form.Content);
        Assert.Equal(["Code", "Img"], graphics.ResourceNames["XObject"]);
        Assert.Equal(["F1"], graphics.ResourceNames["Font"]);
        var drawing = Assert.Single(graphics.Drawings);
        Assert.Equal("Code", drawing.Name);
        Assert.Equal([2d, 0d, 0d, 2d, 60d, 120d], drawing.Matrix);
    }

    [Fact]
    public void AnUncompressedFormReadsTheSame()
    {
        var page = Assert.Single(PdfDocumentReader.Read(WriteDocument(compress: false), Margins).Pages);

        Assert.Equal(FormContent, Assert.Single(page.Graphics!.Forms).Content);
    }

    [Fact]
    public void AFormWithoutAFourNumberBoxHasNoBox()
    {
        var page = Assert.Single(PdfDocumentReader.Read(WriteDocument(compress: false, box: "[0 0 5]"), Margins).Pages);

        Assert.Null(Assert.Single(page.Graphics!.Forms).BoundingBox);
    }

    [Fact]
    public void AnEmptyPageHasNoGraphics()
    {
        Assert.Empty(PageGraphics.Empty.Forms);
        Assert.Empty(PageGraphics.Empty.Drawings);
        Assert.Empty(PageGraphics.Empty.ResourceNames);
    }

    // A one-page document: a form object "Code" drawn at scale 2 after a translation,
    // an image-like stream "Img" that is not a form, a font, and an unbalanced pop
    // that the matrix stack survives.
    private static string WriteDocument(bool compress, string box = "[0 0 50 60]")
    {
        var formBytes = compress ? Compress(FormContent) : FormContent;
        var filter = compress ? "/Filter /FlateDecode " : string.Empty;
        var content = Encoding.ASCII.GetBytes("Q q 1 0 0 1 10 20 cm q 2 0 0 2 50 100 cm /Code Do Q Q\n");
        var objects = new List<byte[]>
        {
            Ascii("<< /Type /Catalog /Pages 2 0 R >>"),
            Ascii("<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            Ascii("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Contents 4 0 R "
                + "/Resources << /Font << /F1 7 0 R >> /XObject << /Code 5 0 R /Img 6 0 R >> >> >>"),
            Stream(string.Empty, content),
            Stream("/Type /XObject /Subtype /Form /BBox " + box + " " + filter, formBytes),
            Stream("/Type /XObject /Subtype /Image /Width 1 /Height 1 /ColorSpace /DeviceGray /BitsPerComponent 8 ", [0]),
            Ascii("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"),
        };

        using var buffer = new MemoryStream();
        Write(buffer, Ascii("%PDF-1.7\n"));
        var offsets = new List<long>();
        for (var index = 0; index < objects.Count; index++)
        {
            offsets.Add(buffer.Position);
            Write(buffer, Ascii($"{index + 1} 0 obj\n"));
            Write(buffer, objects[index]);
            Write(buffer, Ascii("\nendobj\n"));
        }

        var xref = buffer.Position;
        var table = new StringBuilder($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets)
        {
            table.Append(offset.ToString("0000000000", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        }

        table.Append($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        Write(buffer, Ascii(table.ToString()));

        var path = Path.Combine(Path.GetTempPath(), "page-graphics-tests", Guid.NewGuid().ToString("n") + ".pdf");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, buffer.ToArray());
        return path;
    }

    private static byte[] Stream(string dictionary, byte[] data) =>
    [
        .. Ascii($"<< {dictionary}/Length {data.Length} >>\nstream\n"),
        .. data,
        .. Ascii("\nendstream"),
    ];

    private static byte[] Compress(byte[] data)
    {
        using var buffer = new MemoryStream();
        using (var zlib = new ZLibStream(buffer, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(data);
        }

        return buffer.ToArray();
    }

    private static byte[] Ascii(string text) => Encoding.ASCII.GetBytes(text);

    private static void Write(Stream stream, byte[] bytes) => stream.Write(bytes);
}
