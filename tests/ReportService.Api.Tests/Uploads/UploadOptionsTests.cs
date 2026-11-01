using ReportService.Api.Uploads;

namespace ReportService.Api.Tests.Uploads;

/// <summary>
/// The session lifetime as validated configuration: required, longer than zero
/// and no longer than seven days, with the problem naming the setting and the
/// rule and never the value the operator wrote.
/// </summary>
public sealed class UploadOptionsTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(3600)]
    [InlineData(86_400)]
    [InlineData(604_800)]
    public void ALifetimeInRangeHasNoProblem(int seconds)
    {
        var options = new UploadOptions { SessionLifetime = TimeSpan.FromSeconds(seconds) };

        Assert.Empty(options.Validate());
    }

    [Fact]
    public void AMissingLifetimeIsRequired()
    {
        var problem = Assert.Single(new UploadOptions().Validate());

        Assert.Equal(UploadOptions.SessionLifetimeSetting, problem.Setting);
        Assert.Contains("required", problem.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void AZeroOrNegativeLifetimeIsRefused(int seconds)
    {
        var options = new UploadOptions { SessionLifetime = TimeSpan.FromSeconds(seconds) };

        var problem = Assert.Single(options.Validate());

        Assert.Equal(UploadOptions.SessionLifetimeSetting, problem.Setting);
        Assert.Contains("greater than zero", problem.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("-", problem.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void OneTickOverSevenDaysIsRefused()
    {
        var options = new UploadOptions { SessionLifetime = UploadOptions.MaximumSessionLifetime.Add(TimeSpan.FromTicks(1)) };

        var problem = Assert.Single(options.Validate());

        Assert.Equal(UploadOptions.SessionLifetimeSetting, problem.Setting);
        Assert.Contains("seven days", problem.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("7.00:00:00", problem.ToString(), StringComparison.Ordinal);
    }
}
