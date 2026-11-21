using ReportService.Templates;

namespace ReportService.Pdf.Verification.Tests;

/// <summary>
/// Reading a printed document: the pages, the words in painting order, the frame
/// each box is converted to, and the rule that a document the reader cannot read is
/// a failure of the document - never an empty reading that would pass.
/// </summary>
public sealed class PdfWordReaderTests
{
    /// <summary>The table document the rendering suite keeps as its reader regression case.</summary>
    private const string TableDocument = "s2-table-s2-T300u-split-t1.00-f0.90-on-r1.pdf";

    /// <summary>The SHA-256 of that document, as the suite records it.</summary>
    private const string TableDocumentSha256 = "97c9f8491d3fc2bb9efd32aaace1bc3c686f2be9d58cf39edc013526f5627dcc";

    private static string Path => System.IO.Path.Combine(AppContext.BaseDirectory, "PdfCases", TableDocument);

    private static PageMargins Margins => PlacementConvention.FromMillimetres(new PageMarginsMm(15, 12, 18, 12));

    [Fact]
    public void TheDocumentIsTheOneTheSuiteKeeps()
    {
        Assert.Equal(TableDocumentSha256, Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(Path))));
    }

    [Fact]
    public void EveryPageIsRead()
    {
        var reading = PdfWordReader.Read(Path, Margins);

        Assert.Equal(16, reading.Pages.Count);
        Assert.Equal(Enumerable.Range(1, 16), reading.Pages.Select(page => page.Number));
        Assert.All(reading.Pages, page => Assert.NotEmpty(page.Words));
    }

    [Fact]
    public void TheReadingIsTheSameEveryTime()
    {
        static string Describe(PdfReading reading) => string.Join(
            "\n",
            reading.Pages.Select(page => page.Number + "|" + page.Width.ToString("0.###") + "|" + page.Height.ToString("0.###") + "|"
                + string.Join(";", page.Words.Select(word => word.Text + "," + word.Left.ToString("0.###")))));

        var first = Describe(PdfWordReader.Read(Path, Margins));
        var second = Describe(PdfWordReader.Read(Path, Margins));
        var third = Describe(PdfWordReader.Read(Path, Margins));

        Assert.Equal(first, second);
        Assert.Equal(second, third);
    }

    [Fact]
    public void TheWordsOfAPageAreInPaintingOrder()
    {
        var reading = PdfWordReader.Read(Path, Margins);

        foreach (var page in reading.Pages)
        {
            var indices = page.Words.Select(word => word.ContentIndex).ToList();
            Assert.Equal(indices.OrderBy(index => index), indices);
        }
    }

    [Fact]
    public void TheBoxIsConvertedToTheFrameTheChecksCompareIn()
    {
        var reading = PdfWordReader.Read(Path, Margins);
        var page = reading.Pages[0];
        var word = page.Words[0];

        Assert.True(word.Top < page.Height);
        Assert.True(word.Bottom <= page.Height);
        Assert.True(word.Right > word.Left);
        Assert.True(word.Bottom > word.Top);
    }

    [Fact]
    public void ThePageTitleIsReadInTheHeaderBand()
    {
        var reading = PdfWordReader.Read(Path, Margins);

        Assert.Contains(reading.Pages[0].Words, word => word.Band == ReadingBand.Header);
        Assert.Contains(reading.Pages[0].Words, word => word.Band == ReadingBand.Content);
    }

    [Fact]
    public void ADocumentThatIsNotThereCannotBeRead()
    {
        Assert.Throws<PdfUnreadableException>(() => PdfWordReader.Read(
            System.IO.Path.Combine(AppContext.BaseDirectory, "PdfCases", "no-such-document.pdf"),
            Margins));
    }

    [Fact]
    public void ADocumentThatIsNotADocumentCannotBeRead()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "not-a-document-" + Guid.NewGuid().ToString("N") + ".pdf");
        File.WriteAllText(path, "this is not a printed document");
        try
        {
            Assert.Throws<PdfUnreadableException>(() => PdfWordReader.Read(path, Margins));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
