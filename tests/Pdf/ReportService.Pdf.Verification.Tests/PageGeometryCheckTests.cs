namespace ReportService.Pdf.Verification.Tests;

/// <summary>
/// The page-geometry check: every printed page must be the page the template
/// declared, within one point, whichever page of the document is the wrong one.
/// The check reads the page's displayed size - its media box with the rotation
/// applied - so a portrait declaration printed landscape is a wrong page even when
/// the box alone agrees.
/// </summary>
public sealed class PageGeometryCheckTests
{
    private static PageFrame A4Portrait => PageFrame.FromMillimetres(210, 297, 15, 12, 18, 12);

    private static PageFrame A4Landscape => PageFrame.FromMillimetres(297, 210, 20, 15, 20, 15);

    private static PageFrame LetterPortrait => PageFrame.FromMillimetres(215.9, 279.4, 25.4, 19.05, 25.4, 19.05);

    [Fact]
    public void EveryPageOfTheDeclaredSizePasses()
    {
        var outcome = new PageGeometryCheck().Check(Reading((595.276, 841.89), (595.276, 841.89)), A4Portrait);

        Assert.True(outcome.Passed);
        Assert.Null(outcome.Page);
    }

    [Fact]
    public void APageWithinOnePointPasses()
    {
        var outcome = new PageGeometryCheck().Check(Reading((594.5, 842.5), (595.276, 841.89)), A4Portrait);

        Assert.True(outcome.Passed);
    }

    [Fact]
    public void APageJustOverOnePointWideFails()
    {
        var outcome = new PageGeometryCheck().Check(Reading((596.28, 841.89)), A4Portrait);

        Assert.False(outcome.Passed);
        Assert.Equal(1, outcome.Page);
        Assert.Equal(596.28, outcome.MeasuredWidthPt!.Value, 2);
        Assert.Equal(595.276, outcome.ExpectedWidthPt!.Value, 2);
    }

    [Fact]
    public void APageJustOverOnePointHighFails()
    {
        var outcome = new PageGeometryCheck().Check(Reading((595.276, 842.9)), A4Portrait);

        Assert.False(outcome.Passed);
        Assert.Equal(1, outcome.Page);
        Assert.Equal(842.9, outcome.MeasuredHeightPt!.Value, 2);
        Assert.Equal(841.89, outcome.ExpectedHeightPt!.Value, 2);
    }

    [Fact]
    public void APortraitDeclarationPrintedLandscapeFails()
    {
        var outcome = new PageGeometryCheck().Check(Reading((841.89, 595.276)), A4Portrait);

        Assert.False(outcome.Passed);
        Assert.Equal(1, outcome.Page);
    }

    [Fact]
    public void ALandscapeDeclarationPrintedLandscapePasses()
    {
        var outcome = new PageGeometryCheck().Check(Reading((841.89, 595.276)), A4Landscape);

        Assert.True(outcome.Passed);
    }

    [Fact]
    public void ThePageThatIsWrongIsThePageReported()
    {
        var reading = Reading((595.276, 841.89), (595.276, 841.89), (612, 792));

        var outcome = new PageGeometryCheck().Check(reading, A4Portrait);

        Assert.False(outcome.Passed);
        Assert.Equal(3, outcome.Page);
    }

    [Fact]
    public void TheFirstWrongPageIsTheOneReported()
    {
        var reading = Reading((612, 792), (612, 792), (595.276, 841.89));

        var outcome = new PageGeometryCheck().Check(reading, A4Portrait);

        Assert.False(outcome.Passed);
        Assert.Equal(1, outcome.Page);
    }

    [Fact]
    public void OnePageOfTheDeclaredSizePasses()
    {
        var outcome = new PageGeometryCheck().Check(Reading((595.276, 841.89)), A4Portrait);

        Assert.True(outcome.Passed);
    }

    [Fact]
    public void ZeroPagesPassHereBecauseThePageCheckRefusesThemFirst()
    {
        var outcome = new PageGeometryCheck().Check(new PdfReading([]), A4Portrait);

        Assert.True(outcome.Passed);
    }

    [Fact]
    public void TheLargePaperOfTheRenderingSuitePassesAtItsOwnSize()
    {
        var outcome = new PageGeometryCheck().Check(Reading((594.96, 841.92)), A4Portrait);

        Assert.True(outcome.Passed);
    }

    [Fact]
    public void TheLetterDeclarationRefusesAnA4Page()
    {
        var outcome = new PageGeometryCheck().Check(Reading((594.96, 841.92)), LetterPortrait);

        Assert.False(outcome.Passed);
        Assert.Equal(1, outcome.Page);
    }

    [Fact]
    public void AFaultyCheckWouldBeCaughtByAPageThatIsNowhereNear()
    {
        var outcome = new PageGeometryCheck().Check(Reading((841.89, 841.89)), A4Portrait);

        Assert.False(outcome.Passed);
        Assert.Equal(841.89, outcome.MeasuredWidthPt!.Value, 2);
    }

    private static PdfReading Reading(params (double Width, double Height)[] pages)
    {
        var reading = new List<PrintedPage>(pages.Length);
        for (var index = 0; index < pages.Length; index++)
        {
            reading.Add(new PrintedPage(index + 1, pages[index].Width, pages[index].Height, []));
        }

        return new PdfReading(reading);
    }
}
