using ReportService.Templates;

namespace ReportService.Pdf.Verification.Tests;

/// <summary>
/// The placement convention: the margins a package declares, converted to points,
/// the band each word falls in, the content text a page contributes to the
/// alignment, and the line grouping the diagnostics and the reader's regression case
/// use - never a verdict.
/// </summary>
public sealed class PlacementConventionTests
{
    [Fact]
    public void TheDeclaredMarginsAreConvertedToPoints()
    {
        var margins = PlacementConvention.FromMillimetres(new PageMarginsMm(15, 12, 18, 12));

        Assert.Equal(42.52, margins.Top, 2);
        Assert.Equal(34.02, margins.Right, 2);
        Assert.Equal(51.02, margins.Bottom, 2);
        Assert.Equal(34.02, margins.Left, 2);
    }

    [Fact]
    public void AWordAboveTheTopMarginIsInTheHeader()
    {
        Assert.Equal(ReadingBand.Header, PlacementConvention.BandOf(10, 20, 842, new PageMargins(40, 30, 50, 30)));
    }

    [Fact]
    public void AWordBelowTheBottomMarginIsInTheFooter()
    {
        Assert.Equal(ReadingBand.Footer, PlacementConvention.BandOf(800, 810, 842, new PageMargins(40, 30, 50, 30)));
    }

    [Fact]
    public void AWordBetweenTheMarginsIsContent()
    {
        Assert.Equal(ReadingBand.Content, PlacementConvention.BandOf(300, 310, 842, new PageMargins(40, 30, 50, 30)));
    }

    [Fact]
    public void TheContentTextHoldsOnlyTheContentBandAndIsNormalised()
    {
        var page = new PrintedPage(1, 595, 842,
        [
            new PrintedWord("Title", 10, 10, 40, 20, 0, ReadingBand.Header),
            new PrintedWord("Alpha", 10, 100, 40, 110, 1, ReadingBand.Content),
            new PrintedWord("Beta", 50, 100, 80, 110, 2, ReadingBand.Content),
            new PrintedWord("foot", 10, 820, 40, 830, 3, ReadingBand.Footer),
        ]);

        Assert.Equal("AlphaBeta", PlacementConvention.ContentText(page));
    }

    [Fact]
    public void WordsWithinTwoPointsAreReadOnOneLine()
    {
        var page = new PrintedPage(1, 595, 842,
        [
            new PrintedWord("a", 10, 100, 20, 108, 0, ReadingBand.Content),
            new PrintedWord("b", 30, 101, 40, 109, 1, ReadingBand.Content),
            new PrintedWord("c", 10, 120, 20, 128, 2, ReadingBand.Content),
        ]);

        var lines = PlacementConvention.Lines(page.Words);

        Assert.Equal(2, lines.Count);
        Assert.Equal(["a", "b"], lines[0].Select(word => word.Text));
        Assert.Equal(["c"], lines[1].Select(word => word.Text));
    }

    [Fact]
    public void AWordInTheLastBandIsStillRead()
    {
        var page = new PrintedPage(1, 595, 842,
        [
            new PrintedWord("a", 10, 100, 20, 108, 0, ReadingBand.Content),
            new PrintedWord("b", 30, 140, 40, 148, 1, ReadingBand.Content),
        ]);

        Assert.Equal(2, PlacementConvention.Lines(page.Words).Count);
    }
}
