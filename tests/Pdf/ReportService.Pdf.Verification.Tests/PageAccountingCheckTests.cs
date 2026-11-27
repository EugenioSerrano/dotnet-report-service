using System.Globalization;
using ReportService.Pdf.Verification;
using ReportService.Templates;

namespace ReportService.Pdf.Verification.Tests;

/// <summary>
/// The page-accounting check over synthetic readings: page subtotals, the
/// brought/carried-forward chain, group totals across a block boundary, the amount on
/// the row's last part, the continuation marks, the footer band and the numbering area.
/// The expected values come from the block's records and the partition's index facts;
/// each known-bad reading fails with its own reason, and none of the reasons carries a
/// value.
/// </summary>
public sealed class PageAccountingCheckTests
{
    private static readonly AccountingFooter Footer = new(
        12, 282, 186, 12,
        "Saldo anterior", "Subtotal de pagina", "Saldo siguiente");

    [Fact]
    public void ACorrectAccountingPasses()
    {
        var verdict = Run(Printed());

        Assert.True(verdict.Passed, verdict.Reason);
        Assert.Null(verdict.Reason);
    }

    [Fact]
    public void AZeroRecordBlockPassesWithEveryValueZero()
    {
        var printed = Printed(
            records: [],
            broughtOfPageOne: "0,00",
            subtotalOfPageOne: "0,00",
            carriedOfPageOne: "0,00",
            broughtOfPageTwo: "0,00",
            subtotalOfPageTwo: "0,00",
            closing: "0,00",
            groupAOfPageOne: null,
            groupBOfPageTwo: null,
            blockTotal: 0.00m);

        var verdict = Run(printed);

        Assert.True(verdict.Passed, verdict.Reason);
    }

    [Fact]
    public void APageSubtotalWrongByACentFails()
    {
        var verdict = Run(Printed(subtotalOfPageTwo: "1,01"));

        Assert.False(verdict.Passed);
        Assert.Equal(VerificationReasons.PageSubtotalWrong, verdict.Reason);
        Assert.Equal(2, verdict.Page);
    }

    [Fact]
    public void ABroughtForwardThatDoesNotCarryTheOpeningFails()
    {
        var verdict = Run(Printed(broughtOfPageOne: "1,00"));

        Assert.False(verdict.Passed);
        Assert.Equal(VerificationReasons.BroughtForwardWrong, verdict.Reason);
        Assert.Equal(1, verdict.Page);
    }

    [Fact]
    public void ACarriedForwardThatDoesNotCloseFails()
    {
        var verdict = Run(Printed(carriedOfPageOne: "9,99"));

        Assert.False(verdict.Passed);
        Assert.Equal(VerificationReasons.CarriedForwardWrong, verdict.Reason);
        Assert.Equal(1, verdict.Page);
    }

    [Fact]
    public void ABlockTotalThatDisagreesWithThePartitionFails()
    {
        var verdict = Run(Printed(blockTotal: 21.00m));

        Assert.False(verdict.Passed);
        Assert.Equal(VerificationReasons.AccountingChainWrong, verdict.Reason);
    }

    [Fact]
    public void AContinuingBlockWithoutItsOpeningGroupAmountFailsClosed()
    {
        var verdict = Run(Printed(continuesPrevious: true, openingGroupAccumulated: null));

        Assert.False(verdict.Passed);
        Assert.Equal(VerificationReasons.AccountingIndexIncomplete, verdict.Reason);
    }

    [Fact]
    public void AGroupTotalThatOmitsTheOpeningAmountFails()
    {
        // The block continues group A with 3,00 already printed in an earlier block, so its
        // final total is 13,00; the page prints only this block's 10,00.
        var verdict = Run(Printed(continuesPrevious: true, openingGroupAccumulated: 3.00m, groupAOfPageOne: "10,00"));

        Assert.False(verdict.Passed);
        Assert.Equal(VerificationReasons.AccountingGroupWrong, verdict.Reason);
        Assert.Equal(1, verdict.Page);
    }

    [Fact]
    public void AGroupThatContinuesIntoTheNextBlockPrintsNoTotal()
    {
        var verdict = Run(Printed(continuesNext: true, groupBOfPageTwo: "10,00"));

        Assert.False(verdict.Passed);
        Assert.Equal(VerificationReasons.AccountingGroupWrong, verdict.Reason);
        Assert.Equal(2, verdict.Page);
    }

    [Fact]
    public void AContinuationPartWithoutItsMarkFails()
    {
        // The first record prints on page one and ends on page two: page one must carry the
        // fixed mark in its own amount cell.
        var verdict = Run(Printed(
            splitFirstRecord: true,
            markOnPageOne: false,
            broughtOfPageOne: "0,00",
            subtotalOfPageOne: "0,00",
            carriedOfPageOne: "0,00",
            broughtOfPageTwo: "0,00",
            subtotalOfPageTwo: "20,00",
            closing: "20,00",
            groupAOfPageOne: null,
            groupAOfPageTwo: "10,00"));

        Assert.False(verdict.Passed);
        Assert.Equal(VerificationReasons.AccountingMarkWrong, verdict.Reason);
        Assert.Equal(1, verdict.Page);
        Assert.Equal(1, verdict.RecordOrdinal);
    }

    [Fact]
    public void AnAmountLeftOnANonFinalPartFails()
    {
        var verdict = Run(Printed(
            splitFirstRecord: true,
            markOnPageOne: true,
            amountOnPageOne: true,
            broughtOfPageOne: "0,00",
            subtotalOfPageOne: "0,00",
            carriedOfPageOne: "0,00",
            broughtOfPageTwo: "0,00",
            subtotalOfPageTwo: "20,00",
            closing: "20,00",
            groupAOfPageOne: null,
            groupAOfPageTwo: "10,00"));

        Assert.False(verdict.Passed);
        Assert.Equal(VerificationReasons.AccountingMarkWrong, verdict.Reason);
        Assert.Equal(1, verdict.Page);
    }

    [Fact]
    public void APageWithoutItsFooterFails()
    {
        var verdict = Run(Printed(footerOnPageTwo: false));

        Assert.False(verdict.Passed);
        Assert.Equal(VerificationReasons.AccountingFooterMissing, verdict.Reason);
        Assert.Equal(2, verdict.Page);
    }

    [Fact]
    public void AValuePrintedWithAGroupSeparatorFails()
    {
        var verdict = Run(Printed(subtotalOfPageTwo: "10,00", carriedOfPageTwo: "1.010,00"));

        Assert.False(verdict.Passed);
        Assert.Equal(VerificationReasons.AccountingAmountInvalid, verdict.Reason);
    }

    [Fact]
    public void AValuePrintedAtAnotherScaleFails()
    {
        var verdict = Run(Printed(subtotalOfPageTwo: "10,0"));

        Assert.False(verdict.Passed);
        Assert.Equal(VerificationReasons.AccountingAmountInvalid, verdict.Reason);
    }

    [Fact]
    public void AnAccountingWordInsideTheNumberingAreaFails()
    {
        var verdict = Run(Printed(numberingArea: new NumberingAreaMm(12, 279, 120, 12)));

        Assert.False(verdict.Passed);
        Assert.Equal(VerificationReasons.AccountingNumberingCollision, verdict.Reason);
    }

    [Fact]
    public void ARecordWhoseAmountIsMissingFailsClosed()
    {
        var verdict = Run(Printed(secondRecordAmount: null));

        Assert.False(verdict.Passed);
        Assert.Equal(VerificationReasons.AccountingAmountInvalid, verdict.Reason);
        Assert.Equal(2, verdict.RecordOrdinal);
    }

    [Fact]
    public void AmountsGivenAsIntegersAndLongsAreReadExactly()
    {
        var printed = Printed(records: [(1, 10, "A"), (2, 5L, "B"), (3, 5L, "B")]);

        var verdict = Run(printed);

        Assert.True(verdict.Passed, verdict.Reason);
    }

    [Fact]
    public void AnUnusablePresentationCultureFailsClosed()
    {
        var printed = Printed();
        var input = printed.Input with
        {
            Accounting = printed.Input.Accounting! with { Culture = "not a culture tag" },
        };

        var verdict = Run(printed with { Input = input });

        Assert.False(verdict.Passed);
        Assert.Equal(VerificationReasons.AccountingAmountInvalid, verdict.Reason);
    }

    [Fact]
    public void TheExpectedAccountingTextsAreTheInvariantAmountsAndTheCultureTotals()
    {
        var printed = Printed();
        var texts = new PageAccountingCheck().ExpectedAccountingTexts(printed.Input.Accounting!);

        // The row amounts are the invariant exact decimals; the group totals are the band's
        // culture format with no group separator.
        Assert.Contains("10.00", texts);
        Assert.Contains("5.50", texts);
        Assert.Contains("4.50", texts);
        Assert.Contains("10,00", texts);
        Assert.DoesNotContain("10,00", texts.Where(text => text.Contains('.', StringComparison.Ordinal)));
    }

    [Fact]
    public void AContinuingGroupContributesNoTotalAndTheOpeningIsAdded()
    {
        var printed = Printed(continuesPrevious: true, openingGroupAccumulated: 3.00m);
        var check = new PageAccountingCheck();

        var texts = check.ExpectedGroupTotalTexts(printed.Input.Accounting!);

        // Group A continues from the previous block, so its total is its own rows plus the
        // recorded opening; group B's is its own rows.
        Assert.Contains("13,00", texts);
        Assert.Contains("10,00", texts);

        var continuing = Printed(continuesNext: true);
        Assert.DoesNotContain("10,00", check.ExpectedGroupTotalTexts(continuing.Input.Accounting!).Skip(1));
    }

    [Fact]
    public void TheOrderCheckPageTextRemovesTheMarkAndTheAccountingTokens()
    {
        var printed = Printed();
        var page = new PrintedPage(1, 595, 842, [
            .. Content("First"),
            .. Content("10.00"),
            .. Content("10,00"),
            .. Content(AccountingReading.ContinuesMark),
            .. Content("kept"),
        ]);

        var texts = new PageAccountingCheck().PageTexts(new PdfReading([page]), printed.Input.Accounting!);

        _ = Assert.Single(texts);
        Assert.Equal("Firstkept", texts[0]);
    }

    private static AccountingVerdict Run(PrintedFixture printed) =>
        new PageAccountingCheck().Check(printed.Input, printed.Reading, printed.Alignment);

    private sealed record PrintedFixture(PrintedCheckInput Input, PdfReading Reading, AlignmentOutcome Alignment);

    private static PrintedFixture Printed(
        (long Ordinal, object Amount, string Group)[]? records = null,
        string broughtOfPageOne = "0,00",
        string subtotalOfPageOne = "10,00",
        string carriedOfPageOne = "10,00",
        string broughtOfPageTwo = "10,00",
        string subtotalOfPageTwo = "10,00",
        string? carriedOfPageTwo = null,
        string closing = "20,00",
        string? groupAOfPageOne = "10,00",
        string? groupAOfPageTwo = null,
        string? groupBOfPageTwo = "10,00",
        bool continuesPrevious = false,
        decimal? openingGroupAccumulated = null,
        bool continuesNext = false,
        decimal? blockTotal = 20.00m,
        bool splitFirstRecord = false,
        bool markOnPageOne = true,
        bool amountOnPageOne = false,
        bool footerOnPageTwo = true,
        NumberingAreaMm? numberingArea = null,
        string? secondRecordAmount = "5.50")
    {
        var source = records ?? [(1, (object)"10.00", "A"), (2, "5.50", "B"), (3, "4.50", "B")];
        string AmountText(long ordinal)
        {
            var entry = source.FirstOrDefault(record => record.Ordinal == ordinal);
            var value = ordinal == 2 ? (object?)secondRecordAmount : entry.Amount;
            return value is null ? "0,00" : TextOf(value);
        }

        var values = source
            .Select(record => new BlockRecord(
                record.Ordinal,
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["amount"] = record.Ordinal == 2 && records is null ? secondRecordAmount : record.Amount,
                    ["group"] = record.Group,
                }))
            .ToList();

        var block = new BlockFacts(0, 1, values, new Dictionary<string, object?>());
        var index = new IndexFacts(
            0,
            values.Count,
            1,
            ContinuesPreviousGroup: continuesPrevious,
            ContinuesNextGroup: continuesNext,
            OpeningGroupKey: continuesPrevious ? "A" : null,
            OpeningCarriedForward: 0m,
            OpeningGroupAccumulated: openingGroupAccumulated,
            BlockAmountTotal: blockTotal);

        var units = new List<TextUnit>();
        var pages = new List<int>();
        var fragment = 0;
        foreach (var record in source)
        {
            if (record.Ordinal == 1 && splitFirstRecord)
            {
                units.Add(Unit("split", fragment++, record.Ordinal));
                pages.Add(1);
                units.Add(Unit("rest", fragment++, record.Ordinal));
                pages.Add(2);
            }
            else
            {
                units.Add(Unit("row" + record.Ordinal, fragment++, record.Ordinal));
                pages.Add(record.Ordinal == 1 ? 1 : 2);
            }
        }

        var extracted = new ExtractedText(units);
        var composition = Sample.Block((FragmentKind.DocumentStart, 0, "<p>title</p>"), (FragmentKind.DocumentEnd, 0, "<p>end</p>"));
        var alignment = new AlignmentOutcome(true, null, null, null, null, [.. units.Select(_ => 1)], 0, false, pages);

        var pageOneWords = new List<PrintedWord>();
        pageOneWords.AddRange(Content("row1"));
        if (splitFirstRecord)
        {
            pageOneWords.AddRange(Content("split"));
            if (markOnPageOne)
            {
                pageOneWords.AddRange(Content(AccountingReading.ContinuesMark));
            }

            if (amountOnPageOne)
            {
                pageOneWords.AddRange(Content(AmountText(1)));
            }
        }
        else
        {
            pageOneWords.AddRange(Content(AmountText(1)));
        }

        if (groupAOfPageOne is { } groupA)
        {
            pageOneWords.AddRange(Content(groupA));
        }

        pageOneWords.AddRange(FooterWords(broughtOfPageOne, subtotalOfPageOne, carriedOfPageOne));

        var pageTwoWords = new List<PrintedWord>();
        if (splitFirstRecord)
        {
            pageTwoWords.AddRange(Content("rest"));
            pageTwoWords.AddRange(Content(AmountText(1)));
        }

        pageTwoWords.AddRange(Content("row2"));
        pageTwoWords.AddRange(Content(AmountText(2)));
        pageTwoWords.AddRange(Content("row3"));
        pageTwoWords.AddRange(Content(AmountText(3)));
        if (groupAOfPageTwo is { } groupATwo)
        {
            pageTwoWords.AddRange(Content(groupATwo));
        }

        if (groupBOfPageTwo is { } groupB)
        {
            pageTwoWords.AddRange(Content(groupB));
        }

        if (footerOnPageTwo)
        {
            pageTwoWords.AddRange(FooterWords(broughtOfPageTwo, subtotalOfPageTwo, carriedOfPageTwo ?? closing));
        }

        var reading = new PdfReading([Page(1, pageOneWords), Page(2, pageTwoWords)]);
        var input = new PrintedCheckInput(
            "unused.pdf",
            0,
            PageFrame.FromMillimetres(210, 297, 15, 12, 18, 12),
            composition,
            extracted,
            0,
            new AccountingCheckInput(block, index, "amount", "group", 2, "es-AR", Footer, numberingArea));
        return new PrintedFixture(input, reading, alignment);
    }

    private static string TextOf(object value)
    {
        var amount = value switch
        {
            decimal exact => exact,
            int integer => integer,
            long large => large,
            string text when decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => 0m,
        };
        // The row prints the amount as the render context gives it: the invariant exact
        // decimal at the declared scale; only the band's values use the culture.
        return AccountingReading.FormatAmount(amount, 2);
    }

    private static TextUnit Unit(string text, int index, long ordinal) =>
        new(text, Normalisation.Normalise(text), index, FragmentKind.Row, ordinal, 0, false, false, Accounting: false);

    private static IEnumerable<PrintedWord> Content(string text) =>
        [new PrintedWord(text, 10, 40, 60, 50, 0, ReadingBand.Content)];

    private static IEnumerable<PrintedWord> FooterWords(string brought, string subtotal, string carried) =>
        [
            new PrintedWord("Saldo", 12, 792, 30, 800, 0, ReadingBand.Footer),
            new PrintedWord("anterior", 32, 792, 50, 800, 0, ReadingBand.Footer),
            new PrintedWord(brought, 52, 792, 80, 800, 0, ReadingBand.Footer),
            new PrintedWord("Subtotal", 100, 792, 120, 800, 0, ReadingBand.Footer),
            new PrintedWord("de", 122, 792, 130, 800, 0, ReadingBand.Footer),
            new PrintedWord("pagina", 132, 792, 150, 800, 0, ReadingBand.Footer),
            new PrintedWord(subtotal, 152, 792, 180, 800, 0, ReadingBand.Footer),
            new PrintedWord("Saldo", 200, 792, 220, 800, 0, ReadingBand.Footer),
            new PrintedWord("siguiente", 222, 792, 250, 800, 0, ReadingBand.Footer),
            new PrintedWord(carried, 252, 792, 280, 800, 0, ReadingBand.Footer),
        ];

    private static PrintedPage Page(int number, IEnumerable<PrintedWord> words) =>
        new(number, 595, 842, [.. words]);
}
