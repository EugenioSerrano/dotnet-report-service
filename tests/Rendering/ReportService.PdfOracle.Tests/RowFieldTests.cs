namespace ReportService.PdfOracle.Tests;

/// <summary>
/// The text of one row against the record it was printed from: the description
/// (TI-3), the date and the amount (TI-4). Each case changes one cell of a
/// document that passes and nothing else, so the rule that owns the cell is the
/// only rule that can fail — and a rule that reports nothing is a rule the
/// rendering suite would trust with a wrong value.
/// </summary>
public sealed class RowFieldTests
{
    [Fact]
    public void ARowWhoseAmountIsWrongIsAlteredText()
    {
        var verdict = JudgeTests.Judge(Replaced("100.00", "190.00"));

        Assert.Equal("fail", verdict.Rules["TI-4"]);
        var failure = Assert.Single(verdict.Failures);
        Assert.Equal("TI-4", failure.Rule);
        Assert.Equal("text-altered", failure.Class);
        Assert.Equal(1, failure.Row);
        Assert.Equal("amount", failure.Field);
        Assert.Equal(1, failure.Page);
    }

    [Fact]
    public void ARowThatPrintsAnotherRowsAmountIsAlteredText()
    {
        // The printed value is a value of the data — of another record: nothing but
        // the record's own amount can make a row right.
        var verdict = JudgeTests.Judge(Replaced("100.00", "200.00"));

        var failure = Assert.Single(verdict.Failures);
        Assert.Equal("TI-4", failure.Rule);
        Assert.Equal("text-altered", failure.Class);
        Assert.Equal(1, failure.Row);
        Assert.Equal("amount", failure.Field);
    }

    [Fact]
    public void ADateThatIsNotTheRecordsDateIsAlteredText()
    {
        var verdict = JudgeTests.Judge(Replaced("2026-01-02", "2026-01-09"));

        var failure = Assert.Single(verdict.Failures);

        // A date is an ordinary declared column: its cell is compared with the record's
        // member through the same text rule every column but the amount goes through.
        Assert.Equal("TI-3", failure.Rule);
        Assert.Equal("text-altered", failure.Class);
        Assert.Equal(2, failure.Row);
        Assert.Equal("date", failure.Field);
    }

    [Fact]
    public void AnAmountThatPrintsNowhereIsDroppedText()
    {
        var verdict = JudgeTests.Judge(Without("300.00"));

        Assert.Equal("fail", verdict.Rules["TI-4"]);
        var failure = Assert.Single(verdict.Failures);
        Assert.Equal("TI-4", failure.Rule);
        Assert.Equal("text-dropped", failure.Class);
        Assert.Equal(3, failure.Row);
        Assert.Equal("amount", failure.Field);
    }

    [Fact]
    public void AWrongAmountOnTheSecondPageIsReportedOnThePageItPrintedOn()
    {
        var verdict = JudgeTests.Judge(Replaced("300.00", "300.10"));

        var failure = Assert.Single(verdict.Failures);
        Assert.Equal("TI-4", failure.Rule);
        Assert.Equal(3, failure.Row);
        Assert.Equal(2, failure.Page);
        Assert.Equal("fail", verdict.Rules["TI-4"]);
    }

    [Fact]
    public void ACorrectAmountMovedToAnotherPageIsReportedOnItsPlacement()
    {
        // Row 2 continues over the page break. Its amount is moved to page 2, beside
        // the rest of its description: every word still prints, and the amount is
        // still the record's own — only the page it printed on is wrong. The row's
        // amount belongs on the page the row begins on.
        var words = SyntheticDocument.Extraction().Words.ToList();
        var index = words.FindIndex(word => string.Equals(word.Text, "200.00", StringComparison.Ordinal));
        Assert.True(index >= 0, "the built document prints no '200.00'");
        words[index] = words[index] with { Page = 2, YMin = 75, YMax = 87 };

        var verdict = JudgeTests.Judge(SyntheticDocument.Extraction().WithWords(words));

        Assert.Equal("fail", verdict.Rules["TI-4"]);
        var failure = Assert.Single(verdict.Failures);
        Assert.Equal("TI-4", failure.Rule);
        Assert.Equal("structure-lost", failure.Class);
        Assert.Equal(2, failure.Row);
        Assert.Equal("amount", failure.Field);
        Assert.Equal(2, failure.Page);
        Assert.Contains("page 1", failure.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void TheVerdictOnAWrongAmountCarriesTheValueOfNoText()
    {
        var verdict = JudgeTests.Judge(Replaced("100.00", "190.01"));

        Assert.False(verdict.Passed);
        foreach (var value in new[] { "190.01", "100.00", "2026-01-01", "alpha" })
        {
            Assert.DoesNotContain(value, verdict.ToJson(), StringComparison.Ordinal);
        }
    }

    /// <summary>The document with one of its printed words replaced, and nothing else changed.</summary>
    private static PdfExtraction Replaced(string printed, string with)
    {
        var words = SyntheticDocument.Extraction().Words.ToList();
        var index = words.FindIndex(word => string.Equals(word.Text, printed, StringComparison.Ordinal));
        Assert.True(index >= 0, $"the built document prints no '{printed}'");
        words[index] = words[index] with { Text = with };
        return SyntheticDocument.Extraction().WithWords(words);
    }

    /// <summary>The document without one of its printed words, and nothing else changed.</summary>
    private static PdfExtraction Without(string printed)
    {
        var words = JudgeTests.Without(SyntheticDocument.Extraction().Words, word => string.Equals(word.Text, printed, StringComparison.Ordinal));
        Assert.Equal(SyntheticDocument.Extraction().Words.Count - 1, words.Count);
        return SyntheticDocument.Extraction().WithWords(words);
    }
}
