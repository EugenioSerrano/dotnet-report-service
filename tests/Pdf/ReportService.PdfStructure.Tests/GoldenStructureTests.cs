using ReportService.Pdf.Verification;
using ReportService.Templates;

namespace ReportService.PdfStructure.Tests;

/// <summary>
/// The hermetic PDF structure verification (ADR-039 §3.5.8): each committed golden
/// is read in-process with the product's own reader (PdfPig, via PdfWordReader) and
/// held to its structure — every page present and printing words, the global "Page X
/// of Y" numbering continuous and ending at the true page count, the words of each
/// page in painting order, and every source row printed once and in order. No
/// browser, no engine, no container: the goldens are committed binaries.
/// </summary>
public sealed class GoldenStructureTests
{
    /// <summary>Every committed golden, one case each.</summary>
    public static TheoryData<string> Goldens => GoldenCatalog.Goldens;

    [Fact]
    public void TheCatalogHoldsAtLeastOneGolden()
    {
        Assert.NotEmpty(GoldenCatalog.Names);
    }

    [Theory]
    [MemberData(nameof(Goldens))]
    public void EveryPageIsPresentAndPrintsWords(string golden)
    {
        var reading = GoldenCatalog.Read(golden);

        Assert.True(reading.Pages.Count >= 1, $"{golden} printed no page.");
        var pages = new PageCheck().Check(reading);
        Assert.True(pages.Passed, $"{golden}: {pages.Reason}");
        Assert.All(reading.Pages, page => Assert.NotEmpty(page.Words));
    }

    [Theory]
    [MemberData(nameof(Goldens))]
    public void TheGlobalPageNumberingIsContinuousAndEndsAtTheTrueCount(string golden)
    {
        var reading = GoldenCatalog.Read(golden);
        var total = reading.Pages.Count;

        // The pages are read in a total, continuous 1..N order by construction.
        Assert.Equal(Enumerable.Range(1, total), reading.Pages.Select(page => page.Number));

        // Where the template prints an extractable "Page X of Y" label (a running header or
        // footer, not a CSS @page margin box, which the in-process reader does not surface),
        // every page carries it, each numbered in order and ending at the true page count.
        var labelled = reading.Pages.Where(page => PageLabel(page) is not null).ToList();
        if (labelled.Count == 0)
        {
            return;
        }

        Assert.Equal(reading.Pages.Count, labelled.Count);
        foreach (var page in reading.Pages)
        {
            var label = PageLabel(page)!.Value;
            Assert.Equal(page.Number, label.Page);
            Assert.Equal(total, label.Total);
        }
    }

    [Fact]
    public void AtLeastOneGoldenCarriesTheGlobalPageNumbering()
    {
        var carried = GoldenCatalog.Names
            .Select(GoldenCatalog.Read)
            .Any(reading => reading.Pages.Any(page => PageLabel(page) is not null));

        Assert.True(carried, "No committed golden carries an extractable \"Page X of Y\" label; the global page-numbering check is never exercised.");
    }

    [Theory]
    [MemberData(nameof(Goldens))]
    public void TheWordsOfEachPageAreInPaintingOrder(string golden)
    {
        var reading = GoldenCatalog.Read(golden);

        foreach (var page in reading.Pages)
        {
            var indices = page.Words.Select(word => word.ContentIndex).ToList();
            Assert.Equal(indices.OrderBy(index => index), indices);
        }
    }

    [Theory]
    [MemberData(nameof(Goldens))]
    public void EverySourceRowIsPrintedOnceAndInOrder(string golden)
    {
        var expectation = GoldenCatalog.Expectation(golden);
        var expected = GoldenCatalog.SourceRowKeys(golden);
        Assert.NotEmpty(expected);

        var printed = GoldenCatalog.PrintedRowKeys(GoldenCatalog.Read(golden), expectation);

        Assert.Empty(RowCoverage.Missing(expected, printed));
        Assert.Empty(RowCoverage.Missing(printed, expected));
        Assert.Null(RowCoverage.FirstOutOfOrder(expected, printed));
    }

    [Theory]
    [MemberData(nameof(Goldens))]
    public void TheReadingIsTheSameEveryTime(string golden)
    {
        var first = Describe(GoldenCatalog.Read(golden));
        var second = Describe(GoldenCatalog.Read(golden));

        Assert.Equal(first, second);
    }

    [Theory]
    [MemberData(nameof(Goldens))]
    public void ALostRowIsReportedAsThatSpecificMissingRow(string golden)
    {
        var expectation = GoldenCatalog.Expectation(golden);
        var expected = GoldenCatalog.SourceRowKeys(golden);
        var printed = GoldenCatalog.PrintedRowKeys(GoldenCatalog.Read(golden), expectation).ToList();
        Assert.True(printed.Count >= 1, $"{golden} printed no row key.");

        // A document that lost a middle row: the check names that row and no other.
        var lostIndex = printed.Count / 2;
        var lostKey = printed[lostIndex];
        var damaged = printed.Where((_, index) => index != lostIndex).ToList();

        var missing = RowCoverage.Missing(expected, damaged);
        Assert.Equal([lostKey], missing);
    }

    [Theory]
    [MemberData(nameof(Goldens))]
    public void AReorderedPairIsReportedAsThatSpecificOutOfOrderRow(string golden)
    {
        var expectation = GoldenCatalog.Expectation(golden);
        var expected = GoldenCatalog.SourceRowKeys(golden);
        var printed = GoldenCatalog.PrintedRowKeys(GoldenCatalog.Read(golden), expectation).ToList();
        Assert.True(printed.Count >= 2, $"{golden} printed fewer than two row keys.");

        // A document that swapped the first pair: coverage still holds, and the order
        // check names the row that is now out of place.
        var reordered = printed.ToList();
        (reordered[0], reordered[1]) = (reordered[1], reordered[0]);

        Assert.Empty(RowCoverage.Missing(expected, reordered));
        Assert.Equal(reordered[0], RowCoverage.FirstOutOfOrder(expected, reordered));
    }

    [Fact]
    public void AGoldenTheReaderCannotReadFailsRatherThanPassing()
    {
        var path = Path.Combine(Path.GetTempPath(), "not-a-golden-" + Guid.NewGuid().ToString("N") + ".pdf");
        File.WriteAllText(path, "this is not a printed document");
        try
        {
            Assert.Throws<PdfUnreadableException>(() => PdfWordReader.Read(path, PageMargins.None));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static (int Page, int Total)? PageLabel(PrintedPage page)
    {
        var words = page.Words;
        for (var index = 0; index + 3 < words.Count; index++)
        {
            if (string.Equals(words[index].Text, "Page", StringComparison.Ordinal)
                && int.TryParse(words[index + 1].Text, out var number)
                && string.Equals(words[index + 2].Text, "of", StringComparison.Ordinal)
                && int.TryParse(words[index + 3].Text, out var total))
            {
                return (number, total);
            }
        }

        return null;
    }

    private static string Describe(PdfReading reading) => string.Join(
        "\n",
        reading.Pages.Select(page => page.Number + "|" + page.Width.ToString("0.###") + "|" + page.Height.ToString("0.###") + "|"
            + string.Join(";", page.Words.Select(word => word.Text + "," + word.Left.ToString("0.###") + "," + word.ContentIndex.ToString()))));
}
