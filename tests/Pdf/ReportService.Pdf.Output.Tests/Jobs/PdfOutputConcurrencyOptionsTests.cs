using ReportService.ServiceDefaults;
using ReportService.Workers.Orchestration.Jobs;

namespace ReportService.Workers.Orchestration.Tests.Jobs;

/// <summary>
/// The multi-block window setting: two renders by default, a configured value
/// bounded, and a value below one refused at start rather than silently replaced.
/// </summary>
public sealed class PdfOutputConcurrencyOptionsTests
{
    [Fact]
    public void TheDefaultIsTwo() => Assert.Equal(2, PdfOutputConcurrencyOptions.Resolve(null));

    [Fact]
    public void AConfiguredWindowIsUsed() => Assert.Equal(5, PdfOutputConcurrencyOptions.Resolve(5));

    [Fact]
    public void TheWindowIsCappedAtTheBound() =>
        Assert.Equal(PdfOutputConcurrencyOptions.MaximumWindow, PdfOutputConcurrencyOptions.Resolve(10_000));

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public void AWindowBelowOneIsRefused(int configured)
    {
        Assert.Throws<ConfigurationError>(() => PdfOutputConcurrencyOptions.Resolve(configured));
        Assert.NotEmpty(PdfOutputConcurrencyOptions.Validate(configured));
    }

    [Fact]
    public void AnAbsentWindowHasNoProblem() => Assert.Empty(PdfOutputConcurrencyOptions.Validate(null));
}
