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
    public void TheBandsOfALandscapePageComeFromItsOwnHeight()
    {
        // A4 landscape is 210 mm high: the top margin of 20 mm ends at 56.7 pt and
        // the bottom one begins at 538.6 pt.
        var frame = PageFrame.FromMillimetres(297, 210, 20, 15, 20, 15);
        var margins = frame.Margins;

        Assert.Equal(ReadingBand.Header, PlacementConvention.BandOf(20, 30, frame.HeightPt, margins));
        Assert.Equal(ReadingBand.Content, PlacementConvention.BandOf(300, 310, frame.HeightPt, margins));
        Assert.Equal(ReadingBand.Footer, PlacementConvention.BandOf(550, 560, frame.HeightPt, margins));
    }

    [Fact]
    public void TheBandsOfALetterPageComeFromItsOwnHeight()
    {
        // Letter portrait is 279.4 mm high with 25.4 mm margins.
        var frame = PageFrame.FromMillimetres(215.9, 279.4, 25.4, 19.05, 25.4, 19.05);
        var margins = frame.Margins;

        Assert.Equal(ReadingBand.Header, PlacementConvention.BandOf(30, 40, frame.HeightPt, margins));
        Assert.Equal(ReadingBand.Content, PlacementConvention.BandOf(400, 410, frame.HeightPt, margins));
        Assert.Equal(ReadingBand.Footer, PlacementConvention.BandOf(760, 770, frame.HeightPt, margins));
    }

    [Fact]
    public void AMarginBoxWordSitsInItsOwnBand()
    {
        var frame = PageFrame.FromMillimetres(297, 210, 20, 15, 20, 15);
        var page = new PrintedPage(1, frame.WidthPt, frame.HeightPt,
        [
            new PrintedWord("Hoja", 30, 30, 60, 38, 0, ReadingBand.Header),
            new PrintedWord("cuerpo", 45, 300, 90, 308, 1, ReadingBand.Content),
            new PrintedWord("interno", 30, 560, 70, 568, 2, ReadingBand.Footer),
        ]);

        Assert.Equal("cuerpo", PlacementConvention.ContentText(page));
    }

    [Fact]
    public void ASideMarginBoxWordIsContentAndFailsTheAlignment()
    {
        // A side margin box prints in the content band by its vertical centre: its
        // words are not composed units, so the alignment refuses the document.
        var frame = PageFrame.FromMillimetres(210, 297, 15, 12, 18, 12);
        var page = new PrintedPage(1, frame.WidthPt, frame.HeightPt,
        [
            new PrintedWord("lateral", 5, 300, 40, 308, 0, ReadingBand.Content),
            new PrintedWord("cuerpo", 45, 300, 90, 308, 1, ReadingBand.Content),
        ]);

        var content = PlacementConvention.ContentText(page);
        var alignment = new ContentOrderAligner().Align([content], Sample.Units("cuerpo"));

        Assert.False(alignment.Passed);
        Assert.Equal(VerificationReasons.TextExtra, alignment.Reason);
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
