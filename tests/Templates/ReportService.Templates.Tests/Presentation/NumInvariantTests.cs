using System.Globalization;
using System.Numerics;

namespace ReportService.Templates.Tests.Presentation;

/// <summary>
/// The culture-invariant number writer: the same text in every culture, a decimal point and
/// no group separator or exponent, rounding half away from zero when decimals are asked for,
/// an engine's own number accepted, and a culture-formatted text refused rather than read as
/// another number.
/// </summary>
public sealed class NumInvariantTests
{
    private static readonly DateTimeOffset Clock = new(2026, 11, 2, 2, 21, 56, TimeSpan.Zero);

    [Theory]
    [InlineData("41.82", "41.82")]
    [InlineData("-0.50", "-0.50")]
    [InlineData("1000.00", "1000.00")]
    [InlineData("9007199254740994", "9007199254740994")]
    [InlineData("0", "0")]
    [InlineData("-0", "0")]
    [InlineData("-0.00", "0.00")]
    public void PlainTextIsWrittenWithItsOwnScaleInEveryCulture(string value, string expected)
    {
        Assert.Equal(expected, Text(In("es-AR").NumInvariant(value, null)));
        Assert.Equal(expected, Text(In("en-US").NumInvariant(value, null)));
        Assert.Equal(expected, Text(In("de-DE").NumInvariant(value, null)));
    }

    [Theory]
    [InlineData("2.345", 2, "2.35")]
    [InlineData("-2.345", 2, "-2.35")]
    [InlineData("2.344", 2, "2.34")]
    [InlineData("2", 2, "2.00")]
    [InlineData("0.5", 0, "1")]
    [InlineData("-0.5", 0, "-1")]
    [InlineData("-0.004", 2, "0.00")]
    [InlineData("1234567.891", 1, "1234567.9")]
    public void DecimalsRoundHalfAwayFromZero(string value, int decimals, string expected)
    {
        Assert.Equal(expected, Text(In("es-AR").NumInvariant(value, decimals)));
        Assert.Equal(expected, Text(In("es-AR").NumInvariant(value, decimals.ToString(CultureInfo.InvariantCulture))));
    }

    [Fact]
    public void AnEnginesOwnNumberIsAccepted()
    {
        var functions = In("es-AR");
        Assert.Equal("41.82", Text(functions.NumInvariant(41.82m, null)));
        Assert.Equal("0.25", Text(functions.NumInvariant(0.25d, null)));
        Assert.Equal("0.3333333333333333", Text(functions.NumInvariant(1d / 3d, null)));
        Assert.Equal("41.82", Text(functions.NumInvariant(41.82f, 2)));
        Assert.Equal("3", Text(functions.NumInvariant(3, null)));
        Assert.Equal("-3", Text(functions.NumInvariant(-3L, null)));
        Assert.Equal("7", Text(functions.NumInvariant((byte)7, null)));
        Assert.Equal("18446744073709551615", Text(functions.NumInvariant(ulong.MaxValue, null)));
        Assert.Equal("12345678901234567890", Text(functions.NumInvariant(BigInteger.Parse("12345678901234567890", CultureInfo.InvariantCulture), null)));
        Assert.Equal("1.50", Text(functions.NumInvariant(1.5d, 2L)));
        Assert.Equal("0.00001", Text(functions.NumInvariant(1e-5, null)));
    }

    [Theory]
    [InlineData("41,82")]
    [InlineData("1,000")]
    [InlineData("1,000.50")]
    [InlineData("")]
    [InlineData(" 1")]
    [InlineData("1e3")]
    [InlineData("+1")]
    [InlineData("1.")]
    [InlineData(".5")]
    [InlineData("1.2.3")]
    [InlineData("1-")]
    [InlineData("--1")]
    [InlineData("-")]
    [InlineData("abc")]
    public void TextThatIsNotAPlainInvariantDecimalIsRefused(string value)
    {
        Assert.Equal(PresentationRefusal.NoDecimal, Refusal(In("es-AR").NumInvariant(value, null)));
    }

    [Fact]
    public void ValuesThatAreNotNumbersAreRefused()
    {
        var functions = In("es-AR");
        Assert.Equal(PresentationRefusal.NoDecimal, Refusal(functions.NumInvariant(null, null)));
        Assert.Equal(PresentationRefusal.NoDecimal, Refusal(functions.NumInvariant(true, null)));
        Assert.Equal(PresentationRefusal.NoDecimal, Refusal(functions.NumInvariant(double.PositiveInfinity, null)));
        Assert.Equal(PresentationRefusal.NoDecimal, Refusal(functions.NumInvariant(double.NaN, null)));
        Assert.Equal(PresentationRefusal.NoDecimal, Refusal(functions.NumInvariant(1e300, null)));
        Assert.Equal(PresentationRefusal.NoDecimal, Refusal(functions.NumInvariant(BigInteger.Pow(10, 40), null)));
        Assert.Equal(PresentationRefusal.NoDecimal, Refusal(functions.NumInvariant(new object(), null)));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(29)]
    [InlineData("x")]
    [InlineData("")]
    [InlineData("-1")]
    [InlineData("100")]
    [InlineData(1.5)]
    [InlineData(true)]
    public void DecimalsOutsideZeroToTwentyEightAreAFormatRefusal(object decimals)
    {
        Assert.Equal(PresentationRefusal.BadFormat, Refusal(In("es-AR").NumInvariant("1.5", decimals)));
    }

    [Fact]
    public void TheTableTakesOneOrTwoArgumentsAndNoOtherCount()
    {
        var table = PresentationFunctionTable.For(new PresentationSettings(CultureInfo.GetCultureInfo("es-AR"), TimeZoneInfo.Utc, Clock));
        var function = table[PresentationFunctionTable.NumInvariantName];

        Assert.Equal("41.82", Text(function(["41.82"])));
        Assert.Equal("41.8", Text(function(["41.82", "1"])));
        Assert.Equal(PresentationRefusal.BadFormat, Refusal(function([])));
        Assert.Equal(PresentationRefusal.BadFormat, Refusal(function(["1", "1", "1"])));
    }

    private static PresentationFunctions In(string culture) =>
        new(new PresentationSettings(CultureInfo.GetCultureInfo(culture), TimeZoneInfo.Utc, Clock));

    private static string Text(PresentationResult result) => Assert.IsType<PresentationResult.Text>(result).Value;

    private static PresentationRefusal Refusal(PresentationResult result) => Assert.IsType<PresentationResult.Refused>(result).Reason;
}
