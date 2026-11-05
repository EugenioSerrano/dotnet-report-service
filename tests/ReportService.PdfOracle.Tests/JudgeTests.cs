namespace ReportService.PdfOracle.Tests;

/// <summary>
/// The judge against the document the extraction controls act on: a report printed
/// as its package declares passes every rule, and each way a page can be wrong is
/// reported with the failure class the rendering suite maps it to.
/// </summary>
public sealed class JudgeTests
{
    [Fact]
    public void AWellPrintedDocumentPassesEveryRuleThatApplies()
    {
        var verdict = Judge(SyntheticDocument.Extraction());

        Assert.True(verdict.Passed, Describe(verdict));
        Assert.Equal("pass", verdict.Rules["TI-1"]);
        Assert.Equal("pass", verdict.Rules["TI-2"]);
        Assert.Equal("pass", verdict.Rules["TI-3"]);
        Assert.Equal("pass", verdict.Rules["TI-4"]);
        Assert.Equal("pass", verdict.Rules["TI-6"]);
        Assert.Equal("pass", verdict.Rules["TI-7"]);
        Assert.Equal("pass", verdict.Rules["TI-8"]);
    }

    [Fact]
    public void TheGroupRuleIsNotApplicableRatherThanPassingSilently()
    {
        var verdict = Judge(SyntheticDocument.Extraction());

        Assert.Equal("n/a", verdict.Rules["TI-5"]);
        Assert.False(string.IsNullOrWhiteSpace(verdict.NotApplicable["TI-5"]));
    }

    [Fact]
    public void ADocumentHeaderValueThatDoesNotPrintIsDroppedText()
    {
        var words = Without(SyntheticDocument.Extraction().Words, word => word.Text is "Pilot" or "report");

        var verdict = Judge(SyntheticDocument.Extraction().WithWords(words));

        Assert.False(verdict.Passed);
        Assert.Contains(verdict.Failures, failure => failure.Rule == "TI-6" && failure.Class == "text-dropped" && failure.Field == "$.reportTitle");
    }

    [Fact]
    public void ACaptionLineThatDoesNotPrintOnAPageIsLostStructure()
    {
        var captions = SyntheticDocument.HeadRow(2, 45);
        var words = Without(SyntheticDocument.Extraction().Words, word => captions.Contains(word));

        var verdict = Judge(SyntheticDocument.Extraction().WithWords(words));

        Assert.Contains(verdict.Failures, failure => failure.Rule == "TI-8" && failure.Class == "structure-lost" && failure.Page == 2);
    }

    [Fact]
    public void ADescriptionWithTextTheDataDoesNotHoldIsAlteredText()
    {
        var words = SyntheticDocument.Extraction().Words.ToList();
        words.Add(new PdfWord(1, "extra", 210, 105, 240, 117));

        var verdict = Judge(SyntheticDocument.Extraction().WithWords(words));

        Assert.Contains(verdict.Failures, failure => failure.Rule == "TI-3" && failure.Class == "text-altered" && failure.Row == 1 && failure.Field == "description");
    }

    [Fact]
    public void AContinuationMarkThatPrintsIsReportedAndTheRuleThatOwnsItFails()
    {
        var words = SyntheticDocument.Extraction().Words.ToList();
        words.Add(SyntheticDocument.AmountWord(1, 125, "(continues)"));

        var verdict = Judge(SyntheticDocument.Extraction().WithWords(words));

        Assert.Contains(verdict.Failures, failure => failure.Rule == "TI-7" && failure.Class == "text-altered" && failure.Row == 2 && failure.Field == "amount");
        Assert.Equal("fail", verdict.Rules["TI-7"]);
    }

    [Fact]
    public void AContinuationMarkOutsideEveryRowIsLostStructure()
    {
        // A mark the layout prints where no row is: the document header, before the captions.
        var words = SyntheticDocument.Extraction().Words.ToList();
        words.Add(new PdfWord(1, "(continues)", 300, 45, 345, 57));

        var verdict = Judge(SyntheticDocument.Extraction().WithWords(words));

        var failure = Assert.Single(verdict.Failures);
        Assert.Equal("TI-7", failure.Rule);
        Assert.Equal("structure-lost", failure.Class);
        Assert.Equal(1, failure.Page);
        Assert.Equal("fail", verdict.Rules["TI-7"]);
    }

    [Fact]
    public void AReadingOfMorePagesThanTheFileReportsIsLostStructure()
    {
        var extraction = new PdfExtraction(
            SyntheticDocument.Pages + 1,
            SyntheticDocument.Pages,
            [.. SyntheticDocument.Extraction().PageSizes, (SyntheticDocument.PageWidthPt, SyntheticDocument.PageHeightPt)],
            SyntheticDocument.Words());

        var verdict = Judge(extraction);

        Assert.Contains(verdict.Failures, failure => failure.Rule == "TI-1" && failure.Class == "structure-lost" && failure.Page is null);
    }

    [Fact]
    public void APageWhoseTextCoversAnotherPagesSizeIsLostStructure()
    {
        var sizes = SyntheticDocument.Extraction().PageSizes.ToList();
        sizes[1] = (SyntheticDocument.PageWidthPt, SyntheticDocument.PageHeightPt + 20);

        var verdict = Judge(new PdfExtraction(SyntheticDocument.Pages, SyntheticDocument.Pages, sizes, SyntheticDocument.Words()));

        Assert.Contains(verdict.Failures, failure => failure.Rule == "TI-1" && failure.Class == "structure-lost" && failure.Page == 2);
    }

    [Fact]
    public void AReportWithNoDetailRowPasses()
    {
        const string noRows = """
            {
              "contractVersion": "1.0",
              "header": {
                "reportTitle": "Pilot report",
                "customer": { "name": "ACME" },
                "currency": "ARS",
                "totals": { "recordCount": 3 },
                "freeText": ""
              },
              "items": []
            }
            """;
        var words = SyntheticDocument.Extraction().Words
            .Where(word => word.Page == 1 && word.YCentre < 100)
            .ToList();

        var verdict = IntegrityRules.Judge(
            new PdfExtraction(1, 1, [(SyntheticDocument.PageWidthPt, SyntheticDocument.PageHeightPt)], words),
            FixtureSource.Parse(noRows, SyntheticDocument.Expectation()),
            SyntheticDocument.Expectation(),
            "document.pdf");

        Assert.True(verdict.Passed, Describe(verdict));
        Assert.Equal("pass", verdict.Rules["TI-2"]);
    }

    [Fact]
    public void TheVerdictCarriesNoValueOfTheDataNorOfThePrintedText()
    {
        var words = SyntheticDocument.Extraction().Words.ToList();
        words.Add(new PdfWord(1, "extra", 210, 105, 240, 117));

        var json = Judge(SyntheticDocument.Extraction().WithWords(words)).ToJson();

        foreach (var value in new[] { "alpha", "gamma", "extra", "100.00", "Pilot", "ACME" })
        {
            Assert.DoesNotContain(value, json, StringComparison.Ordinal);
        }
    }

    internal static Verdict Judge(PdfExtraction extraction) =>
        IntegrityRules.Judge(extraction, SyntheticDocument.Fixture(), SyntheticDocument.Expectation(), "document.pdf");

    internal static List<PdfWord> Without(IReadOnlyList<PdfWord> words, Func<PdfWord, bool> remove) =>
        [.. words.Where(word => !remove(word))];

    internal static string Describe(Verdict verdict) =>
        string.Join("; ", verdict.Failures.Select(failure =>
            $"{failure.Rule} {failure.Class} row {failure.Row} field {failure.Field} page {failure.Page} {failure.Detail}"));
}
