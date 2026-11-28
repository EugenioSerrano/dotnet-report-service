using System.Globalization;

namespace ReportService.Templates.Tests.Presentation;

/// <summary>
/// The ten presentation functions under the job's culture and zone: the same decimal in
/// three cultures, the same instant in three zones across a daylight-saving change, a
/// calendar date that never moves, the report's clock, and each typed refusal.
/// </summary>
public sealed class PresentationFunctionsTests
{
    private static readonly DateTimeOffset Clock = new(2026, 11, 2, 2, 21, 56, TimeSpan.Zero);

    [Fact]
    public void ADecimalIsPresentedInTheJobsCulture()
    {
        Assert.Equal("1.000,00", Text(In("es-AR", "UTC").FormatNumber("1000.00", "N2")));
        Assert.Equal("1,000.00", Text(In("en-US", "UTC").FormatNumber("1000.00", "N2")));
        Assert.Equal("1,000.00", Text(In("en-GB", "UTC").FormatNumber("1000.00", "N2")));
    }

    [Fact]
    public void ANegativeDecimalKeepsItsSignAndItsCulture()
    {
        Assert.Equal("-0,50", Text(In("es-AR", "UTC").FormatNumber("-0.50", "N2")));
        Assert.Equal("-0.50", Text(In("en-US", "UTC").FormatNumber("-0.50", "N2")));
    }

    [Theory]
    [InlineData("C2", "1.000,00")]
    [InlineData("P1", "100.000,0")]
    [InlineData("#,##0.00", "1.000,00")]
    public void TheCurrencyThePercentageAndTheCustomPatternAreTheCultures(string format, string expected)
    {
        Assert.Contains(expected, Text(In("es-AR", "UTC").FormatNumber("1000.00", format)), StringComparison.Ordinal);
    }

    [Fact]
    public void TheIntegerFormatRoundsToTheFormatsRule()
    {
        Assert.Equal("2", Text(In("es-AR", "UTC").FormatNumber("1.6", "F0")));
        Assert.Equal("1", Text(In("en-US", "UTC").FormatNumber("1.4", "F0")));
    }

    [Fact]
    public void AnInstantIsShownInTheJobsZone()
    {
        var buenosAires = In("es-AR", "America/Argentina/Buenos_Aires").FormatDate("2026-11-02T02:30:00Z", "dd/MM/yyyy");
        var tokyo = In("ja-JP", "Asia/Tokyo").FormatDate("2026-11-02T02:30:00Z", "dd/MM/yyyy");

        Assert.Equal("01/11/2026", Text(buenosAires));
        Assert.Equal("02/11/2026", Text(tokyo));
    }

    [Fact]
    public void ACalendarDatePrintsTheSameDayInEveryZone()
    {
        var buenosAires = Text(In("es-AR", "America/Argentina/Buenos_Aires").FormatDate("2026-10-29", "dd/MM/yyyy"));
        var tokyo = Text(In("ja-JP", "Asia/Tokyo").FormatDate("2026-10-29", "dd/MM/yyyy"));
        var madrid = Text(In("es-ES", "Europe/Madrid").FormatDate("2026-10-29", "dd/MM/yyyy"));

        Assert.Equal("29/10/2026", buenosAires);
        Assert.Equal(buenosAires, tokyo);
        Assert.Equal(buenosAires, madrid);
    }

    [Fact]
    public void AnInstantAcrossADaylightSavingChangeIsShownInTheJobsZone()
    {
        var es = In("es-ES", "Europe/Madrid");

        Assert.Equal("29/03/2026 01:30", Text(es.FormatDate("2026-03-29T00:30:00Z", "dd/MM/yyyy HH:mm")));
        Assert.Equal("29/03/2026 03:30", Text(es.FormatDate("2026-03-29T01:30:00Z", "dd/MM/yyyy HH:mm")));
        Assert.Equal("25/10/2026 02:30", Text(es.FormatDate("2026-10-25T00:30:00Z", "dd/MM/yyyy HH:mm")));
        Assert.Equal("25/10/2026 02:30", Text(es.FormatDate("2026-10-25T01:30:00Z", "dd/MM/yyyy HH:mm")));
    }

    [Fact]
    public void TheReportsClockIsTheJobsAcceptanceInstantInItsZone()
    {
        const string delivered = "2026-11-02T02:21:56.0000000+00:00";

        Assert.Equal("01/11/2026 23:21", Text(In("es-AR", "America/Argentina/Buenos_Aires").FormatDate(delivered, "dd/MM/yyyy HH:mm")));
        Assert.Equal("02/11/2026 11:21", Text(In("ja-JP", "Asia/Tokyo").FormatDate(delivered, "dd/MM/yyyy HH:mm")));
    }

    [Theory]
    [InlineData("2", "1", true)]
    [InlineData("1", "2", false)]
    [InlineData("-1", "0", false)]
    public void GreaterThanAnswersAFlag(string left, string right, bool expected)
    {
        var result = Assert.IsType<PresentationResult.Flag>(In("es-AR", "UTC").NumGreaterThan(left, right));

        Assert.Equal(expected, result.Value);
    }

    [Fact]
    public void TheSixComparisonsAgreeOnTheirBoundaries()
    {
        var functions = In("es-AR", "UTC");

        Assert.True(Assert.IsType<PresentationResult.Flag>(functions.NumGreaterOrEqual("1", "1")).Value);
        Assert.True(Assert.IsType<PresentationResult.Flag>(functions.NumLessOrEqual("1", "1")).Value);
        Assert.True(Assert.IsType<PresentationResult.Flag>(functions.NumEqual("0.50", "0.5")).Value);
        Assert.True(Assert.IsType<PresentationResult.Flag>(functions.NumNotEqual("1", "0")).Value);
        Assert.False(Assert.IsType<PresentationResult.Flag>(functions.NumLessThan("1", "1")).Value);
    }

    [Theory]
    [InlineData("-2.5", "-1")]
    [InlineData("0.00", "0")]
    [InlineData("3", "1")]
    public void TheSignIsTextForPrinting(string value, string expected)
    {
        Assert.Equal(expected, Text(In("es-AR", "UTC").NumSign(value)));
    }

    [Fact]
    public void ANumberWithoutAnExactDecimalIsRefused()
    {
        var refusal = Assert.IsType<PresentationResult.Refused>(In("es-AR", "UTC").FormatNumber("1234567890123456789012345678901", "N2"));

        Assert.Equal(PresentationRefusal.NoDecimal, refusal.Reason);
        Assert.Equal(PresentationRefusal.NoDecimal, Assert.IsType<PresentationResult.Refused>(In("es-AR", "UTC").NumSign("abc")).Reason);
        Assert.Equal(PresentationRefusal.NoDecimal, Assert.IsType<PresentationResult.Refused>(In("es-AR", "UTC").NumGreaterThan("abc", "0")).Reason);
    }

    [Fact]
    public void AValueThatIsNotADateIsRefused()
    {
        var refusal = Assert.IsType<PresentationResult.Refused>(In("es-AR", "UTC").FormatDate("29/10/2026", "dd/MM/yyyy"));

        Assert.Equal(PresentationRefusal.NotADate, refusal.Reason);
    }

    [Fact]
    public void AMissingComparisonOperandIsRefused()
    {
        var refusal = Assert.IsType<PresentationResult.Refused>(In("es-AR", "UTC").NumGreaterThan(null, "0"));

        Assert.Equal(PresentationRefusal.NotADate, refusal.Reason);
    }

    [Fact]
    public void AFormatTheRulesRejectIsRefusedBeforeTheValueIsRead()
    {
        var refusal = Assert.IsType<PresentationResult.Refused>(In("es-AR", "UTC").FormatNumber("not a number", "abc"));

        Assert.Equal(PresentationRefusal.BadFormat, refusal.Reason);
        Assert.Equal(PresentationRefusal.BadFormat, Assert.IsType<PresentationResult.Refused>(In("es-AR", "UTC").FormatDate("2026-10-29", "")).Reason);
    }

    private static PresentationFunctions In(string culture, string zone) =>
        new(new PresentationSettings(CultureInfo.GetCultureInfo(culture), TimeZoneInfo.FindSystemTimeZoneById(zone), Clock));

    private static string Text(PresentationResult result) => Assert.IsType<PresentationResult.Text>(result).Value;
}
