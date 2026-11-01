using ReportService.Api.Uploads;
using ReportService.Uploads;

namespace ReportService.Api.Tests.Uploads;

/// <summary>
/// The session lifetime and the size limit as validated configuration: each is
/// required, the lifetime is longer than zero and no longer than seven days and the
/// limit is greater than zero, with the problem naming the setting and the rule and
/// never the value the operator wrote.
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
        var options = new UploadOptions { SessionLifetime = TimeSpan.FromSeconds(seconds), MaxStoredBytes = 1 };

        Assert.Empty(options.Validate());
    }

    [Fact]
    public void AMissingLifetimeAndLimitAreBothRequired()
    {
        var problems = new UploadOptions().Validate().ToList();

        Assert.Equal(2, problems.Count);
        Assert.Equal(UploadOptions.SessionLifetimeSetting, problems[0].Setting);
        Assert.Contains("required", problems[0].Reason, StringComparison.Ordinal);
        Assert.Equal(UploadOptions.MaxStoredBytesSetting, problems[1].Setting);
        Assert.Contains("required", problems[1].Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void AZeroOrNegativeLifetimeIsRefused(int seconds)
    {
        var options = new UploadOptions { SessionLifetime = TimeSpan.FromSeconds(seconds), MaxStoredBytes = 1 };

        var problem = Assert.Single(options.Validate());

        Assert.Equal(UploadOptions.SessionLifetimeSetting, problem.Setting);
        Assert.Contains("greater than zero", problem.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("-", problem.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void OneTickOverSevenDaysIsRefused()
    {
        var options = new UploadOptions
        {
            SessionLifetime = UploadOptions.MaximumSessionLifetime.Add(TimeSpan.FromTicks(1)),
            MaxStoredBytes = 1,
        };

        var problem = Assert.Single(options.Validate());

        Assert.Equal(UploadOptions.SessionLifetimeSetting, problem.Setting);
        Assert.Contains("seven days", problem.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("7.00:00:00", problem.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void AZeroOrNegativeLimitIsRefused(long limit)
    {
        var options = new UploadOptions { SessionLifetime = TimeSpan.FromHours(1), MaxStoredBytes = limit };

        var problem = Assert.Single(options.Validate());

        Assert.Equal(UploadOptions.MaxStoredBytesSetting, problem.Setting);
        Assert.Contains("greater than zero", problem.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain(limit.ToString(System.Globalization.CultureInfo.InvariantCulture), problem.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ALimitOfOneByteIsAccepted()
    {
        var options = new UploadOptions { SessionLifetime = TimeSpan.FromHours(1), MaxStoredBytes = 1 };

        Assert.Empty(options.Validate());
    }
}
