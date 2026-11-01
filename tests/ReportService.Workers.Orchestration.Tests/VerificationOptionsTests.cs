using ReportService.Workers.Orchestration.Uploads;

namespace ReportService.Workers.Orchestration.Tests;

/// <summary>
/// The size limits verification enforces: both are required and must be greater
/// than zero, and a refusal names the setting and the rule and never the value.
/// </summary>
public sealed class VerificationOptionsTests
{
    [Fact]
    public void APositiveLimitOfEachKindIsValid()
    {
        var options = new VerificationOptions { MaxStoredBytes = 1, MaxUncompressedBytes = 1 };

        Assert.Empty(options.Validate());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void AStoredLimitThatIsNotPositiveIsRefusedByName(long value)
    {
        var options = new VerificationOptions { MaxStoredBytes = value, MaxUncompressedBytes = 1024 };

        var problem = Assert.Single(options.Validate());
        Assert.Equal("MaxStoredBytes", problem.Setting);
        Assert.DoesNotContain(value.ToString(), problem.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ADecompressedLimitThatIsNotPositiveIsRefusedByName(long value)
    {
        var options = new VerificationOptions { MaxStoredBytes = 1024, MaxUncompressedBytes = value };

        var problem = Assert.Single(options.Validate());
        Assert.Equal("MaxUncompressedBytes", problem.Setting);
        Assert.DoesNotContain(value.ToString(), problem.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void BothLimitsMissingAreReportedTogether()
    {
        var options = new VerificationOptions();

        Assert.Equal(2, options.Validate().Count());
    }
}
