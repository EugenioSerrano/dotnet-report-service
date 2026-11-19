namespace ReportService.Templates.Tests.Presentation;

/// <summary>
/// The format strings a template may ask for: each admitted family, the 32-character
/// bound, and the patterns the runtime rejects. Validation runs before a value is
/// formatted, so a format is refused by its own defect.
/// </summary>
public sealed class FormatRulesTests
{
    [Theory]
    [InlineData("N2")]
    [InlineData("C2")]
    [InlineData("F0")]
    [InlineData("P1")]
    [InlineData("E2")]
    [InlineData("#,##0.00")]
    [InlineData("0")]
    public void ANumericFormatTheFamilyAdmitsIsAccepted(string format)
    {
        Assert.True(FormatRules.TryValidate(format, FormatFamily.Numeric, out var reason));
        Assert.Null(reason);
    }

    [Theory]
    [InlineData("d")]
    [InlineData("D")]
    [InlineData("dd/MM/yyyy")]
    [InlineData("dd MMM yyyy HH:mm")]
    [InlineData("yyyy-MM-dd'T'HH:mm:ss")]
    public void ADateFormatTheFamilyAdmitsIsAccepted(string format)
    {
        Assert.True(FormatRules.TryValidate(format, FormatFamily.DateTime, out var reason));
        Assert.Null(reason);
    }

    [Theory]
    [InlineData("dddd, dd MMMM yyyy HH:mm:ss.fffff")]
    [InlineData("123456789012345678901234567890123")]
    public void AFormatLongerThanTheBoundIsRefused(string format)
    {
        Assert.False(FormatRules.TryValidate(format, FormatFamily.DateTime, out var reason));
        Assert.Equal(FormatRules.RefusalReason, reason);
    }

    [Theory]
    [InlineData("abc", FormatFamily.Numeric)]
    [InlineData("abc", FormatFamily.DateTime)]
    [InlineData("dd'", FormatFamily.DateTime)]
    [InlineData("D2", FormatFamily.Numeric)]
    [InlineData("", FormatFamily.Numeric)]
    [InlineData(null, FormatFamily.DateTime)]
    public void AFormatTheRulesRejectIsRefused(string? format, FormatFamily family)
    {
        Assert.False(FormatRules.TryValidate(format, family, out var reason));
        Assert.Equal(FormatRules.RefusalReason, reason);
    }

    [Fact]
    public void TheBoundIsInclusive()
    {
        var format = new string('0', FormatRules.MaximumLength);

        Assert.True(FormatRules.TryValidate(format, FormatFamily.Numeric, out _));
        Assert.False(FormatRules.TryValidate(format + "0", FormatFamily.Numeric, out _));
    }
}
