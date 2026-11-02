using ReportService.Workers.Orchestration.Uploads;

namespace ReportService.Workers.Orchestration.Tests;

/// <summary>
/// The settings of the expiry sweep: how long the worker waits between two rounds and
/// how many uploads one round expires. Both are required and bounded, and a refusal
/// names the setting and the rule and never the value.
/// </summary>
public sealed class UploadExpiryOptionsTests
{
    [Fact]
    public void AShortIntervalAndASmallBatchAreAccepted()
    {
        var options = new UploadExpiryOptions { SweepInterval = TimeSpan.FromSeconds(1), BatchSize = 1 };

        Assert.Empty(options.Validate());
    }

    [Fact]
    public void AnIntervalOfOneHourAndABatchOfFiveThousandAreAccepted()
    {
        var options = new UploadExpiryOptions { SweepInterval = TimeSpan.FromHours(1), BatchSize = 5000 };

        Assert.Empty(options.Validate());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void AnIntervalBelowOneSecondIsRefused(int seconds)
    {
        var options = new UploadExpiryOptions { SweepInterval = TimeSpan.FromSeconds(seconds), BatchSize = 500 };

        var problem = Assert.Single(options.Validate());

        Assert.Equal(UploadExpiryOptions.SweepIntervalSetting, problem.Setting);
        Assert.Contains("at least one second", problem.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain(seconds.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".", problem.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void AnIntervalOverOneHourIsRefused()
    {
        var options = new UploadExpiryOptions
        {
            SweepInterval = UploadExpiryOptions.MaximumSweepInterval.Add(TimeSpan.FromTicks(1)),
            BatchSize = 500,
        };

        var problem = Assert.Single(options.Validate());

        Assert.Equal(UploadExpiryOptions.SweepIntervalSetting, problem.Setting);
        Assert.Contains("one hour or less", problem.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(5001)]
    public void ABatchOutsideItsBoundsIsRefused(int batchSize)
    {
        var options = new UploadExpiryOptions { SweepInterval = TimeSpan.FromMinutes(1), BatchSize = batchSize };

        var problem = Assert.Single(options.Validate());

        Assert.Equal(UploadExpiryOptions.BatchSizeSetting, problem.Setting);
        Assert.Contains("between 1 and 5000", problem.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void BothSettingsMissingAreReportedTogether()
    {
        Assert.Equal(2, new UploadExpiryOptions().Validate().Count());
    }
}
