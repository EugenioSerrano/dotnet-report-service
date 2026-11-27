using ReportService.Pdf.Verification.PageCodes;

namespace ReportService.Pdf.Verification.Tests;

public sealed class PageCodeSourceTests
{
    [Fact]
    public void EveryTokenIsReplacedAndBracesEscape()
    {
        var source = Parse("{{x}} {page.number}/{page.total} {template.id}@{template.version} {match}", "INV-[0-9]{4}", 8);

        var value = source.Evaluate(Inputs("header INV-0042 footer", 3, 9));

        Assert.Equal("{x} 3/9 invoices@1.2.0 INV-0042", value);
    }

    [Fact]
    public void TheNamedGroupValueIsTheMatch()
    {
        var source = Parse("{match}", "No\\. (?<value>[0-9]+)", 8);

        Assert.Equal("123", source.Evaluate(Inputs("Invoice No. 123 of", 1, 1)));
    }

    [Theory]
    [InlineData("no invoice here")]
    [InlineData("INV-123456789012")]
    public void NoMatchOrAMatchLongerThanMaxLengthYieldsNoValue(string text)
    {
        var source = Parse("{match}", "INV-[0-9]+", 8);

        Assert.Null(source.Evaluate(Inputs(text, 1, 1)));
    }

    [Fact]
    public void APageBeyondTheBoundYieldsNoValue()
    {
        var source = Parse("{page.number}", null, null);

        Assert.Null(source.Evaluate(Inputs(string.Empty, PageCodeSource.MaximumPageNumber + 1, 1)));
        Assert.Equal("9999999", source.Evaluate(Inputs(string.Empty, PageCodeSource.MaximumPageNumber, 1)));
    }

    [Fact]
    public void AValueOverTheByteBoundAtRunTimeYieldsNoValue()
    {
        var source = Parse(new string('x', 200), null, null);

        Assert.Null(source.Evaluate(Inputs(string.Empty, 1, 1)));
    }

    [Theory]
    [InlineData("", null, null, "source.value")]
    [InlineData("{page.count}", null, null, "source.value")]
    [InlineData("{page.number", null, null, "source.value")]
    [InlineData("a}b", null, null, "source.value")]
    [InlineData("{match}", null, 4, "source.match.pattern")]
    [InlineData("{match}", "(", 4, "source.match.pattern")]
    [InlineData("{match}", "(a)\\1", 4, "source.match.pattern")]
    [InlineData("{match}", "a", null, "source.match.maxLength")]
    [InlineData("{match}", "a", 129, "source.match.maxLength")]
    [InlineData("{page.number}", "a", 4, "source.match")]
    public void AMalformedSourceIsRefusedNamingItsField(string value, string? pattern, int? maxLength, string field)
    {
        var problems = new List<PageCodeProblem>();

        Assert.Null(PageCodeSource.Parse(value, pattern, maxLength, problems));
        Assert.Contains(problems, problem => problem.Field == field);
        Assert.All(problems, problem => Assert.False(string.IsNullOrWhiteSpace(problem.Fix)));
    }

    [Fact]
    public void ATemplateLongerThanTheLimitIsRefused()
    {
        var problems = new List<PageCodeProblem>();

        Assert.Null(PageCodeSource.Parse(new string('x', PageCodeSource.MaximumTemplateLength + 1), null, null, problems));
    }

    [Fact]
    public void TheStaticBoundCountsLiteralsPageTokensIdentityAndFourBytesPerMatchedCharacter()
    {
        var source = Parse("{template.id}|{template.version}|{page.number}/{page.total}|{match}", "x", 12);

        // "|" x3 + "/" = 4 literal bytes; id 8; version 5; two page tokens 14; match 48.
        Assert.Equal(4 + 8 + 5 + 14 + 48, source.StaticBound("invoices", "1.2.0"));
        Assert.Equal((128 - (4 + 8 + 5 + 14)) / 4, source.LargestMatchLength("invoices", "1.2.0"));
        Assert.True(source.UsesMatch);
        Assert.Equal(12, source.MatchLength);
    }

    [Fact]
    public void WithNoOtherTokenTheLargestMatchIsThirtyTwo()
    {
        Assert.Equal(32, Parse("{match}", "x", 1).LargestMatchLength("t", "1"));
        Assert.Equal(0, Parse(new string('x', 128) + "{match}", "x", 1).LargestMatchLength("t", "1"));
    }

    [Fact]
    public void APageTextExcludesTheGivenRegionsAndNormalises()
    {
        var page = new PrintedPage(1, 600, 800,
        [
            Word("Café", 10, 10),
            Word("stamp", 400, 10),
            Word("code", 10, 400),
            Word("  tail  ", 300, 300),
        ]);
        var stamp = new PageCodeText.Region(390, 0, 100, 40);
        var code = new PageCodeText.Region(0, 390, 50, 50);

        Assert.Equal("Café tail", PageCodeText.Of(page, stamp, code, null));
        Assert.Equal(["code"], PageCodeText.Inside(page, code));
        Assert.True(stamp.Overlaps(new PageCodeText.Region(450, 30, 10, 10)));
        Assert.False(stamp.Overlaps(code));
    }

    private static PrintedWord Word(string text, double left, double top) =>
        new(text, left, top, left + 20, top + 10, 0, ReadingBand.Content);

    private static PageCodeInputs Inputs(string text, int number, int total) =>
        new(text, number, total, "invoices", "1.2.0");

    private static PageCodeSource Parse(string value, string? pattern, int? maxLength)
    {
        var problems = new List<PageCodeProblem>();
        return PageCodeSource.Parse(value, pattern, maxLength, problems)
            ?? throw new InvalidOperationException(string.Join("; ", problems.Select(problem => problem.Problem)));
    }
}
