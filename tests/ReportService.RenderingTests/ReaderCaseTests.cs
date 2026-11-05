using System.Globalization;
using System.Text;
using ReportService.Pdf.Verification;
using ReportService.Templates;

namespace ReportService.RenderingTests;

/// <summary>
/// A printed document kept as a reader case: the table document of the page
/// experiment, whose page five is the one placement the reader is known to group
/// differently. The document is read by the product's reader, and every figure the
/// reading produced - the page count, every word with its band, and the diagnostic
/// grouping of page five - is compared with the reading that was committed beside
/// it. Any change in the reader fails this case, which is the point: the reading it
/// produces is the measure every printed document is held to.
/// </summary>
public sealed class ReaderCaseTests
{
    /// <summary>The document's file, kept beside its reading.</summary>
    private const string Document = "s2-table-s2-T300u-split-t1.00-f0.90-on-r1.pdf";

    /// <summary>The SHA-256 of the document, as the experiment recorded it.</summary>
    private const string DocumentSha256 = "97c9f8491d3fc2bb9efd32aaace1bc3c686f2be9d58cf39edc013526f5627dcc";

    /// <summary>How many pages the document holds.</summary>
    private const int Pages = 16;

    /// <summary>How many words the document prints.</summary>
    private const int Words = 3857;

    /// <summary>How many words page five prints.</summary>
    private const int PageFiveWords = 58;

    /// <summary>The margins the document was printed with.</summary>
    private static PageMargins Margins => PlacementConvention.FromMillimetres(new PageMarginsMm(15, 12, 18, 12));

    private static string Folder => Path.Combine(AppContext.BaseDirectory, "ReaderCases");

    private static string DocumentPath => Path.Combine(Folder, Document);

    [Fact]
    public void TheDocumentIsTheOneTheExperimentRecorded()
    {
        Assert.Equal(DocumentSha256, Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(DocumentPath))));
    }

    [Fact]
    public void TheReadingIsTheOneThatWasCommitted()
    {
        var reading = PdfWordReader.Read(DocumentPath, Margins);
        var expected = File.ReadAllText(Path.Combine(Folder, "T300u.words.tsv"), Encoding.UTF8);

        Assert.Equal(Pages, reading.Pages.Count);
        Assert.Equal(Words, reading.WordCount);
        Assert.Equal(expected, Describe(reading));
    }

    [Fact]
    public void ThreeReadingsOfTheDocumentAreIdentical()
    {
        var first = Describe(PdfWordReader.Read(DocumentPath, Margins));
        var second = Describe(PdfWordReader.Read(DocumentPath, Margins));
        var third = Describe(PdfWordReader.Read(DocumentPath, Margins));

        Assert.Equal(first, second);
        Assert.Equal(second, third);
    }

    [Fact]
    public void TheDiagnosticGroupingOfPageFiveIsTheOneThatWasCommitted()
    {
        var reading = PdfWordReader.Read(DocumentPath, Margins);
        var page = reading.Pages[4];
        var lines = PlacementConvention.Lines(page.Words);

        int LineOf(PrintedWord word)
        {
            for (var index = 0; index < lines.Count; index++)
            {
                foreach (var candidate in lines[index])
                {
                    if (ReferenceEquals(candidate, word))
                    {
                        return index;
                    }
                }
            }

            return -1;
        }

        var fingerprint = new StringBuilder();
        foreach (var word in page.Words)
        {
            fingerprint.Append(LineOf(word).ToString(CultureInfo.InvariantCulture)).Append(' ');
        }

        Assert.Equal(PageFiveWords, page.Words.Count);
        Assert.Equal(File.ReadAllText(Path.Combine(Folder, "T300u.page5-lines.txt"), Encoding.UTF8), fingerprint.ToString());
    }

    /// <summary>The reading as the committed file holds it: page, band and word, one per line.</summary>
    private static string Describe(PdfReading reading)
    {
        var builder = new StringBuilder();
        foreach (var page in reading.Pages)
        {
            foreach (var word in page.Words)
            {
                builder.Append(page.Number.ToString(CultureInfo.InvariantCulture))
                    .Append('\t')
                    .Append(word.Band)
                    .Append('\t')
                    .Append(word.Text)
                    .Append('\n');
            }
        }

        return builder.ToString();
    }
}
