using System.Globalization;

namespace ReportService.PdfOracle.Tests;

/// <summary>
/// The caption rule on a page that prints no detail row: a document whose text after
/// the table runs onto a page of its own prints no column there, and that page is not
/// a lost table. Repeating a head row is the page-elements work; a page that prints
/// rows without captions is still lost structure.
/// </summary>
public sealed class CaptionPageTests
{
    private const int PageWidth = 595;
    private const int PageHeight = 842;

    [Fact]
    public void APageThatPrintsNoDetailRowNeedsNoCaptions()
    {
        var fixture = ReportingWithTrail();
        var words = SyntheticDocument.Words().ToList();

        // The trail prints alone on a third page.
        words.Add(new PdfWord(3, "closing", 40, 60, 80, 72));
        words.Add(new PdfWord(3, "note", 82, 60, 110, 72));
        var extraction = new PdfExtraction(3, 3, [(PageWidth, PageHeight), (PageWidth, PageHeight), (PageWidth, PageHeight)], words);

        var verdict = IntegrityRules.Judge(extraction, fixture, SyntheticDocument.Expectation(), "trail.pdf");

        Assert.True(verdict.Passed, Describe(verdict));
        Assert.Equal("pass", verdict.Rules["TI-8"]);
    }

    [Fact]
    public void APageThatPrintsADetailRowWithoutCaptionsIsLostStructure()
    {
        var captions = SyntheticDocument.HeadRow(2, 45);
        var words = SyntheticDocument.Words().Where(word => !captions.Contains(word)).ToList();

        var verdict = IntegrityRules.Judge(
            SyntheticDocument.Extraction().WithWords(words), SyntheticDocument.Fixture(), SyntheticDocument.Expectation(), "two-pages.pdf");

        Assert.Contains(verdict.Failures, failure => failure.Rule == "TI-8" && failure.Class == "structure-lost" && failure.Page == 2);
    }

    private static FixtureSource ReportingWithTrail() =>
        FixtureSource.Parse(
            SyntheticDocument.FixtureJson.Replace("\"freeText\": \"\"", "\"freeText\": \"closing note\"", StringComparison.Ordinal),
            SyntheticDocument.Expectation());

    private static string Describe(Verdict verdict) =>
        string.Join(", ", verdict.Failures.Select(failure => $"{failure.Rule} {failure.Class} {failure.Page?.ToString(CultureInfo.InvariantCulture)} {failure.Detail}"));
}
