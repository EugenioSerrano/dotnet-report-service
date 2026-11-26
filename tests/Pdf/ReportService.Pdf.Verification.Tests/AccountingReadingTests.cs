using System.Globalization;
using ReportService.Pdf.Verification;
using ReportService.Templates;

namespace ReportService.Pdf.Verification.Tests;

/// <summary>
/// The accounting band's own reading and writing: a value is written with exactly the
/// declared fraction digits and no group separator, in the block's presentation culture,
/// and a printed value is read back under the same rule — a group separator, another
/// scale or a missing separator is refused rather than read as a similar number. The
/// band's three labels are found in the footer band and the word that follows each is its
/// value; the fixed continuation mark is not composed text and is removed from the order
/// check's page text.
/// </summary>
public sealed class AccountingReadingTests
{
    private static readonly CultureInfo Argentine = CultureInfo.GetCultureInfo("es-AR");

    private static readonly AccountingFooter Footer = new(
        12, 282, 186, 12,
        "Saldo anterior", "Subtotal de pagina", "Saldo siguiente");

    [Fact]
    public void AValueIsWrittenAtTheDeclaredScaleWithoutAGroupSeparator()
    {
        Assert.True(AccountingReading.TryFormat(1234.56m, 2, Argentine, out var text));
        Assert.Equal("1234,56", text);

        Assert.True(AccountingReading.TryFormat(-0.5m, 2, Argentine, out var negative));
        Assert.Equal("-0,50", negative);

        Assert.True(AccountingReading.TryFormat(10m, 0, Argentine, out var whole));
        Assert.Equal("10", whole);
    }

    [Fact]
    public void AScaleOutsideTheRangeIsRefused()
    {
        Assert.False(AccountingReading.TryFormat(1m, 10, Argentine, out _));
        Assert.False(AccountingReading.TryFormat(1m, -1, Argentine, out _));
    }

    [Fact]
    public void AValueIsReadBackUnderTheDeclaredScale()
    {
        Assert.True(AccountingReading.TryParse("1234,56", 2, Argentine, out var value));
        Assert.Equal(1234.56m, value);

        Assert.True(AccountingReading.TryParse("-0,50", 2, Argentine, out var negative));
        Assert.Equal(-0.50m, negative);

        Assert.True(AccountingReading.TryParse("10", 0, Argentine, out var whole));
        Assert.Equal(10m, whole);
    }

    [Theory]
    [InlineData("1.234,56")]
    [InlineData("1234,5")]
    [InlineData("1 234,56")]
    [InlineData("1234.56")]
    [InlineData(",56")]
    [InlineData("")]
    [InlineData("1234,56,7")]
    public void AnUnusablePrintedValueIsRefused(string text)
    {
        Assert.False(AccountingReading.TryParse(text, 2, Argentine, out _));
    }

    [Fact]
    public void TheThreeLabelledValuesAreReadFromTheFooterBand()
    {
        var page = new PrintedPage(1, 595, 842, [
            .. FooterWords("Saldo anterior", "0,00"),
            .. FooterWords("Subtotal de pagina", "10,00"),
            .. FooterWords("Saldo siguiente", "10,00"),
            new PrintedWord("row", 10, 40, 60, 50, 0, ReadingBand.Content),
        ]);

        var lines = AccountingReading.ReadFooter(page, Footer);

        Assert.True(lines.Complete);
        Assert.Equal("0,00", lines.BroughtForward);
        Assert.Equal("10,00", lines.PageSubtotal);
        Assert.Equal("10,00", lines.CarriedForward);
    }

    [Fact]
    public void APageWithoutItsLabelsReportsTheLinesItLacks()
    {
        var page = new PrintedPage(1, 595, 842, [.. FooterWords("Saldo anterior", "0,00")]);

        var lines = AccountingReading.ReadFooter(page, Footer);

        Assert.False(lines.Complete);
        Assert.Equal("0,00", lines.BroughtForward);
        Assert.Null(lines.PageSubtotal);
        Assert.Null(lines.CarriedForward);
    }

    [Fact]
    public void TheContinuationMarkIsRemovedFromTheOrderCheckPageText()
    {
        var page = new PrintedPage(1, 595, 842, [
            new PrintedWord("First", 10, 40, 60, 50, 0, ReadingBand.Content),
            new PrintedWord(AccountingReading.ContinuesMark, 62, 40, 120, 50, 0, ReadingBand.Content),
            new PrintedWord("Second", 10, 60, 60, 70, 0, ReadingBand.Content),
            new PrintedWord("footer", 10, 792, 60, 800, 0, ReadingBand.Footer),
        ]);

        var text = AccountingReading.ContentTextWithoutMark(page);

        Assert.Contains("First", text, StringComparison.Ordinal);
        Assert.Contains("Second", text, StringComparison.Ordinal);
        Assert.DoesNotContain("continues", text, StringComparison.Ordinal);
        Assert.DoesNotContain("footer", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AWordIsInsideTheRectangleByItsCentre()
    {
        var inside = new PrintedWord("value", 100, 800, 120, 810, 0, ReadingBand.Footer);
        var outside = new PrintedWord("value", 300, 800, 320, 810, 0, ReadingBand.Footer);

        Assert.True(AccountingReading.IsInside(inside, 90, 790, 100, 40));
        Assert.False(AccountingReading.IsInside(outside, 90, 790, 100, 40));
    }

    [Fact]
    public void TheRowAmountIsWrittenInTheInvariantForm()
    {
        Assert.Equal("1234.56", AccountingReading.FormatAmount(1234.56m, 2));
        Assert.Equal("10.50", AccountingReading.FormatAmount(10.5m, 2));
        Assert.Equal("10", AccountingReading.FormatAmount(10m, 0));
        Assert.Equal("-0.50", AccountingReading.FormatAmount(-0.5m, 2));
    }

    [Fact]
    public void TheAccountingTokensAreRemovedOncePerOccurrenceAndTheMarkAlways()
    {
        var page = new PrintedPage(1, 595, 842, [
            .. Content("first"),
            .. Content("10.00"),
            .. Content("10.00"),
            .. Content(AccountingReading.ContinuesMark),
            .. Content("10,00"),
            .. Content("last"),
        ]);

        var once = AccountingReading.ContentTextWithoutAccounting(page, new Dictionary<string, int>(StringComparer.Ordinal) { ["10.00"] = 1, ["10,00"] = 1 });

        Assert.Equal("first10.00last", once);

        var twice = AccountingReading.ContentTextWithoutAccounting(page, new Dictionary<string, int>(StringComparer.Ordinal) { ["10.00"] = 2, ["10,00"] = 1 });
        Assert.Equal("firstlast", twice);
    }

    private static IEnumerable<PrintedWord> Content(string text) =>
        [new PrintedWord(text, 10, 40, 60, 50, 0, ReadingBand.Content)];

    private static IEnumerable<PrintedWord> FooterWords(string label, string value)
    {
        var left = 10d;
        foreach (var word in label.Split(' '))
        {
            yield return new PrintedWord(word, left, 792, left + 20, 800, 0, ReadingBand.Footer);
            left += 25;
        }

        yield return new PrintedWord(value, left, 792, left + 20, 800, 0, ReadingBand.Footer);
    }
}
