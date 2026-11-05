using ReportService.Templates;

namespace ReportService.Pdf.Verification.Tests;

/// <summary>
/// The content-order alignment: the expected units consumed page by page, the split
/// points searched longest first, the repeated head and foot of a surface, and the
/// class each way of getting the printed text wrong is reported with.
/// </summary>
public sealed class ContentOrderAlignerTests
{
    private static TextUnit Unit(string text, int index, bool head = false, bool foot = false) =>
        new(text, Normalisation.Normalise(text), index, FragmentKind.Row, index + 1, 0, head, foot);

    private static TextUnit Head(string text) => Unit(text, 0, head: true);

    private static AlignmentOutcome Align(IReadOnlyList<TextUnit> units, params string[] pages) =>
        new ContentOrderAligner().Align(Sample.Pages(pages), units);

    [Fact]
    public void OneUnitOnOnePagePasses()
    {
        var outcome = Align([Unit("alpha", 0)], "alpha");

        Assert.True(outcome.Passed);
        Assert.Null(outcome.Reason);
    }

    [Fact]
    public void AUnitSplitAcrossTwoPagesPasses()
    {
        var outcome = Align([Unit("abcdef", 0)], "abc", "def");

        Assert.True(outcome.Passed);
        Assert.Equal([6], outcome.ConsumedCharacters);
    }

    [Fact]
    public void ThreePagesPass()
    {
        var outcome = Align([Unit("abc", 0), Unit("def", 1), Unit("ghi", 2)], "abc", "def", "ghi");

        Assert.True(outcome.Passed);
    }

    [Fact]
    public void ARepeatedHeadIsConsumedOnEveryPageThatPrintsIt()
    {
        var units = new[] { Head("SeqAmount"), Unit("one", 1), Unit("two", 2) };

        var outcome = Align(units, "SeqAmountone", "SeqAmounttwo");

        Assert.True(outcome.Passed);
    }

    [Fact]
    public void APageThatDoesNotRepeatTheHeadPasses()
    {
        var units = new[] { Head("SeqAmount"), Unit("one", 1), Unit("two", 2) };

        var outcome = Align(units, "SeqAmountone", "two");

        Assert.True(outcome.Passed);
    }

    [Fact]
    public void ARepeatedFootIsConsumedBeforeTheEndOfAPage()
    {
        var units = new[] { Unit("one", 0), Unit("two", 1), Unit("total", 2, foot: true) };

        var outcome = Align(units, "onetotal", "twototal");

        Assert.True(outcome.Passed);
    }

    [Fact]
    public void AnAmbiguousSplitIsResolvedAndPasses()
    {
        var outcome = Align([Unit("ab", 0), Unit("b", 1)], "abb");

        Assert.True(outcome.Passed);
    }

    [Fact]
    public void ALostRowIsReportedAsSuch()
    {
        var units = new[] { Unit("rowone", 0), Unit("rowtwo", 1), Unit("rowthree", 2) };

        var outcome = Align(units, "rowonerowthree");

        Assert.False(outcome.Passed);
        Assert.Equal(VerificationReasons.RowLost, outcome.Reason);
        Assert.Equal(1, outcome.UnitIndex);
    }

    [Fact]
    public void ADroppedWordIsReportedAsDroppedText()
    {
        var outcome = Align([Unit("alphaANDbeta", 0)], "alphabeta");

        Assert.False(outcome.Passed);
        Assert.Equal(VerificationReasons.TextDropped, outcome.Reason);
    }

    [Fact]
    public void ARepeatedLineIsReportedAsExtraText()
    {
        var outcome = Align([Unit("alpha", 0), Unit("beta", 1)], "alphaalphabeta");

        Assert.False(outcome.Passed);
        Assert.Equal(VerificationReasons.TextExtra, outcome.Reason);
    }

    [Fact]
    public void AnAlteredWordIsReportedAsAlteredText()
    {
        var outcome = Align([Unit("amount100.00", 0)], "amount100.01");

        Assert.False(outcome.Passed);
        Assert.Equal(VerificationReasons.TextAltered, outcome.Reason);
    }

    [Fact]
    public void TextMissingAtTheEndIsReportedAsDropped()
    {
        var outcome = Align([Unit("alphabeta", 0)], "alpha");

        Assert.False(outcome.Passed);
        Assert.Equal(VerificationReasons.TextDropped, outcome.Reason);
    }

    [Fact]
    public void TextBeyondTheCompositionIsReportedAsExtra()
    {
        var outcome = Align([Unit("alpha", 0)], "alphaextra");

        Assert.False(outcome.Passed);
        Assert.Equal(VerificationReasons.TextExtra, outcome.Reason);
    }

    [Fact]
    public void APageWithNothingToPrintAndNothingComposedPasses()
    {
        Assert.True(Align([], string.Empty).Passed);
    }

    [Fact]
    public void ADocumentThatHoldsNoPageFails()
    {
        var outcome = Align([Unit("alpha", 0)]);

        Assert.False(outcome.Passed);
    }

    [Fact]
    public void AnAlignmentOverItsBudgetFailsAsAmbiguous()
    {
        var outcome = new ContentOrderAligner(budget: 1).Align(Sample.Pages("abcdef"), [Unit("abcdef", 0)]);

        Assert.False(outcome.Passed);
        Assert.Equal(VerificationReasons.AmbiguousAlignment, outcome.Reason);
    }

    [Fact]
    public void EveryUnitAndEveryPageIsConsumedExactlyOnce()
    {
        var outcome = Align([Unit("abc", 0), Unit("def", 1)], "abcdef");

        Assert.True(outcome.Passed);
        Assert.Equal([3, 3], outcome.ConsumedCharacters);
    }

    /// <summary>The units of one row: the cells of one fragment, in document order.</summary>
    private static TextUnit[] Cells(int fragment, long ordinal, params string[] texts) =>
    [
        .. texts.Select((text, index) => new TextUnit(
            text,
            Normalisation.Normalise(text),
            fragment,
            FragmentKind.Row,
            ordinal,
            index,
            false,
            false)),
    ];

    [Fact]
    public void ACellInterruptedByALaterCellOfItsRowContinuesOnTheNextPage()
    {
        // A row that runs past the page: the cells that fit print on the page the row
        // began on, and the tall cell's remainder is the first thing the next page prints.
        var units = new List<TextUnit>();
        units.AddRange(Cells(0, 1, "39", "2026-09-09", "abcdefgh", "22531.20"));
        units.AddRange(Cells(1, 2, "40", "2026-09-12"));

        var outcome = Align(units, "392026-09-09abcd22531.20", "efgh402026-09-12");

        Assert.True(outcome.Passed);
        Assert.Equal([2, 10, 8, 8, 2, 10], outcome.ConsumedCharacters);
    }

    [Fact]
    public void ACellContinuesAfterTheRepeatedHeadOfTheNextPage()
    {
        var units = new List<TextUnit> { Head("SeqAmount") };
        units.AddRange(Cells(0, 1, "39", "abcdefgh", "22531.20"));
        units.AddRange(Cells(1, 2, "40"));

        var outcome = Align(units, "SeqAmount39abcd22531.20", "SeqAmountefgh40");

        Assert.True(outcome.Passed);
    }

    [Fact]
    public void ACellInterruptedByAnotherRowsTextFails()
    {
        // Only a later cell of the interrupted cell's own row may print before it
        // continues: the next row's text must wait for the remainder.
        var units = new List<TextUnit>();
        units.AddRange(Cells(0, 1, "a", "abcdefgh"));
        units.AddRange(Cells(1, 2, "b"));

        var outcome = Align(units, "aabcd", "b");

        Assert.False(outcome.Passed);
    }

    [Fact]
    public void ARowMissingAtTheEndIsReportedAsRowLost()
    {
        var units = new List<TextUnit>();
        units.AddRange(Cells(0, 1, "a"));
        units.AddRange(Cells(1, 2, "b"));

        var outcome = Align(units, "a");

        Assert.False(outcome.Passed);
        Assert.Equal(VerificationReasons.RowLost, outcome.Reason);
        Assert.Equal(1, outcome.UnitIndex);
    }
}
