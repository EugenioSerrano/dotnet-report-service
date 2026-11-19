namespace ReportService.Templates.Tests.Presentation;

/// <summary>
/// The number a template receives: the invariant text of an exact decimal, and nothing
/// else. Whitespace, an exponent, an overflow or a separator the context never writes is
/// not a decimal.
/// </summary>
public sealed class NumberTextTests
{
    [Theory]
    [InlineData("1000.00", "1000.00")]
    [InlineData("1000", "1000")]
    [InlineData("-0.50", "-0.50")]
    [InlineData("9007199254740994", "9007199254740994")]
    [InlineData("12345678901234567890123456789", "12345678901234567890123456789")]
    [InlineData("0", "0")]
    public void AnExactDecimalIsRead(string text, string expected)
    {
        Assert.True(NumberText.TryParseDecimal(text, out var value));
        Assert.Equal(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), value);
    }

    [Theory]
    [InlineData("1234567890123456789012345678901")]
    [InlineData("1e3")]
    [InlineData("1 000,00")]
    [InlineData("1\u2009000,00")]
    [InlineData(" 12")]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("1.2.3")]
    public void EverythingElseIsNotADecimal(string text)
    {
        Assert.False(NumberText.TryParseDecimal(text, out _));
    }

    [Fact]
    public void ANullValueIsNotADecimal()
    {
        Assert.False(NumberText.TryParseDecimal(null, out _));
    }
}
