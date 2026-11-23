namespace ReportService.PdfOracle.Tests;

/// <summary>
/// The rule of the presented values: the printed text of a field a package formats must be the
/// text its declaration's culture and zone produce from the snapshot, on every row and on the
/// page the acceptance instant prints. The rule is what a correct composition cannot answer for
/// itself: both sides of the composition can agree and still be in the wrong culture, so only a
/// reading computed from the snapshot refuses it.
/// </summary>
public sealed class PresentedValueTests
{
    [Fact]
    public void AWellPrintedPresentedDocumentPassesEveryRuleThatApplies()
    {
        var verdict = Judge(PresentedDocument.Extraction());

        Assert.True(verdict.Passed, Describe(verdict));
        Assert.Equal("pass", verdict.Rules["TI-12"]);
        Assert.Equal("pass", verdict.Rules["TI-3"]);
        Assert.Equal("pass", verdict.Rules["TI-4"]);
    }

    [Fact]
    public void ADocumentWithNoPresentedValueReportsTheRuleAsNotApplicable()
    {
        var verdict = IntegrityRules.Judge(
            SyntheticDocument.Extraction(),
            SyntheticDocument.Fixture(),
            SyntheticDocument.Expectation(),
            "plain");

        Assert.Equal("n/a", verdict.Rules["TI-12"]);
        Assert.False(string.IsNullOrWhiteSpace(verdict.NotApplicable["TI-12"]));
    }

    [Fact]
    public void AnAmountInAnotherCultureIsPresentedValueWrongAndNotAFieldOfTheRow()
    {
        var words = With(PresentedDocument.Extraction().Words.ToList(), "100,00", "100.00");

        var verdict = Judge(PresentedDocument.Extraction().WithWords(words));

        var failure = Assert.Single(verdict.Failures);
        Assert.Equal("TI-12", failure.Rule);
        Assert.Equal("presented-value-wrong", failure.Class);
        Assert.Equal(1, failure.Row);
        Assert.Equal("amount", failure.Field);
        Assert.Equal("pass", verdict.Rules["TI-3"]);
        Assert.Equal("pass", verdict.Rules["TI-4"]);
    }

    [Fact]
    public void ADateShiftedByOneDayIsPresentedValueWrong()
    {
        var words = With(PresentedDocument.Extraction().Words.ToList(), "03/01/2026", "04/01/2026");

        var verdict = Judge(PresentedDocument.Extraction().WithWords(words));

        var failure = Assert.Single(verdict.Failures);
        Assert.Equal("TI-12", failure.Rule);
        Assert.Equal("presented-value-wrong", failure.Class);
        Assert.Equal(3, failure.Row);
        Assert.Equal("date", failure.Field);
    }

    [Fact]
    public void AnAcceptanceInstantAnHourLaterIsPresentedValueWrong()
    {
        var words = With(PresentedDocument.Extraction().Words.ToList(), "21:00", "22:00");

        var verdict = Judge(PresentedDocument.Extraction().WithWords(words));

        var failure = Assert.Single(verdict.Failures);
        Assert.Equal("TI-12", failure.Rule);
        Assert.Equal("presented-value-wrong", failure.Class);
        Assert.Equal("render.clock", failure.Field);
    }

    [Fact]
    public void ThePresentedControlsAreReportedWithTheFailureTheyPlant()
    {
        var outcomes = ExtractionControls.Run(PresentedDocument.Extraction(), PresentedDocument.Fixture(), PresentedDocument.Expectation());

        Assert.Equal(10, outcomes.Count);
        foreach (var outcome in outcomes)
        {
            Assert.True(
                outcome.Behaved,
                $"{outcome.Id} planted {outcome.Expected} and the judge reported: {string.Join("; ", outcome.Observed)}");
        }

        Assert.Equal("presented-value-wrong", Assert.Single(outcomes, outcome => outcome.Id == "E15").Expected);
        Assert.Equal("presented-value-wrong", Assert.Single(outcomes, outcome => outcome.Id == "E16").Expected);
    }

    private static Verdict Judge(PdfExtraction extraction) =>
        IntegrityRules.Judge(extraction, PresentedDocument.Fixture(), PresentedDocument.Expectation(), "presented");

    private static List<PdfWord> With(List<PdfWord> words, string from, string to)
    {
        var index = words.FindIndex(word => string.Equals(word.Text, from, StringComparison.Ordinal));
        Assert.True(index >= 0, $"The document prints no '{from}'.");
        words[index] = words[index] with { Text = to };
        return words;
    }

    private static string Describe(Verdict verdict) =>
        string.Join("; ", verdict.Failures.Select(failure => $"{failure.Rule} {failure.Class}"));
}
