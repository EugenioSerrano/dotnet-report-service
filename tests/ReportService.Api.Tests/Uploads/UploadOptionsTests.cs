using ReportService.Api.Uploads;

namespace ReportService.Api.Tests.Uploads;

/// <summary>
/// The three settings of an upload as validated configuration: the session lifetime,
/// the stored-size limit and the verification deadline. Each is required, each has
/// its own bound, the problem names the setting and the rule and never the value the
/// operator wrote, and all problems are reported together.
/// </summary>
public sealed class UploadOptionsTests
{
    private static UploadOptions Valid() => new()
    {
        SessionLifetime = TimeSpan.FromHours(1),
        MaxStoredBytes = 1,
        VerificationDeadline = TimeSpan.FromHours(6),
    };

    [Theory]
    [InlineData(1)]
    [InlineData(3600)]
    [InlineData(86_400)]
    [InlineData(604_800)]
    public void ALifetimeInRangeHasNoProblem(int seconds)
    {
        var options = Valid();
        options.SessionLifetime = TimeSpan.FromSeconds(seconds);

        Assert.Empty(options.Validate());
    }

    [Fact]
    public void AMissingLifetimeLimitAndDeadlineAreAllRequired()
    {
        var problems = new UploadOptions().Validate().ToList();

        Assert.Equal(3, problems.Count);
        Assert.Equal(UploadOptions.SessionLifetimeSetting, problems[0].Setting);
        Assert.Contains("required", problems[0].Reason, StringComparison.Ordinal);
        Assert.Equal(UploadOptions.MaxStoredBytesSetting, problems[1].Setting);
        Assert.Contains("required", problems[1].Reason, StringComparison.Ordinal);
        Assert.Equal(UploadOptions.VerificationDeadlineSetting, problems[2].Setting);
        Assert.Contains("required", problems[2].Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void AZeroOrNegativeLifetimeIsRefused(int seconds)
    {
        var options = Valid();
        options.SessionLifetime = TimeSpan.FromSeconds(seconds);

        var problem = Assert.Single(options.Validate());

        Assert.Equal(UploadOptions.SessionLifetimeSetting, problem.Setting);
        Assert.Contains("greater than zero", problem.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("-", problem.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void OneTickOverSevenDaysIsRefused()
    {
        var options = Valid();
        options.SessionLifetime = UploadOptions.MaximumSessionLifetime.Add(TimeSpan.FromTicks(1));

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
        var options = Valid();
        options.MaxStoredBytes = limit;

        var problem = Assert.Single(options.Validate());

        Assert.Equal(UploadOptions.MaxStoredBytesSetting, problem.Setting);
        Assert.Contains("greater than zero", problem.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain(limit.ToString(System.Globalization.CultureInfo.InvariantCulture), problem.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ALimitOfOneByteIsAccepted()
    {
        Assert.Empty(Valid().Validate());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(21_600)]
    [InlineData(604_800)]
    public void ADeadlineInRangeHasNoProblem(int seconds)
    {
        var options = Valid();
        options.VerificationDeadline = TimeSpan.FromSeconds(seconds);

        Assert.Empty(options.Validate());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void AZeroOrNegativeDeadlineIsRefused(int seconds)
    {
        var options = Valid();
        options.VerificationDeadline = TimeSpan.FromSeconds(seconds);

        var problem = Assert.Single(options.Validate());

        Assert.Equal(UploadOptions.VerificationDeadlineSetting, problem.Setting);
        Assert.Contains("greater than zero", problem.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("-", problem.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ADeadlineOneTickOverSevenDaysIsRefused()
    {
        var options = Valid();
        options.VerificationDeadline = UploadOptions.MaximumVerificationDeadline.Add(TimeSpan.FromTicks(1));

        var problem = Assert.Single(options.Validate());

        Assert.Equal(UploadOptions.VerificationDeadlineSetting, problem.Setting);
        Assert.Contains("seven days", problem.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("7.00:00:00", problem.ToString(), StringComparison.Ordinal);
    }
}
