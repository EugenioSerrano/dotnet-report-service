namespace ReportService.PdfOracle.Tests;

/// <summary>
/// The judge on a document built as a page frame: the page's geometry, its content
/// frame, the repeated column headers and the page elements in the bands, each way
/// they can be wrong reported with the class the rendering suite maps it to, and the
/// extraction controls of the frame behaving on it.
/// </summary>
public sealed class PageFrameJudgeTests
{
    [Fact]
    public void AWellPrintedPageFramePassesEveryRuleThatApplies()
    {
        var verdict = Judge(PageFrameDocument.Extraction());

        Assert.True(verdict.Passed, JudgeTests.Describe(verdict));
        foreach (var rule in new[] { "TI-1", "TI-2", "TI-3", "TI-4", "TI-6", "TI-7", "TI-8", "TI-9", "TI-10", "TI-11" })
        {
            Assert.Equal("pass", verdict.Rules[rule]);
        }

        Assert.Equal("n/a", verdict.Rules["TI-5"]);
    }

    [Fact]
    public void TheExtractionControlsOfThePageFrameAreAllReported()
    {
        var outcomes = ExtractionControls.Run(PageFrameDocument.Extraction(), PageFrameDocument.Fixture(), PageFrameDocument.Expectation());

        foreach (var id in new[] { "E9", "E10", "E11", "E12", "E13", "E14" })
        {
            var outcome = Assert.Single(outcomes, candidate => candidate.Id == id);
            Assert.True(outcome.Behaved, $"{id} planted {outcome.Expected} and the judge reported: {string.Join("; ", outcome.Observed)}");
        }
    }

    [Fact]
    public void APageOfAnotherSizeIsAPageGeometryFailure()
    {
        var sizes = PageFrameDocument.Extraction().PageSizes.ToList();
        sizes[1] = (sizes[1].Width + 2.0, sizes[1].Height);

        var verdict = Judge(PageFrameDocument.Extraction().WithPageSizes(sizes));

        Assert.Contains(verdict.Failures, failure => failure.Rule == "TI-9" && failure.Class == "page-geometry-wrong" && failure.Page == 2);
        Assert.Equal("fail", verdict.Rules["TI-9"]);
    }

    [Fact]
    public void ARotatedPageIsAPageGeometryFailure()
    {
        var extraction = PageFrameDocument.Extraction();

        var verdict = Judge(new PdfExtraction(
            extraction.Pages,
            extraction.ReportedPages,
            extraction.PageSizes,
            extraction.Words,
            [0, 90, 0]));

        Assert.Contains(verdict.Failures, failure => failure.Rule == "TI-9" && failure.Class == "page-geometry-wrong" && failure.Page == 2);
    }

    [Fact]
    public void AContentWordPushedIntoTheMarginIsAFrameFailure()
    {
        var words = PageFrameDocument.Extraction().Words.ToList();
        var word = words.First(candidate => candidate.Page == 1 && candidate.Text == "Pilot");
        words[words.IndexOf(word)] = word with { XMin = word.XMin - 10, XMax = word.XMax - 10 };

        var verdict = Judge(PageFrameDocument.Extraction().WithWords(words));

        Assert.Contains(verdict.Failures, failure => failure.Rule == "TI-10" && failure.Class == "frame-wrong" && failure.Page == 1);
    }

    [Fact]
    public void APageThatEndsWellAboveTheMarginIsAFrameFailure()
    {
        var words = PageFrameDocument.Extraction().Words
            .Where(candidate => candidate.Page != 1 || candidate.YCentre < 200)
            .ToList();

        var verdict = Judge(PageFrameDocument.Extraction().WithWords(words));

        Assert.Contains(verdict.Failures, failure => failure.Rule == "TI-10" && failure.Class == "frame-wrong" && failure.Page == 1);
    }

    [Fact]
    public void AWrongPositionIsANumberingFailure()
    {
        var words = PageFrameDocument.Extraction().Words.ToList();
        var word = words.First(candidate => candidate.Page == 2 && candidate.Text == "2" && candidate.YCentre < 40);
        words[words.IndexOf(word)] = word with { Text = "3" };

        var verdict = Judge(PageFrameDocument.Extraction().WithWords(words));

        Assert.Contains(verdict.Failures, failure => failure.Rule == "TI-8" && failure.Class == "numbering-wrong" && failure.Page == 2);
    }

    [Fact]
    public void AWrongPageCountIsANumberingFailure()
    {
        var words = PageFrameDocument.Extraction().Words.ToList();
        var word = words.Last(candidate => candidate.Text == "3" && candidate.YCentre < 40);
        words[words.IndexOf(word)] = word with { Text = "4" };

        var verdict = Judge(PageFrameDocument.Extraction().WithWords(words));

        Assert.Contains(verdict.Failures, failure => failure.Rule == "TI-8" && failure.Class == "numbering-wrong");
    }

    [Fact]
    public void AMissingFooterIsAFooterFailure()
    {
        var footer = PageFrameDocument.Extraction().Words.Where(candidate => candidate.Page == 3 && candidate.YCentre > 790).ToList();
        var words = JudgeTests.Without(PageFrameDocument.Extraction().Words, candidate => footer.Contains(candidate));

        var verdict = Judge(PageFrameDocument.Extraction().WithWords(words));

        Assert.Contains(verdict.Failures, failure => failure.Rule == "TI-8" && failure.Class == "footer-wrong" && failure.Page == 3);
    }

    [Fact]
    public void AContinuedPageThatDoesNotBeginWithTheColumnHeadersIsAColumnHeaderFailure()
    {
        var captions = PageFrameDocument.HeadRow(3, 45);
        var words = JudgeTests.Without(PageFrameDocument.Extraction().Words, candidate => captions.Contains(candidate));

        var verdict = Judge(PageFrameDocument.Extraction().WithWords(words));

        Assert.Contains(verdict.Failures, failure => failure.Rule == "TI-11" && failure.Class == "column-header-missing" && failure.Page == 3);
    }

    [Fact]
    public void ADocumentWithoutAPageFrameIsNotJudgedOnOne()
    {
        var verdict = JudgeTests.Judge(SyntheticDocument.Extraction());

        Assert.Equal("n/a", verdict.Rules["TI-9"]);
        Assert.Equal("n/a", verdict.Rules["TI-10"]);
        Assert.Equal("n/a", verdict.Rules["TI-11"]);
        Assert.False(string.IsNullOrWhiteSpace(verdict.NotApplicable["TI-9"]));
    }

    [Fact]
    public void ANumberingWordingThatNamesNoCounterIsRefused()
    {
        var json = PageFrameDocument.ExpectationJson.Replace("Hoja {X} de {Y}", "Hoja de siempre", StringComparison.Ordinal);

        Assert.Throws<InvalidDataException>(() => ExpectationDocument.Parse(json));
    }

    [Theory]
    [InlineData("  Page    1 rot:   0\n  Page    2 rot:  90\n  Page    3 rot:   0\n", new[] { 0, 90, 0 })]
    [InlineData("Pages: 2\n", new int[0])]
    public void ThePageRotationsAreReadInPageOrder(string output, int[] expected)
    {
        Assert.Equal(expected, PopplerText.Rotations(output));
    }

    private static Verdict Judge(PdfExtraction extraction) =>
        IntegrityRules.Judge(extraction, PageFrameDocument.Fixture(), PageFrameDocument.Expectation(), "page-frame.pdf");
}
