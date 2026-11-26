using ReportService.Templates;

namespace ReportService.Pdf.Verification.Tests;

/// <summary>
/// The additive document reading: the same words the per-block reading returns,
/// with each page's media box and its own dictionary exposed for the assembly
/// verification. A document the reader cannot read remains a failure of the
/// document, never an empty reading.
/// </summary>
public sealed class PdfDocumentReaderTests
{
    private const string TableDocument = "s2-table-s2-T300u-split-t1.00-f0.90-on-r1.pdf";

    private static string Path => System.IO.Path.Combine(AppContext.BaseDirectory, "PdfCases", TableDocument);

    private static PageMargins Margins => PlacementConvention.FromMillimetres(new PageMarginsMm(15, 12, 18, 12));

    [Fact]
    public void EveryPageIsReadWithItsBoxAndDictionary()
    {
        var reading = PdfDocumentReader.Read(Path, Margins);

        Assert.Equal(16, reading.Pages.Count);
        Assert.Equal(Enumerable.Range(1, 16), reading.Pages.Select(page => page.Number));
        Assert.All(reading.Pages, page =>
        {
            Assert.NotEmpty(page.Page.Words);
            Assert.True(page.Box.Width > 0);
            Assert.True(page.Box.Height > 0);
            Assert.NotEmpty(page.Dictionary.Entries);
        });
    }

    [Fact]
    public void TheWordsAreThePerBlockReading()
    {
        var ordinary = PdfWordReader.Read(Path, Margins);
        var structured = PdfDocumentReader.Read(Path, Margins);

        for (var index = 0; index < ordinary.Pages.Count; index++)
        {
            Assert.Equal(ordinary.Pages[index].Width, structured.Pages[index].Page.Width);
            Assert.Equal(ordinary.Pages[index].Height, structured.Pages[index].Page.Height);
            Assert.Equal(
                ordinary.Pages[index].Words.Select(word => word.Text),
                structured.Pages[index].Page.Words.Select(word => word.Text));
        }
    }

    [Fact]
    public void ThePageBoxIsTheMediaBoxAndTheDictionaryNamesThePage()
    {
        var reading = PdfDocumentReader.Read(Path, Margins);

        Assert.All(reading.Pages, page =>
        {
            Assert.Equal(page.Page.Width, page.Box.Width, precision: 3);
            Assert.Equal(page.Page.Height, page.Box.Height, precision: 3);
            Assert.Equal("/Page", page.Dictionary.Entries["Type"]);
            Assert.StartsWith("[", page.Dictionary.Entries["MediaBox"], StringComparison.Ordinal);
        });
    }

    [Fact]
    public void TheDictionaryRenderingIsStable()
    {
        static string Describe(PdfDocumentReading reading) => string.Join(
            "\n",
            reading.Pages.Select(page => page.Number + "|" + string.Join(
                ";",
                page.Dictionary.Entries.OrderBy(entry => entry.Key, StringComparer.Ordinal)
                    .Select(entry => entry.Key + "=" + entry.Value))));

        var first = Describe(PdfDocumentReader.Read(Path, Margins));
        var second = Describe(PdfDocumentReader.Read(Path, Margins));
        var third = Describe(PdfDocumentReader.Read(Path, Margins));

        Assert.Equal(first, second);
        Assert.Equal(second, third);
    }

    [Fact]
    public void TheDictionaryCarriesThePageReferencesAndNestedEntries()
    {
        var reading = PdfDocumentReader.Read(Path, Margins);
        var entries = reading.Pages[0].Dictionary.Entries;

        Assert.Contains(entries.Keys, key => key == "Parent");
        Assert.Contains(entries.Keys, key => key == "Resources");
        Assert.Matches("^R:[0-9]+:[0-9]+$", entries["Parent"]);
        Assert.Contains("<<", entries["Resources"], StringComparison.Ordinal);
    }

    [Fact]
    public void ADocumentThatCannotBeReadIsAFailure()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "reader-" + Guid.NewGuid().ToString("n") + ".pdf");
        File.WriteAllText(path, "not a document");
        try
        {
            Assert.Throws<PdfUnreadableException>(() => PdfDocumentReader.Read(path, Margins));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
